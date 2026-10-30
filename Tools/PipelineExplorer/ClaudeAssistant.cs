using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoAuthoring.Engine;

// "Ask Claude" in the co-authoring tab: runs Claude Code headless (`claude -p`, stream-json) under the sign-in it
// already has on this machine (no API key). Claude edits through the co-authoring MCP server
// (Tools/CoAuthoring/mcp/coauthor-mcp.mjs) as the participant "Claude", with the same commands everyone sends; its
// file, shell and web tools are off. Its progress goes to everyone as "assistant" events. A follow-up in the same
// scene continues the conversation.
sealed class ClaudeAssistant(string projectRoot)
{
    public const string Actor = "Claude";
    static readonly TimeSpan Limit = TimeSpan.FromMinutes(10);

    readonly ConcurrentDictionary<string, string> conversations = new();   // workbench|scene → Claude Code session id
    readonly ConcurrentDictionary<string, Process> running = new();         // workbench → the run

    /// <summary>Claude Code: CLAUDE_CODE_PATH, else on PATH, else the copy the Claude desktop app bundles.</summary>
    public static string? FindClaude()
    {
        if (Environment.GetEnvironmentVariable("CLAUDE_CODE_PATH") is { Length: > 0 } configured && File.Exists(configured)) return configured;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (var name in new[] { "claude.exe", "claude.cmd", "claude" })
                if (dir.Length > 0 && File.Exists(Path.Combine(dir, name))) return Path.Combine(dir, name);
        // %APPDATA%\Claude\claude-code\<version>\<build>\claude.exe
        var bundled = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude-code");
        if (!Directory.Exists(bundled)) return null;
        return Directory.EnumerateFiles(bundled, "claude.exe", SearchOption.AllDirectories)
            .OrderByDescending(p => Version.TryParse(Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(p))), out var v) ? v : new Version())
            .FirstOrDefault();
    }

    public async Task<JsonObject> AskAsync(Session session, string scene, string prompt, string askedBy, string serviceUrl, CancellationToken ct)
    {
        void Tell(string state, string? text = null) => session.Events.PublishTransient(new SessionEvent(session.Seq, "assistant", Actor,
            new JsonObject { ["state"] = state, ["text"] = text, ["prompt"] = prompt, ["askedBy"] = askedBy }, DateTimeOffset.UtcNow));

        var claude = FindClaude();
        if (claude == null)
            return new JsonObject { ["error"] = "Claude Code wasn't found on this machine: install it (or the Claude desktop app), or set CLAUDE_CODE_PATH, then restart the Explorer." };
        if (running.ContainsKey(session.Workbench))
            return new JsonObject { ["error"] = "Claude is already working on this workbench; wait for it to finish." };

        // Claude shows in the scene too: a camera above it, looking down.
        session.Presence.Report(Actor, "agent", new JsonObject
        {
            ["scene"] = scene, ["camera"] = new JsonObject { ["p"] = new JsonArray(0, 18, -18), ["q"] = new JsonArray(0.3826834, 0, 0, 0.9238795), ["fov"] = 60, ["aspect"] = 1.6 },
        });
        Tell("thinking", $"{askedBy} asked: {prompt}");

        var work = Path.Combine(Path.GetTempPath(), "coauthor-claude", session.Workbench);
        Directory.CreateDirectory(work);
        var mcpConfig = Path.Combine(work, "mcp.json");
        await File.WriteAllTextAsync(mcpConfig, new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["coauthor"] = new JsonObject
                {
                    ["command"] = "node",
                    ["args"] = new JsonArray(Path.Combine(projectRoot, "Tools", "CoAuthoring", "mcp", "coauthor-mcp.mjs")),
                    ["env"] = new JsonObject
                    {
                        ["COAUTHOR_URL"] = serviceUrl, ["COAUTHOR_WORKBENCH"] = session.Workbench,
                        ["COAUTHOR_SCENE"] = scene, ["COAUTHOR_ACTOR"] = Actor,
                    },
                },
            },
        }.ToJsonString(), ct);

        var psi = new ProcessStartInfo(claude)
        {
            WorkingDirectory = work, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
        };
        foreach (var a in new[]
                 {
                     "-p", $"Request from {askedBy} about the scene {scene}: {prompt}", "--output-format", "stream-json", "--verbose",
                     "--mcp-config", mcpConfig, "--strict-mcp-config", "--allowedTools", "mcp__coauthor",
                     "--disallowedTools", "Bash", "PowerShell", "Edit", "Write", "NotebookEdit", "WebFetch", "WebSearch", "Task", "Agent",
                     "--append-system-prompt", SystemPrompt,
                 })
            psi.ArgumentList.Add(a);
        var key = session.Workbench + "|" + scene;
        if (conversations.TryGetValue(key, out var conversation)) { psi.ArgumentList.Add("--resume"); psi.ArgumentList.Add(conversation); }

        Process process;
        try { process = Process.Start(psi) ?? throw new InvalidOperationException("Claude Code didn't start."); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Tell("done", $"Claude Code didn't start: {e.Message}");
            return new JsonObject { ["error"] = $"Claude Code didn't start: {e.Message}" };
        }
        process.StandardInput.Close();
        running[session.Workbench] = process;
        var commands = new JsonArray();
        string? answer = null;
        var failed = false;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(Limit);
            var stderr = process.StandardError.ReadToEndAsync(limit.Token);
            while (await process.StandardOutput.ReadLineAsync(limit.Token) is { } line)
            {
                if (line.Length == 0) continue;
                JsonNode? node;
                try { node = JsonNode.Parse(line); }
                catch (JsonException) { continue; }
                switch (node?["type"]?.GetValue<string>())
                {
                    case "system" when node["subtype"]?.GetValue<string>() == "init":
                        if (node["session_id"]?.GetValue<string>() is { } id) conversations[key] = id;
                        break;
                    case "assistant":
                        foreach (var item in node["message"]?["content"] as JsonArray ?? [])
                        {
                            var type = item?["type"]?.GetValue<string>();
                            if (type == "text" && item!["text"]?.GetValue<string>() is { Length: > 0 } text) Tell("says", text.Trim());
                            else if (type == "tool_use")
                            {
                                var name = (item!["name"]?.GetValue<string>() ?? "").Replace("mcp__coauthor__", "");
                                if (item["input"]?["commands"] is JsonArray cmds)
                                {
                                    foreach (var c in cmds) commands.Add(c?.DeepClone());
                                    Tell("running", string.Join("\n", cmds.Select(c => c is JsonValue v && v.TryGetValue<string>(out var s) ? s : c?["command"]?.ToString() ?? "")));
                                }
                                else Tell("running", $"{name} {item["input"]?.ToJsonString()}");
                            }
                        }
                        break;
                    case "result":
                        answer = node["result"]?.GetValue<string>();
                        failed = node["is_error"]?.GetValue<bool>() == true;
                        break;
                }
            }
            await process.WaitForExitAsync(limit.Token);
            var err = (await stderr).Trim();
            if (answer == null && process.ExitCode != 0) { failed = true; answer = err.Length > 0 ? err[..Math.Min(err.Length, 1000)] : $"Claude Code exited with {process.ExitCode}."; }
            if (failed && answer != null && (answer.Contains("authenticat", StringComparison.OrdinalIgnoreCase) || answer.Contains("log in", StringComparison.OrdinalIgnoreCase)))
                answer = $"Claude Code isn't signed in on this machine: run \"{claude}\" and sign in (/login), then ask again.";
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            failed = true;
            answer = "Claude took too long and was stopped.";
        }
        finally { running.TryRemove(session.Workbench, out _); }
        answer ??= "Done.";
        Tell("done", answer);
        return failed ? new JsonObject { ["error"] = answer, ["commands"] = commands } : new JsonObject { ["text"] = answer, ["commands"] = commands };
    }

    const string SystemPrompt = """
        You edit a Unity scene live, with other people and agents working in it at the same time. Change it only with
        the coauthor tools: unity_command sends Unity CLI commands (the pipeline package's), which run on a shared
        draft in order with everyone else's; find_assets finds asset paths. Nothing is committed until a person saves.
        - Look before you change: get_scene_hierarchy (or find_gameobjects) shows what's there and its paths.
        - Address objects by hierarchy path ("/Parent/Child"), or by a globalId from an earlier result.
        - Positions are metres, Y is up; rotations are Euler degrees; set_transform sets local values.
        - Built-in primitives: Cube, Sphere, Capsule, Cylinder, Plane, Quad. Place prefabs with
          instantiate_prefab --prefab "Assets/…/X.prefab" (find_assets with ".prefab" lists them).
        - Several commands in one call run in order; with transaction: true they apply all or nothing.
        - Others edit at the same time: if a command fails because something changed, look again and retry.
        - Do what was asked and no more; when done, say in one or two sentences what you changed.
        """;
}
