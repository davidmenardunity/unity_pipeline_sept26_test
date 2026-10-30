using System.Text;
using System.Text.Json.Nodes;
using CoAuthoring.Commands;
using CoAuthoring.Engine;
using Pipeline.Client;
using Unity.DataModel.Levels;

// Co-authoring (Tools/CoAuthoring): a local endpoint, not in Pipeline yet. People and agents send Unity CLI
// commands for a workbench; one queue per workbench resolves them in order on the Unity data model, against a
// draft kept on disk; Save collapses the draft into one commit on the workbench. Every route is under
// /api/coauthor/v1/workbenches/{wb}. Callers say who they are with X-Actor (and X-Actor-Kind: human | agent).
static class CoAuthoringEndpoints
{
    public static void MapCoAuthoring(this RouteGroupBuilder api, BranchOf branchOf, string projectRoot)
    {
        var catalog = Catalog.Builtin();
        // Custom commands: assemblies the Editor compiled that refer to Unity.DataModel (see Assets/CoAuthoringCommands).
        catalog.LoadCustom(Path.Combine(projectRoot, "Library", "ScriptAssemblies"));
        var runner = new Runner(catalog);
        Sessions? sessions = null;
        Sessions SessionsOf(Pipelines p) => sessions ??= new Sessions(new PipelineWorkbench(p, branchOf),
            Environment.GetEnvironmentVariable("COAUTHOR_DATA") is { Length: > 0 } d ? d : Path.Combine(projectRoot, "Library", "CoAuthoring"));

        var g = api.MapGroup("/coauthor/v1/workbenches/{wb}");

        g.MapGet("/commands", (string? detail) => Results.Ok(new
        {
            commands = catalog.All.OrderBy(c => c.Custom).ThenBy(c => c.Name).Select(c => c.Describe(detail != "compact")),
            notImplemented = catalog.NotImplemented,
            loadErrors = catalog.LoadErrors,
        }));

        g.MapPost("/commands", async (Pipelines p, string wb, HttpRequest request, CancellationToken ct) =>
        {
            var body = await JsonNode.ParseAsync(request.Body, cancellationToken: ct) as JsonObject;
            if (body == null) return Results.BadRequest(new { error = "the body must be {\"commands\": [\"create_gameobject --name Box\", …]}" });
            var (actor, kind) = Actor(request);
            var session = await SessionsOf(p).GetAsync(wb, ct);
            try
            {
                var answer = await runner.RunAsync(session, new Runner.Request(actor, kind, body["scene"]?.GetValue<string>(), body,
                    request.Headers["Idempotency-Key"].FirstOrDefault()), ct);
                return Results.Text(answer.ToJsonString(), "application/json");
            }
            catch (CommandException e) { return Results.BadRequest(new { error = e.Message, code = e.Code }); }
        });

        // Server-sent events: every change with what it changed, saves, discards, and presence.
        g.MapGet("/events", async (Pipelines p, string wb, long? since, HttpResponse response, CancellationToken ct) =>
        {
            var session = await SessionsOf(p).GetAsync(wb, ct);
            response.Headers.ContentType = "text/event-stream";
            response.Headers.CacheControl = "no-cache";
            await response.WriteAsync($"event: hello\ndata: {new JsonObject { ["seq"] = session.Seq, ["participants"] = session.Presence.List() }.ToJsonString()}\n\n", ct);
            await response.Body.FlushAsync(ct);
            using var keepAlive = new PeriodicTimer(TimeSpan.FromSeconds(15));
            var ping = Task.Run(async () =>
            {
                while (await keepAlive.WaitForNextTickAsync(ct)) { await response.WriteAsync(": ping\n\n", ct); await response.Body.FlushAsync(ct); }
            }, ct);
            try
            {
                await foreach (var e in session.Events.Subscribe(since, session.Seq, ct))
                {
                    await response.WriteAsync($"event: {e.Type}\ndata: {e.ToJson().ToJsonString()}\n\n", ct);
                    await response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) { /* the client left */ }
        });

        g.MapPut("/presence", async (Pipelines p, string wb, HttpRequest request, CancellationToken ct) =>
        {
            var state = await JsonNode.ParseAsync(request.Body, cancellationToken: ct) as JsonObject ?? new JsonObject();
            var (actor, kind) = Actor(request);
            var session = await SessionsOf(p).GetAsync(wb, ct);
            session.Presence.Report(actor, kind, state);
            return Results.Text(new JsonObject { ["participants"] = session.Presence.List() }.ToJsonString(), "application/json");
        });

        g.MapDelete("/presence", async (Pipelines p, string wb, HttpRequest request, CancellationToken ct) =>
        {
            (await SessionsOf(p).GetAsync(wb, ct)).Presence.Leave(Actor(request).Actor);
            return Results.NoContent();
        });

        g.MapGet("/draft", async (Pipelines p, string wb, CancellationToken ct) =>
            Results.Text((await SessionsOf(p).GetAsync(wb, ct)).Describe().ToJsonString(), "application/json"));

        // A scene as the draft has it: what it places and where (for the viewer).
        g.MapGet("/scene", async (Pipelines p, string wb, string path, CancellationToken ct) =>
        {
            var session = await SessionsOf(p).GetAsync(wb, ct);
            if (await session.SceneAsync(path, ct) is not { } snap) return Results.NotFound(new { error = $"No scene {path} in the workbench." });
            var (items, skipped) = new LevelScene(snap.Scene, snap.Prefabs).Placeables();
            return Results.Ok(new
            {
                path, seq = session.Seq, baseRevision = session.BaseRevision,
                items = items.Select(i => new
                {
                    id = i.Id.ToString(), kind = i.Kind == PlaceableKind.Instance ? "instance" : "object", name = i.Name,
                    assetGuid = i.AssetGuid, primitive = i.Primitive, hierarchyPath = snap.Scene.Hierarchy.PathOf(i.Id), world = PoseJson.From(i.World), parentWorld = PoseJson.From(i.ParentWorld),
                }),
                skipped = skipped.Select(s => new { id = s.Id.ToString(), reason = s.Reason }),
            });
        });

        // A scene's hierarchy as the draft has it: GameObjects and prefab instances, with their children.
        g.MapGet("/hierarchy", async (Pipelines p, string wb, string path, CancellationToken ct) =>
        {
            var session = await SessionsOf(p).GetAsync(wb, ct);
            if (await session.SceneFileAsync(path, ct) is not { } scene) return Results.NotFound(new { error = $"No scene {path} in the workbench." });
            var h = scene.Hierarchy;
            object Node(Unity.DataModel.HierarchyNode n) => new
            {
                id = n.Object.FileId.ToString(), name = n.Label, hierarchyPath = h.PathOf(n.Object.FileId),
                kind = n.Kind == Unity.DataModel.HierarchyNodeKind.PrefabInstance ? "instance" : "go",
                active = n.Object is not Unity.DataModel.GameObject go || go.IsActive,
                components = n.Object is Unity.DataModel.GameObject g2 ? h.ComponentsOf(g2).Select(c => c.TypeName) : null,
                children = n.Children.Select(Node),
            };
            return Results.Ok(new { path, seq = session.Seq, roots = h.Roots.Select(Node) });
        });

        // The project's files whose path contains q (e.g. "Barrel", ".prefab"): for agents finding assets.
        g.MapGet("/assets", async (Pipelines p, string wb, string? q, CancellationToken ct) =>
        {
            var session = await SessionsOf(p).GetAsync(wb, ct);
            var files = await ProjectFilesAsync(p.Default, wb, session.ReadRevision, ct);
            return Results.Ok(new { paths = files.Where(x => string.IsNullOrEmpty(q) || x.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(200) });
        });

        g.MapPost("/save", async (Pipelines p, string wb, HttpRequest request, CancellationToken ct) =>
        {
            var body = request.ContentLength > 0 ? await JsonNode.ParseAsync(request.Body, cancellationToken: ct) as JsonObject : null;
            var session = await SessionsOf(p).GetAsync(wb, ct);
            try { return Results.Text((await session.SaveAsync(Actor(request).Actor, body?["message"]?.GetValue<string>(), ct)).ToJsonString(), "application/json"); }
            catch (CommandException e) { return Results.Json(new { error = e.Message, code = e.Code, details = e.Details?.ToJsonString() }, statusCode: e.Code == ErrorCodes.Conflict ? 409 : 400); }
        });

        // Demo: pretend people who join a scene and edit it (DemoCrowd), until stopped or for `seconds`.
        var crowds = new System.Collections.Concurrent.ConcurrentDictionary<string, DemoCrowd>();
        g.MapPost("/demo", async (Pipelines p, string wb, HttpRequest request, CancellationToken ct) =>
        {
            var body = await JsonNode.ParseAsync(request.Body, cancellationToken: ct) as JsonObject ?? new JsonObject();
            var scene = body["scene"]?.GetValue<string>();
            if (string.IsNullOrEmpty(scene)) return Results.BadRequest(new { error = "open a scene first (scene is required)" });
            var session = await SessionsOf(p).GetAsync(wb, ct);
            var crowd = crowds.GetOrAdd(wb, _ => new DemoCrowd(session, runner));
            var count = body["actors"]?.GetValue<int>() ?? 5;
            var seconds = body["seconds"]?.GetValue<int>() ?? 90;
            var c = body["center"] as JsonArray;
            // The project's prefabs, for the pretend people to place.
            var prefabs = (await ProjectFilesAsync(p.Default, wb, session.ReadRevision, ct))
                .Where(x => x.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)).ToList();
            crowd.Start(scene, count, TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 900)),
                new DemoCrowd.Vector(c?[0]?.GetValue<double>() ?? 0, c?[1]?.GetValue<double>() ?? 0, c?[2]?.GetValue<double>() ?? 0), prefabs);
            return Results.Ok(new { running = true, actors = count, seconds, prefabs = prefabs.Count });
        });
        g.MapPost("/demo/stop", (string wb) =>
        {
            if (crowds.TryGetValue(wb, out var crowd)) crowd.Stop();
            return Results.Ok(new { running = false });
        });

        // Ask Claude: headless Claude Code makes the change with the same commands, as the participant "Claude" (ClaudeAssistant.cs).
        var assistant = new ClaudeAssistant(projectRoot);
        g.MapPost("/ask", async (Pipelines p, string wb, HttpRequest request, CancellationToken ct) =>
        {
            var body = await JsonNode.ParseAsync(request.Body, cancellationToken: ct) as JsonObject ?? new JsonObject();
            var prompt = body["prompt"]?.GetValue<string>()?.Trim();
            var scene = body["scene"]?.GetValue<string>();
            if (string.IsNullOrEmpty(prompt) || string.IsNullOrEmpty(scene)) return Results.BadRequest(new { error = "a prompt and an open scene are required" });
            var session = await SessionsOf(p).GetAsync(wb, ct);
            var serviceUrl = $"{request.Scheme}://{request.Host}";
            var answer = await assistant.AskAsync(session, scene, prompt, Actor(request).Actor, serviceUrl, ct);
            return Results.Text(answer.ToJsonString(), "application/json");
        });

        g.MapPost("/discard", async (Pipelines p, string wb, HttpRequest request, CancellationToken ct) =>
            Results.Text((await (await SessionsOf(p).GetAsync(wb, ct)).DiscardAsync(Actor(request).Actor, ct)).ToJsonString(), "application/json"));
    }

    public delegate Task<string> BranchOf(PipelineClient c, string wb, string? given, CancellationToken ct);

    // Every file under Assets/ at a revision (no .meta), from the workbench's file tree, kept per revision.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<List<string>>> ProjectFiles = new();

    static Task<List<string>> ProjectFilesAsync(PipelineClient c, string wb, string rev, CancellationToken ct) =>
        ProjectFiles.GetOrAdd($"{wb}|{rev}", _ => Task.Run(async () =>
        {
            var files = new List<string>();
            var folders = new Queue<string>(["Assets"]);
            for (var walked = 0; folders.Count > 0 && walked < 400; walked++)
            {
                var folder = folders.Dequeue();
                foreach (var e in (await c.GetFileTreeAsync(wb, rev, folder, CancellationToken.None)).Entries)
                    if (e.IsFolder) folders.Enqueue(e.RelativePath);
                    else if (!e.RelativePath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) files.Add(e.RelativePath);
            }
            return files;
        }));

    static (string Actor, string Kind) Actor(HttpRequest r)
    {
        var actor = r.Headers["X-Actor"].FirstOrDefault()?.Trim();
        var kind = r.Headers["X-Actor-Kind"].FirstOrDefault()?.Trim().ToLowerInvariant();
        return (string.IsNullOrEmpty(actor) ? "anonymous" : actor[..Math.Min(actor.Length, 40)], kind is "agent" ? "agent" : "human");
    }

    // The workbench through the Pipeline API.
    sealed class PipelineWorkbench(Pipelines pipelines, BranchOf branchOf) : IWorkbenchSource
    {
        PipelineClient C => pipelines.Default;

        // The revision reads can use: the validated one, else the head.
        public async Task<string> HeadAsync(string wb, CancellationToken ct)
        {
            var r = await C.GetReadinessAsync(wb, ct);
            return r.SettledRevision ?? r.Head ?? throw new InvalidOperationException($"Workbench {wb} has no revision yet.");
        }

        public async Task<string?> ReadTextAsync(string wb, string rev, string path, CancellationToken ct)
        {
            try { return Encoding.UTF8.GetString(await C.GetFileAsync(wb, rev, path, null, ct)).TrimStart('﻿'); }
            catch (PipelineApiException e) when (e.Status == 404) { return null; }
        }

        public async Task<string?> GuidOfAsync(string wb, string rev, string path, CancellationToken ct)
        {
            try { return await C.ResolveGuidAsync(wb, rev, path, null, ct); }
            catch (PipelineApiException e) when (e.Status == 404) { return null; }
        }

        // Right after the project service starts, the asset index answers 202 with nothing for a while: retry a little.
        public async Task<string?> PathOfAsync(string wb, string rev, string guid, CancellationToken ct)
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(1), ct);
                try
                {
                    if ((await C.GetAssetAsync(wb, rev, guid, ct)).Path is { Length: > 0 } p) return p.TrimStart('/');
                }
                catch (PipelineApiException e) when (e.Status == 404) { return null; }
            }
            return null;
        }

        public async Task<string?> FindScriptAsync(string wb, string rev, string typeName, CancellationToken ct)
        {
            var hits = await C.SearchAsync(wb, rev, typeName + ".cs", ct);
            return hits.FirstOrDefault(h => h.RelativePath.EndsWith("/" + typeName + ".cs", StringComparison.OrdinalIgnoreCase))?.AssetGuid;
        }

        public async Task<string> CommitAsync(string wb, string baseRevision, IReadOnlyList<(string Path, string Text)> files, string message, CancellationToken ct) =>
            await C.SaveFilesAsync(wb, baseRevision, await branchOf(C, wb, null, ct),
                files.Select(f => (f.Path, new UTF8Encoding(false).GetBytes(f.Text))).ToList(), message, ct);
    }
}
