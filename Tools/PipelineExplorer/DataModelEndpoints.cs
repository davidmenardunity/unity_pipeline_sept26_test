using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Pipeline.Client;
using Unity.DataModel;
using Unity.DataModel.Levels;
using Unity.DataModel.Yaml;

// The Inspector and Level editor tabs change files through the Unity data model (Tools/Unity.DataModel):
// the page sends what changed (a value, a moved object, an added prefab), this server reads the file at the
// revision the page opened, applies the change with the model, and commits the text it writes.
static class DataModelEndpoints
{
    public delegate Task<string> BranchOf(PipelineClient c, string wb, string? given, CancellationToken ct);

    public static void MapDataModel(this RouteGroupBuilder rev, BranchOf branchOf)
    {
        // A file's objects: every document with its fields (kind, text, property path), and the hierarchy.
        rev.MapGet("/model", async (Pipelines p, string wb, string rev, string path, CancellationToken ct) =>
        {
            var text = await Files.TextAsync(p.Default, wb, rev, path, ct);
            if (!UnityFile.IsYaml(text)) return NotYaml(path);
            var file = UnityFile.Parse(text);
            var h = file.Hierarchy;
            return Results.Ok(new
            {
                path,
                objects = file.Objects.Select(o => new
                {
                    fileId = Id(o.FileId), classId = o.ClassId, type = o.TypeName, stripped = o.IsStripped,
                    components = o is GameObject go ? h.ComponentsOf(go).Select(c => Id(c.FileId)) : null,
                    root = Node(o.Root),
                }),
                hierarchy = new { roots = h.Roots.Select(Tree), others = h.Others.Select(Tree) },
                text,
            });
        });

        // Change values in a file and commit it as a new revision (or, with preview, return the new text).
        rev.MapPost("/model/edit", async (Pipelines p, string wb, string rev, ModelEditRequest body, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Path) || body.Edits is not { Count: > 0 }) return Results.BadRequest(new { error = "a path and at least one edit are required" });
            var c = p.Default;
            var path = body.Path.Trim('/');
            var text = await Files.TextAsync(c, wb, rev, path, ct);
            if (!UnityFile.IsYaml(text)) return NotYaml(path);
            var file = UnityFile.Parse(text);
            foreach (var e in body.Edits)
            {
                var obj = long.TryParse(e.FileId, out var id) ? file.Find(id) : null;
                if (obj == null) return Results.BadRequest(new { error = $"{Path.GetFileName(path)} has no object &{e.FileId} at revision {rev}" });
                try
                {
                    switch (e.As)
                    {
                        case "number": obj.SetNumber(e.Path, e.Value ?? ""); break;
                        case "bool": obj.SetBool(e.Path, e.Value is "1" or "true"); break;
                        case "raw": obj.SetRaw(e.Path, e.Value ?? ""); break;
                        default: obj.SetString(e.Path, e.Value ?? ""); break;
                    }
                }
                catch (Exception ex) when (ex is KeyNotFoundException or FormatException or InvalidOperationException or ArgumentException)
                {
                    return Results.BadRequest(new { error = $"{obj.TypeName} &{e.FileId} {e.Path}: {ex.Message}" });
                }
            }
            var after = file.ToText();
            if (body.Preview) return Results.Ok(new { text = after, changes = file.Changes.Count });
            var revision = await c.SaveFilesAsync(wb, rev, await branchOf(c, wb, body.Branch, ct),
                [(path, Utf8.GetBytes(after))], Message(body.Message), ct);
            return Results.Ok(new { revision, paths = new[] { path }, changes = file.Changes.Count });
        });

        // ── level editor ──────────────────────────────────────────────────────

        // What a scene places, before its prefabs are read: counts, and the prefabs to read.
        rev.MapGet("/level", async (Pipelines p, string wb, string rev, string path, CancellationToken ct) =>
        {
            var text = await Files.TextAsync(p.Default, wb, rev, path, ct);
            if (!UnityFile.IsYaml(text)) return NotYaml(path);
            var scene = UnityFile.Parse(text);
            var level = new LevelScene(scene, new Dictionary<string, PrefabRoot?>());
            return Results.Ok(new { path, instances = level.Instances.Count, objects = level.MeshObjects.Count, prefabs = LevelScene.PrefabGuids(scene) });
        });

        // A prefab's root (by GUID or path): its path, name, and whether it can be placed.
        rev.MapGet("/level/prefab", async (Pipelines p, string wb, string rev, string? guid, string? path, CancellationToken ct) =>
        {
            var c = p.Default;
            if (string.IsNullOrEmpty(guid) && !string.IsNullOrEmpty(path)) guid = await c.ResolveGuidAsync(wb, rev, path.Trim('/'), null, ct);
            if (string.IsNullOrEmpty(guid)) return Results.Ok(new { guid, path, ok = false, reason = $"{path} has no GUID at revision {rev}" });
            var info = await Prefabs.ReadAsync(c, wb, rev, guid, ct);
            return Results.Ok(new { guid, path = info.Path, name = info.Root?.Name, ok = info.Root != null,
                reason = info.Root == null ? "it has no plain root (a prefab variant?), or isn't a prefab in this workbench" : null });
        });

        // Everything the scene places, with world poses.
        rev.MapGet("/level/placeables", async (Pipelines p, string wb, string rev, string path, CancellationToken ct) =>
        {
            var c = p.Default;
            var text = await Files.TextAsync(c, wb, rev, path, ct);
            if (!UnityFile.IsYaml(text)) return NotYaml(path);
            var level = await LevelAsync(c, wb, rev, UnityFile.Parse(text), [], ct);
            return Results.Ok(Placed(level));
        });

        // Move items and add prefab instances, commit the scene as a new revision, and return what it now places
        // (or, with preview, return the new text without committing).
        rev.MapPost("/level/save", async (Pipelines p, string wb, string rev, LevelSaveRequest body, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Path)) return Results.BadRequest(new { error = "a scene path is required" });
            var c = p.Default;
            var path = body.Path.Trim('/');
            var text = await Files.TextAsync(c, wb, rev, path, ct);
            if (!UnityFile.IsYaml(text)) return NotYaml(path);
            var added = body.Added ?? [];
            var level = await LevelAsync(c, wb, rev, UnityFile.Parse(text), added.Select(a => a.AssetGuid), ct);
            var items = level.Placeables().Items.ToDictionary(i => Id(i.Id));
            foreach (var m in body.Moved ?? [])
            {
                if (!items.TryGetValue(m.Id, out var item)) return Results.BadRequest(new { error = $"&{m.Id} isn't placed in {Path.GetFileName(path)} at revision {rev}" });
                level.Move(item, m.World.ToPose());
            }
            var ids = new Dictionary<string, string>();
            foreach (var a in added)
            {
                try { ids[a.Key] = Id(level.Add(new NewInstance(a.AssetGuid, a.Name, a.World.ToPose()))); }
                catch (InvalidOperationException ex) { return Results.BadRequest(new { error = $"{a.Name}: {ex.Message}" }); }
            }
            if (!level.File.HasChanges) return Results.BadRequest(new { error = "nothing changed" });
            var after = level.File.ToText();
            if (body.Preview) return Results.Ok(new { text = after, added = ids, changes = level.File.Changes.Count });
            var revision = await c.SaveFilesAsync(wb, rev, await branchOf(c, wb, body.Branch, ct),
                [(path, Utf8.GetBytes(after))], Message(body.Message), ct);
            var saved = new LevelScene(UnityFile.Parse(after), level.Prefabs);
            return Results.Ok(new { revision, added = ids, placed = Placed(saved) });
        });
    }

    static readonly UTF8Encoding Utf8 = new(false);
    static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
    static string? Message(string? m) => string.IsNullOrWhiteSpace(m) ? null : m.Trim();
    static IResult NotYaml(string path) => Results.BadRequest(new
        { error = $"{Path.GetFileName(path)} isn't saved as text (YAML). Set Asset Serialization to Force Text in the project to edit it here." });

    static async Task<LevelScene> LevelAsync(PipelineClient c, string wb, string rev, UnityFile scene, IEnumerable<string> more, CancellationToken ct)
    {
        var guids = LevelScene.PrefabGuids(scene).Concat(more).Distinct().ToList();
        var roots = new ConcurrentDictionary<string, PrefabRoot?>();
        await Parallel.ForEachAsync(guids, new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct },
            async (g, t) => roots[g] = (await Prefabs.ReadAsync(c, wb, rev, g, t)).Root);
        return new LevelScene(scene, roots);
    }

    static object Placed(LevelScene level)
    {
        var (items, skipped) = level.Placeables();
        return new
        {
            items = items.Select(i => new
            {
                id = Id(i.Id), kind = i.Kind == PlaceableKind.Instance ? "instance" : "object", name = i.Name,
                assetGuid = i.AssetGuid, primitive = i.Primitive, world = PoseJson.From(i.World),
            }),
            skipped = skipped.Select(s => new { id = Id(s.Id), reason = s.Reason }),
        };
    }

    static object Tree(HierarchyNode n) => new
    {
        fileId = Id(n.Object.FileId),
        kind = n.Kind switch { HierarchyNodeKind.GameObject => "go", HierarchyNodeKind.PrefabInstance => "instance", _ => "object" },
        label = n.Label,
        children = n.Children.Select(Tree),
    };

    static readonly Dictionary<YamlNodeKind, string> Kinds = new()
    {
        [YamlNodeKind.Scalar] = "scalar", [YamlNodeKind.Flow] = "flow", [YamlNodeKind.FlowSequence] = "flowseq",
        [YamlNodeKind.Sequence] = "seq", [YamlNodeKind.Map] = "map", [YamlNodeKind.Block] = "block",
    };

    // A field for the page: its key and property path, kind, text (raw, and as plain text), and what it holds.
    static object Node(YamlNode n)
    {
        var r = ObjectReference.From(n);
        return new
        {
            key = n.Key, path = n.Path, kind = Kinds[n.Kind], raw = n.Raw, value = n.Text, readOnly = n.ReadOnly, isItem = n.IsItem,
            line = n.Line,
            @ref = r is { } x ? new { fileId = Id(x.FileId), guid = x.Guid, type = x.Type } : null,
            items = n.Items?.Select(i => new { key = i.Key, path = i.Path, raw = i.Raw, value = i.Text }),
            children = n.Children.Count > 0 ? n.Children.Select(Node) : null,
        };
    }

    // Revisions don't change, so what's read at one is kept (a little of it).
    static class Files
    {
        static readonly ConcurrentDictionary<string, string> Cache = new();

        public static async Task<string> TextAsync(PipelineClient c, string wb, string rev, string path, CancellationToken ct)
        {
            path = path.Trim('/');
            var key = $"{wb}|{rev}|{path}";
            if (Cache.TryGetValue(key, out var text)) return text;
            var bytes = await c.GetFileAsync(wb, rev, path, null, ct);
            text = Utf8.GetString(bytes).TrimStart('﻿');
            if (Cache.Count > 200) Cache.Clear();
            Cache[key] = text;
            return text;
        }
    }

    static class Prefabs
    {
        public sealed record Info(string? Path, PrefabRoot? Root);
        static readonly ConcurrentDictionary<string, Info> Cache = new();

        public static async Task<Info> ReadAsync(PipelineClient c, string wb, string rev, string guid, CancellationToken ct)
        {
            var key = $"{wb}|{rev}|{guid}";
            if (Cache.TryGetValue(key, out var known)) return known;
            Info info;
            try
            {
                // Right after the project service starts, the asset index answers 202 (an empty body) for a
                // few seconds: retry, and don't keep an answer without a path.
                var path = "";
                for (var attempt = 0; attempt < 5 && path.Length == 0; attempt++)
                {
                    if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(1), ct);
                    path = ((await c.GetAssetAsync(wb, rev, guid, ct)).Path ?? "").TrimStart('/');
                }
                if (path.Length == 0) return new Info(null, null);
                if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) info = new Info(path, null);
                else
                {
                    var text = await Files.TextAsync(c, wb, rev, path, ct);
                    info = new Info(path, UnityFile.IsYaml(text) ? PrefabRoot.Read(UnityFile.Parse(text)) : null);
                }
            }
            catch (PipelineApiException) { return new Info(null, null); }
            if (Cache.Count > 2000) Cache.Clear();
            Cache[key] = info;
            return info;
        }
    }
}

record ModelEdit(string FileId, string Path, string? Value, string? As);
record ModelEditRequest(string Path, List<ModelEdit>? Edits, string? Message, string? Branch, bool Preview = false);

record PoseJson(double[] P, double[] Q, double[] S)
{
    public Pose ToPose() => new(new Unity.DataModel.Vector3(P[0], P[1], P[2]), new Unity.DataModel.Quaternion(Q[0], Q[1], Q[2], Q[3]),
        new Unity.DataModel.Vector3(S[0], S[1], S[2]));
    public static PoseJson From(Pose p) => new([p.Position.X, p.Position.Y, p.Position.Z],
        [p.Rotation.X, p.Rotation.Y, p.Rotation.Z, p.Rotation.W], [p.Scale.X, p.Scale.Y, p.Scale.Z]);
}
record MovedItem(string Id, PoseJson World);
record AddedItem(string Key, string AssetGuid, string? Name, PoseJson World);
record LevelSaveRequest(string Path, List<MovedItem>? Moved, List<AddedItem>? Added, string? Message, string? Branch, bool Preview = false);
