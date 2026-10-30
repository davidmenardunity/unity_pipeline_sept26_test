using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pipeline.Client;

// The Animations tab: every animation clip in a workbench revision (.anim files, and the clips an .fbx
// imports), whether it's Humanoid, and the AnimationViewer_WebGL player that previews them (clips come from
// Pipeline-built content archives, like the level editor's assets).
// A clip that isn't Humanoid can get a model generated for it with Unity AI (the local `unity ai` CLI). The
// open Unity Editor imports it under Assets/AnimationViewer/Generated and packs it into a WebGL content
// archive (Builds/AnimationViewer_Generated) that the player loads from this server.
static class AnimationEndpoints
{
    const string PlayerFolder = "AnimationViewer_WebGL";
    const string GeneratedFolder = "AnimationViewer_Generated";
    // Text to 3D, as FBX (the Editor imports FBX without a package). Low poly: it's a preview.
    const string ModelId = "model3d-meshy-7-1-text-to-3d";
    static readonly string[] ModelParams = ["targetFormat=fbx", "target_polycount=8000", "enable_pbr=false"];

    static readonly ConcurrentDictionary<string, Task<List<Clip>>> Lists = new();
    static readonly SemaphoreSlim Generating = new(1, 1);

    public static void MapAnimations(this WebApplication app, RouteGroupBuilder api)
    {
        var project = UnityProject(app.Environment.ContentRootPath);

        // The player build, read on each request (no restart needed after building it).
        api.MapGet("/anim-player", () =>
            WebGlPlayer.Describe(WebGlPlayer.FindBuild(app.Environment.ContentRootPath, PlayerFolder, "PIPELINE_EXPLORER_ANIM_PLAYER"),
                "anim-player", PlayerFolder, PlayerFolder));
        app.MapGet("/anim-player/{**file}", (string file, HttpResponse response) =>
        {
            var dir = WebGlPlayer.FindBuild(app.Environment.ContentRootPath, PlayerFolder, "PIPELINE_EXPLORER_ANIM_PLAYER");
            var path = dir is null ? null : Path.GetFullPath(Path.Combine(dir, file));
            if (path is null || !path.StartsWith(dir + Path.DirectorySeparatorChar) || !File.Exists(path))
                return Results.NotFound();
            response.Headers.CacheControl = "no-cache";
            var name = Path.GetFileName(path);
            var encoding = name.EndsWith(".br") ? "br" : name.EndsWith(".gz") ? "gzip" : null;
            var inner = encoding is null ? name : name[..name.LastIndexOf('.')];
            if (encoding is not null) response.Headers.ContentEncoding = encoding;
            var type = inner.EndsWith(".js") ? "application/javascript" : inner.EndsWith(".wasm") ? "application/wasm"
                : inner.EndsWith(".html") ? "text/html" : "application/octet-stream";
            return Results.File(path, type);
        });

        // Every animation clip in the revision (cached per revision: a revision never changes).
        api.MapGet("/workbenches/{wb}/revisions/{rev}/animations", async (Pipelines p, string wb, string rev, bool? refresh, CancellationToken ct) =>
        {
            var key = $"{wb}|{rev}";
            if (refresh == true) Lists.TryRemove(key, out _);
            var task = Lists.GetOrAdd(key, _ => ListAsync(p.Default, wb, rev));
            try { return Results.Ok(new { clips = await task.WaitAsync(ct), generated = Generated(project) }); }
            catch { Lists.TryRemove(key, out _); throw; }
        });

        // What a model for a clip would cost (free): the quote's points, worst case and the org charged.
        api.MapPost("/animations/model-quote", async (ModelRequest body, CancellationToken ct) =>
        {
            var prompt = Prompt(body);
            var r = await RunAsync("unity", ["ai", "quote", "--model", ModelId, "--prompt", prompt, .. ModelArgs(), "--format", "json"], project, TimeSpan.FromMinutes(1), ct);
            var quoted = Events(r.Out).FirstOrDefault(e => (string?)e["event"] == "quoted");
            if (quoted is null) return Failed("couldn't get a quote", r);
            return Results.Ok(new { model = ModelId, prompt, pointsCost = (double?)quoted["pointsCost"], worstCase = (double?)quoted["worstCase"],
                org = (string?)quoted["org"], orgName = (string?)quoted["orgName"] });
        });

        // Generate a model for a clip (spends Unity AI points: the page only calls this after the user
        // accepted the quote), import it in the open Editor and pack it into a WebGL archive for the player.
        api.MapPost("/animations/model", async (ModelRequest body, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Clip) || body.AcceptedWorstCase is null)
                return Results.BadRequest(new { error = "clip and acceptedWorstCase are required" });
            if (!await Generating.WaitAsync(0, ct))
                return Results.Json(new { error = "another model is being generated; try again when it's done" }, statusCode: 409);
            try
            {
                var steps = new List<string>();
                var prompt = Prompt(body);
                var safe = Safe(body.Name ?? Path.GetFileNameWithoutExtension(body.Clip));
                var folder = $"Assets/AnimationViewer/Generated/{safe}";
                Directory.CreateDirectory(Path.Combine(project, folder));
                var model = $"{folder}/{safe}_Model.fbx";
                for (var n = 2; File.Exists(Path.Combine(project, model)); n++) model = $"{folder}/{safe}_Model_{n}.fbx";

                // The quote again: never spend more than the worst case the user accepted.
                var q = await RunAsync("unity", ["ai", "quote", "--model", ModelId, "--prompt", prompt, .. ModelArgs(), "--format", "json"], project, TimeSpan.FromMinutes(1), ct);
                var quoted = Events(q.Out).FirstOrDefault(e => (string?)e["event"] == "quoted");
                if (quoted is null) return Failed("couldn't get a quote", q);
                if ((double?)quoted["worstCase"] > body.AcceptedWorstCase)
                    return Results.Json(new { error = $"the quote went up to {quoted["worstCase"]} points (worst case); ask again" }, statusCode: 409);

                var g = await RunAsync("unity", ["ai", "generate", "--model", ModelId, "--prompt", prompt, .. ModelArgs(), "--out", model, "--yes", "--format", "json"],
                    project, TimeSpan.FromMinutes(20), CancellationToken.None);   // once submitted, it's charged: don't abandon it
                var events = Events(g.Out);
                if (!File.Exists(Path.Combine(project, model)))
                    return Failed("the generation didn't produce a model", g);
                var cost = events.Select(e => (double?)e["pointsCost"]).LastOrDefault(c => c is not null);
                steps.Add($"generated {model} with {ModelId}{(cost is null ? "" : $" ({cost} points)")}");

                // The Editor imports it, makes a prefab sized for the preview, and builds its WebGL archive.
                var archiveName = $"{safe}_{Hash(body.Clip + "|" + model)}.ca";
                var archive = Path.Combine(project, "Builds", GeneratedFolder, archiveName).Replace('\\', '/');
                // run_script, not eval: eval gives up on main-thread work after 5 s. The CLI itself stops waiting
                // after 30 s while the Editor carries on, so the archive appearing is what says it's done.
                var script = Path.Combine(project, "Temp", $"AnimationViewerPack_{Hash(archive)}.cs");
                File.WriteAllText(script, "public static class AnimationViewerPack { public static string Run() => " +
                    $"Unity.Pipeline.AnimationViewer.Editor.AnimationViewerSetup.PackModel({Lit(model)}, {Lit(archive)}); }}");
                var e = await RunAsync("unity", ["command", "run_script", "--project-path", project.Replace('\\', '/'), "--file", script.Replace('\\', '/'),
                    "--entry", "AnimationViewerPack.Run", "--timeout_ms", "600000"], project, TimeSpan.FromMinutes(12), ct);
                for (var waited = 0; !File.Exists(archive) && !e.Out.Contains("\"success\":true") && waited < 600; waited += 2)
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                if (!File.Exists(archive))
                    return Failed($"the Editor couldn't pack {model} (is the Editor open on this project and idle?)", e);
                steps.Add($"packed {archiveName}");

                var entry = new JsonObject
                {
                    ["clip"] = body.Clip, ["name"] = body.Name, ["prompt"] = prompt, ["model"] = model,
                    ["prefab"] = model.Replace(".fbx", ".prefab"), ["archive"] = archiveName,
                    ["pointsCost"] = cost, ["org"] = (string?)quoted["orgName"], ["at"] = DateTime.UtcNow.ToString("o"),
                };
                File.WriteAllText(Path.Combine(project, "Builds", GeneratedFolder, Path.ChangeExtension(archiveName, ".json")), entry.ToJsonString());
                return Results.Ok(new { generated = entry, steps });
            }
            finally { Generating.Release(); }
        });

        // A generated model's archive, for the player.
        api.MapGet("/animations/archive/{name}", (string name) =>
        {
            var path = Path.Combine(project, "Builds", GeneratedFolder, Path.GetFileName(name));
            return name.EndsWith(".ca") && File.Exists(path) ? Results.File(path, "application/octet-stream") : Results.NotFound();
        });
    }

    // ── listing ────────────────────────────────────────────────────────────

    record Clip(string Path, string? Guid, string Name, string Source, string Rig, bool Humanoid, bool Legacy,
        double? Length, int? Frames, bool Loop, IReadOnlyList<string> Paths);

    static async Task<List<Clip>> ListAsync(PipelineClient c, string wb, string rev)
    {
        var files = new ConcurrentBag<string>();
        var gate = new SemaphoreSlim(8);
        async Task Walk(string folder)
        {
            FileTree tree;
            await gate.WaitAsync();
            try { tree = await c.GetFileTreeAsync(wb, rev, folder, CancellationToken.None); }
            finally { gate.Release(); }
            var subs = new List<Task>();
            foreach (var e in tree.Entries)
            {
                var path = e.Path.TrimStart('/');
                if (path == folder) continue;
                if (e.IsFolder) subs.Add(Walk(path));
                else if (path.EndsWith(".anim", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) files.Add(path);
            }
            await Task.WhenAll(subs);
        }
        await Walk("Assets");

        var clips = new ConcurrentBag<Clip>();
        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (path, ct) =>
        {
            var meta = await TextAsync(c, wb, rev, path + ".meta", ct);
            var guid = meta is null ? null : Regex.Match(meta, @"^guid: (\w+)", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value : null;
            if (path.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
            {
                if (await TextAsync(c, wb, rev, path, ct) is { } text) clips.Add(Anim(path, guid, text));
            }
            else if (meta is not null)
                foreach (var clip in Fbx(path, guid, meta)) clips.Add(clip);
        });
        return clips.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Name).ToList();
    }

    static async Task<string?> TextAsync(PipelineClient c, string wb, string rev, string path, CancellationToken ct)
    {
        try { return Encoding.UTF8.GetString(await c.GetFileAsync(wb, rev, path, null, ct)); }
        catch (PipelineApiException) { return null; }
    }

    // Humanoid clips animate the Animator's muscles and root (RootT, RootQ, "Left Arm Down-Up"…), not transforms.
    static readonly Regex HumanCurve = new(@"^\s*attribute: (RootT\.|RootQ\.|MotionT\.|MotionQ\.|(Left|Right)(Hand|Foot)[TQ]\.|.*(Front-Back|Left-Right|Down-Up|Twist In-Out|Twist Left-Right|In-Out|Stretch|Spread|Close))",
        RegexOptions.Multiline | RegexOptions.Compiled);

    static Clip Anim(string path, string? guid, string text)
    {
        string? Value(string key) => Regex.Match(text, $@"^\s*{key}: (.*)$", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value.Trim() : null;
        double Num(string key) => double.TryParse(Value(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        var humanoid = HumanCurve.IsMatch(text);
        var legacy = Value("m_Legacy") == "1";
        // The transforms it animates (generic clips): what a model for it needs, by name and hierarchy
        // (m_ClipBindingConstant lists them again as numeric hashes: skip those).
        var paths = Regex.Matches(text, @"^\s*path: (.+)$", RegexOptions.Multiline).Select(m => m.Groups[1].Value.Trim())
            .Where(x => x.Length > 0 && !x.All(char.IsDigit)).Distinct().Take(200).ToList();
        var length = Num("m_StopTime") - Num("m_StartTime");
        return new Clip(path, guid, Value("m_Name") ?? Path.GetFileNameWithoutExtension(path), "anim",
            humanoid ? "Humanoid" : legacy ? "Legacy" : "Generic", humanoid, legacy, length > 0 ? Math.Round(length, 3) : null, null,
            Value("m_LoopTime") == "1", paths);
    }

    // An .fbx's clips are listed in its ModelImporter settings (clipAnimations); animationType 3 is Humanoid.
    static IEnumerable<Clip> Fbx(string path, string? guid, string meta)
    {
        var type = Regex.Match(meta, @"^\s*animationType: (\d)", RegexOptions.Multiline) is { Success: true } t ? t.Groups[1].Value : "0";
        if (type == "0" || Regex.IsMatch(meta, @"^\s*importAnimation: 0", RegexOptions.Multiline)) yield break;
        var rig = type switch { "1" => "Legacy", "2" => "Generic", _ => "Humanoid" };
        var inBlock = false;
        string? name = null;
        int first = 0, last = 0;
        var loop = false;
        var found = new List<Clip>();
        void Add() { if (name is not null) found.Add(new Clip(path, guid, name, "fbx", rig, rig == "Humanoid", rig == "Legacy", null, Math.Max(0, last - first), loop, [])); }
        foreach (var line in meta.Split('\n'))
        {
            if (!inBlock) { if (line.TrimEnd() == "    clipAnimations:") inBlock = true; continue; }
            if (line.StartsWith("    - ")) { Add(); name = null; first = last = 0; loop = false; continue; }
            if (!line.StartsWith("      ")) break;
            var m = Regex.Match(line, @"^      (\w+): (.*)$");
            if (!m.Success) continue;
            var v = m.Groups[2].Value.Trim();
            switch (m.Groups[1].Value)
            {
                case "name": name = v; break;
                case "firstFrame": first = (int)double.Parse(v, CultureInfo.InvariantCulture); break;
                case "lastFrame": last = (int)double.Parse(v, CultureInfo.InvariantCulture); break;
                case "loopTime": loop = v == "1"; break;
            }
        }
        Add();
        foreach (var clip in found) yield return clip;
    }

    // Models generated so far, by clip (the sidecars next to their archives).
    static List<JsonNode> Generated(string project)
    {
        var dir = Path.Combine(project, "Builds", GeneratedFolder);
        if (!Directory.Exists(dir)) return [];
        return Directory.GetFiles(dir, "*.json").OrderBy(File.GetLastWriteTimeUtc)
            .Select(f => { try { return JsonNode.Parse(File.ReadAllText(f)); } catch { return null; } })
            .Where(n => n is not null && File.Exists(Path.Combine(dir, (string?)n["archive"] ?? "-"))).Select(n => n!).ToList();
    }

    // ── running the unity CLI ──────────────────────────────────────────────

    record Run(int Code, string Out, string Err);

    static async Task<Run> RunAsync(string exe, IEnumerable<string> args, string cwd, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["NO_COLOR"] = "1";
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"couldn't start {exe}");
        var stdout = proc.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = proc.StandardError.ReadToEndAsync(CancellationToken.None);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { proc.Kill(true); } catch { /* gone */ } throw new TimeoutException($"{exe} {string.Join(' ', args.Take(2))} took longer than {timeout.TotalMinutes} min"); }
        return new Run(proc.ExitCode, await stdout, await stderr);
    }

    // `unity ai … --format json` prints {"events": [...]}.
    static List<JsonNode> Events(string json)
    {
        try { return JsonNode.Parse(json)?["events"]?.AsArray().Where(e => e is not null).Select(e => e!).ToList() ?? []; }
        catch (JsonException) { return []; }
    }

    static IResult Failed(string what, Run r)
    {
        var error = Events(r.Out).FirstOrDefault(e => (string?)e["event"] == "error");
        var detail = error is null ? (r.Err.Trim().Length > 0 ? r.Err.Trim() : r.Out.Trim()) : $"{error["code"]}: {error["message"]}";
        if (detail.Length > 1500) detail = detail[..1500] + "…";
        return Results.Json(new { error = $"{what}: {detail}", code = (string?)error?["code"], traceId = (string?)error?["traceId"], jobId = (string?)error?["jobId"], exit = r.Code },
            statusCode: 502);
    }

    static IEnumerable<string> ModelArgs() => ModelParams.SelectMany(p => new[] { "--param", p });

    static string Prompt(ModelRequest body)
    {
        var prompt = (body.Prompt ?? "").Trim();
        if (prompt.Length == 0) prompt = $"a simple low-poly 3D model of {Regex.Replace(body.Name ?? "an object", @"[_\-]+", " ")}";
        return prompt.Length > 600 ? prompt[..600] : prompt;
    }

    static string Safe(string name) => Regex.Replace(name, @"[^A-Za-z0-9_\-]+", "_").Trim('_') is { Length: > 0 } s ? s : "Clip";
    static string Hash(string s) => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(s)))[..8].ToLowerInvariant();
    static string Lit(string s) => "\"" + s.Replace("\\", "/").Replace("\"", "\\\"") + "\"";

    // The Unity project this app sits in (Tools/ is next to Assets/).
    static string UnityProject(string contentRoot)
    {
        for (var dir = new DirectoryInfo(contentRoot); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "Assets")) && Directory.Exists(Path.Combine(dir.FullName, "ProjectSettings")))
                return dir.FullName;
        return contentRoot;
    }
}

record ModelRequest(string? Clip, string? Name, string? Prompt, double? AcceptedWorstCase);
