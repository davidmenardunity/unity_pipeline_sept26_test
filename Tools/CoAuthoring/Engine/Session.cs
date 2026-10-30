using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using CoAuthoring.Commands;
using Unity.DataModel;

namespace CoAuthoring.Engine;

/// <summary>
/// A workbench being co-authored. Everyone's commands go through one queue and are resolved one at a time, in
/// arrival order, against a draft of the workbench's files: each command that succeeds is written to the draft
/// (on disk, so it survives a restart) and gets the next sequence number; one that fails changes nothing.
/// <see cref="SaveAsync"/> collapses the draft into one commit on the workbench.
/// </summary>
public sealed class Session : IDisposable
{
    readonly IWorkbenchSource source;
    readonly string dir;
    readonly Channel<Func<Task>> queue = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
    readonly ConcurrentDictionary<string, string> files = new(StringComparer.Ordinal);    // path → text (as read, or as changed); written on the queue
    readonly HashSet<string> dirty = new(StringComparer.Ordinal);
    readonly Dictionary<(string Path, long FileId, string Field), long> fieldSeq = new();
    readonly Dictionary<string, string> actorScene = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, string?> guids = new(), paths = new(), scripts = new();
    readonly ConcurrentDictionary<string, Task<PrefabRoot?>> prefabs = new();
    readonly ConcurrentDictionary<string, JsonObject> idempotent = new();
    readonly List<JsonObject> changeLog = new();   // since the last save: who did what

    Session(string workbench, IWorkbenchSource source, string dataDir)
    {
        Workbench = workbench;
        this.source = source;
        dir = Path.Combine(dataDir, Safe(workbench));
        Events = new EventHub(4096);
        Presence = new Presence(list => Events.PublishTransient(new SessionEvent(Seq, "presence", null, new JsonObject { ["participants"] = list }, DateTimeOffset.UtcNow)));
        _ = Task.Run(Worker);
    }

    public string Workbench { get; }

    /// <summary>The revision the draft is on: saves commit on top of it.</summary>
    public string BaseRevision { get; private set; } = "";

    /// <summary>The revision files the draft hasn't touched are read at (the one the session started from).</summary>
    public string ReadRevision { get; private set; } = "";

    /// <summary>The last sequence number given to a change.</summary>
    public long Seq { get; private set; }

    public EventHub Events { get; }
    public Presence Presence { get; }
    public IReadOnlyCollection<string> DirtyFiles => dirty;

    static string Safe(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
    public static string NormalizePath(string path)
    {
        path = path.Replace('\\', '/').Trim().TrimStart('/');
        return path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Packages/", StringComparison.Ordinal)
            || path.StartsWith("ProjectSettings/", StringComparison.Ordinal) ? path : "Assets/" + path;
    }

    // ── starting, and the draft on disk ───────────────────────────────────

    /// <summary>
    /// The session for a workbench: picked up from disk when there is a draft there (its unsaved changes come back),
    /// else started at the workbench's latest revision.
    /// </summary>
    public static async Task<Session> OpenAsync(string workbench, IWorkbenchSource source, string dataDir, CancellationToken ct)
    {
        var s = new Session(workbench, source, dataDir);
        var state = Path.Combine(s.dir, "state.json");
        if (File.Exists(state))
        {
            var j = JsonNode.Parse(await File.ReadAllTextAsync(state, ct))!.AsObject();
            s.BaseRevision = j["base"]!.GetValue<string>();
            s.ReadRevision = j["read"]?.GetValue<string>() ?? s.BaseRevision;
            s.Seq = j["seq"]!.GetValue<long>();
            foreach (var p in j["dirty"]!.AsArray().Select(n => n!.GetValue<string>()))
            {
                var f = Path.Combine(s.dir, "files", p);
                if (!File.Exists(f)) continue;
                s.files[p] = await File.ReadAllTextAsync(f, ct);
                s.dirty.Add(p);
            }
            foreach (var c in j["changes"]?.AsArray() ?? []) if (c is JsonObject o) s.changeLog.Add(o);
        }
        else
        {
            s.BaseRevision = s.ReadRevision = await source.HeadAsync(workbench, ct);
            await s.PersistAsync();
        }
        return s;
    }

    async Task PersistAsync()
    {
        Directory.CreateDirectory(Path.Combine(dir, "files"));
        foreach (var p in dirty)
        {
            var f = Path.Combine(dir, "files", p);
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            await File.WriteAllTextAsync(f, files[p], new UTF8Encoding(false));
        }
        var state = new JsonObject
        {
            ["workbench"] = Workbench, ["base"] = BaseRevision, ["read"] = ReadRevision, ["seq"] = Seq,
            ["dirty"] = new JsonArray(dirty.Order().Select(p => (JsonNode)p).ToArray()),
            ["changes"] = new JsonArray(changeLog.TakeLast(500).Select(c => c.DeepClone()).ToArray()),
        };
        var tmp = Path.Combine(dir, "state.json.tmp");
        await File.WriteAllTextAsync(tmp, state.ToJsonString(), new UTF8Encoding(false));
        File.Move(tmp, Path.Combine(dir, "state.json"), true);
    }

    async Task LogAsync(JsonObject entry)
    {
        Directory.CreateDirectory(dir);
        await File.AppendAllTextAsync(Path.Combine(dir, "oplog.jsonl"), entry.ToJsonString() + "\n");
    }

    // ── the queue ─────────────────────────────────────────────────────────

    async Task Worker()
    {
        await foreach (var work in queue.Reader.ReadAllAsync())
            try { await work(); } catch { /* each item reports its own failure */ }
    }

    /// <summary>Run <paramref name="work"/> on the queue: after everything sent before it, and alone.</summary>
    public Task<T> Enqueue<T>(Func<Task<T>> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Writer.TryWrite(async () =>
        {
            try { done.SetResult(await work()); }
            catch (Exception e) { done.SetException(e); }
        });
        return done.Task;
    }

    /// <summary>A retried request (same idempotency key) gets the first answer.</summary>
    public bool TryReplay(string? key, out JsonObject? answer)
    {
        answer = null;
        return key != null && idempotent.TryGetValue(key, out answer);
    }

    public void Remember(string? key, JsonObject answer)
    {
        if (key == null) return;
        if (idempotent.Count > 2000) idempotent.Clear();
        idempotent[key] = answer;
    }

    // ── reading ───────────────────────────────────────────────────────────

    internal async Task<string?> ReadAsync(string path, CancellationToken ct)
    {
        if (files.TryGetValue(path, out var text)) return text;
        text = await source.ReadTextAsync(Workbench, ReadRevision, path, ct);
        if (text != null) files[path] = text;
        return text;
    }

    internal async Task<string?> GuidOfAsync(string path, CancellationToken ct) =>
        guids.TryGetValue(path, out var g) ? g : guids[path] = await source.GuidOfAsync(Workbench, ReadRevision, path, ct);

    internal async Task<string?> PathOfAsync(string guid, CancellationToken ct) =>
        paths.TryGetValue(guid, out var p) ? p : paths[guid] = (await source.PathOfAsync(Workbench, ReadRevision, guid, ct))?.TrimStart('/');

    internal async Task<string?> FindScriptAsync(string type, CancellationToken ct) =>
        scripts.TryGetValue(type, out var g) ? g : scripts[type] = await source.FindScriptAsync(Workbench, ReadRevision, type, ct);

    // A prefab's root, read once (and shared by concurrent callers). Safe off the queue: it reads the draft's copy
    // when there is one, else the workbench, without adding to the draft.
    internal async Task<PrefabRoot?> PrefabAsync(string guid, CancellationToken ct)
    {
        var task = prefabs.GetOrAdd(guid, g => ReadPrefabAsync(g));
        var root = await task;
        if (root == null && task.IsCompleted && await PathOfAsync(guid, ct) == null) prefabs.TryRemove(guid, out _);   // may resolve later (the asset index warms up)
        return root;
    }

    async Task<PrefabRoot?> ReadPrefabAsync(string guid)
    {
        var path = await PathOfAsync(guid, CancellationToken.None);
        if (path == null || !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) return null;
        var text = files.TryGetValue(path, out var t) ? t : await source.ReadTextAsync(Workbench, ReadRevision, path, CancellationToken.None);
        return text != null && UnityFile.IsYaml(text) ? PrefabRoot.Read(UnityFile.Parse(text)) : null;
    }

    /// <summary>A scene as the draft has it, with the roots of the prefabs it uses (for placing it in a viewer).</summary>
    // Only reading the scene's text waits for the queue; the prefab roots load beside it, several at a time.
    public async Task<(UnityFile Scene, Dictionary<string, PrefabRoot?> Prefabs)?> SceneAsync(string path, CancellationToken ct)
    {
        var text = await DraftTextAsync(path, ct);
        if (text == null || !UnityFile.IsYaml(text)) return null;
        var scene = UnityFile.Parse(text);
        var guids = Unity.DataModel.Levels.LevelScene.PrefabGuids(scene);
        var roots = new ConcurrentDictionary<string, PrefabRoot?>();
        await Parallel.ForEachAsync(guids, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (g, t) => roots[g] = await PrefabAsync(g, t));
        return (scene, new Dictionary<string, PrefabRoot?>(roots));
    }

    /// <summary>A scene as the draft has it, without its prefabs (for its hierarchy).</summary>
    public async Task<UnityFile?> SceneFileAsync(string path, CancellationToken ct) =>
        await DraftTextAsync(path, ct) is { } text && UnityFile.IsYaml(text) ? UnityFile.Parse(text) : null;

    /// <summary>A file as the draft has it (with every change so far), or null.</summary>
    public Task<string?> DraftTextAsync(string path, CancellationToken ct) => Enqueue(() => ReadAsync(NormalizePath(path), ct));

    // ── running commands ──────────────────────────────────────────────────

    public string? SceneOf(string actor) => actorScene.TryGetValue(actor, out var s) ? s : null;

    internal CommandScope NewScope(string actor, string kind, string? scene, CancellationToken ct) =>
        new(this, actor, kind, scene != null ? NormalizePath(scene) : SceneOf(actor), ct);

    /// <summary>
    /// Keep what a scope changed: check conflicts (with <paramref name="baseSeq"/>, a change fails when a field it
    /// touches changed after that sequence), write the files to the draft, give the change a sequence number, and
    /// tell everyone. Returns what changed (or null when nothing did). Runs on the queue.
    /// </summary>
    internal async Task<JsonObject?> CommitScopeAsync(CommandScope scope, string command, long? baseSeq)
    {
        if (scope.ScenePathOrNull is { } sp) actorScene[scope.Actor] = sp;
        var changes = new JsonObject();
        var changedFiles = new List<(string Path, string Text, FileDiff Diff)>();
        foreach (var (path, file) in scope.Opened)
        {
            var after = file.ToText();
            var before = files[path];
            if (after == before) continue;
            changedFiles.Add((path, after, FileDiff.Between(UnityFile.Parse(before), UnityFile.Parse(after))));
        }
        if (changedFiles.Count == 0) return null;

        if (baseSeq is { } b)
        {
            var stale = new JsonArray();
            foreach (var (path, _, diff) in changedFiles)
                foreach (var (id, fields) in diff.Updated)
                    foreach (var f in fields)
                        if (fieldSeq.TryGetValue((path, id, f), out var s) && s > b)
                            stale.Add(new JsonObject { ["path"] = path, ["fileId"] = id.ToString(), ["field"] = f, ["changedAt"] = s });
            if (stale.Count > 0)
                throw new CommandException(ErrorCodes.Conflict,
                    $"{stale.Count} field(s) this command changes were changed by someone else after sequence {b}; read them again and resend.",
                    new JsonObject { ["fields"] = stale });
        }

        var seq = ++Seq;
        foreach (var (path, text, diff) in changedFiles)
        {
            files[path] = text;
            dirty.Add(path);
            foreach (var (id, fields) in diff.Updated) foreach (var f in fields) fieldSeq[(path, id, f)] = seq;
            changes[path] = DescribeDiff(UnityFile.Parse(text), diff);
        }
        var entry = new JsonObject { ["seq"] = seq, ["actor"] = scope.Actor, ["kind"] = scope.ActorKind, ["command"] = command, ["at"] = DateTimeOffset.UtcNow.ToString("O"),
            ["files"] = new JsonArray(changedFiles.Select(c => (JsonNode)c.Path).ToArray()) };
        changeLog.Add(entry);
        await PersistAsync();
        await LogAsync(entry);
        Events.Publish(new SessionEvent(seq, "write", scope.Actor, new JsonObject { ["command"] = command, ["kind"] = scope.ActorKind, ["changes"] = changes.DeepClone() }, DateTimeOffset.UtcNow));
        return changes;
    }

    static JsonObject DescribeDiff(UnityFile after, FileDiff diff)
    {
        var h = after.Hierarchy;
        JsonObject Obj(UnityObject o) => new() { ["fileId"] = o.FileId.ToString(), ["type"] = o.TypeName, ["name"] = (o as PrefabInstance)?.Name ?? o.Name, ["hierarchyPath"] = h.PathOf(o.FileId) };
        return new JsonObject
        {
            ["created"] = new JsonArray(diff.Created.Where(o => o is GameObject or PrefabInstance).Select(o => (JsonNode)Obj(o)).ToArray()),
            ["deleted"] = new JsonArray(diff.Deleted.Where(o => o is GameObject or PrefabInstance).Select(o => (JsonNode)new JsonObject { ["fileId"] = o.FileId.ToString(), ["type"] = o.TypeName }).ToArray()),
            ["updated"] = new JsonArray(diff.Updated.Select(kv => (JsonNode)new JsonObject
            {
                ["fileId"] = kv.Key.ToString(), ["type"] = after.Find(kv.Key)?.TypeName,
                ["gameObject"] = after.Find(kv.Key)?.GetReference("m_GameObject")?.FileId.ToString(),
                ["fields"] = new JsonArray(kv.Value.Select(f => (JsonNode)f).ToArray()),
            }).ToArray()),
        };
    }

    // ── saving ────────────────────────────────────────────────────────────

    /// <summary>
    /// Every change since the last save as one commit on the workbench (on its branch), with a message. If someone
    /// committed to a file the draft changed meanwhile, nothing is committed (a conflict).
    /// </summary>
    public Task<JsonObject> SaveAsync(string actor, string? message, CancellationToken ct) => Enqueue(async () =>
    {
        if (dirty.Count == 0) throw new CommandException(ErrorCodes.Invalid, "Nothing to save: the draft has no changes.");
        var head = await source.HeadAsync(Workbench, ct);
        var onto = BaseRevision;
        if (head != BaseRevision)
        {
            var clash = new List<string>();
            foreach (var p in dirty)
                if (await source.ReadTextAsync(Workbench, head, p, ct) != await source.ReadTextAsync(Workbench, BaseRevision, p, ct)) clash.Add(p);
            if (clash.Count > 0)
                throw new CommandException(ErrorCodes.Conflict,
                    $"The workbench moved to revision {head}, and {string.Join(", ", clash)} changed there too. Discard the draft, or save those changes elsewhere first.",
                    new JsonObject { ["head"] = head, ["files"] = new JsonArray(clash.Select(c => (JsonNode)c).ToArray()) });
            onto = head;
        }
        var people = changeLog.Select(c => c["actor"]!.GetValue<string>()).Distinct().ToList();
        message = string.IsNullOrWhiteSpace(message)
            ? $"Co-authoring: {changeLog.Count} change{(changeLog.Count == 1 ? "" : "s")} by {string.Join(", ", people)}"
            : message.Trim();
        var saved = dirty.Order().Select(p => (p, files[p])).ToList();
        var revision = await source.CommitAsync(Workbench, onto, saved, message, ct);
        var count = changeLog.Count;
        BaseRevision = revision;
        dirty.Clear();
        changeLog.Clear();
        await PersistAsync();
        await LogAsync(new JsonObject { ["seq"] = Seq, ["actor"] = actor, ["save"] = revision, ["message"] = message, ["at"] = DateTimeOffset.UtcNow.ToString("O") });
        var data = new JsonObject { ["revision"] = revision, ["message"] = message, ["files"] = new JsonArray(saved.Select(s => (JsonNode)s.Item1).ToArray()), ["changes"] = count, ["by"] = new JsonArray(people.Select(p => (JsonNode)p).ToArray()) };
        Events.Publish(new SessionEvent(++Seq, "save", actor, data, DateTimeOffset.UtcNow));
        return data;
    });

    /// <summary>Drop every unsaved change: the draft goes back to the workbench's latest revision.</summary>
    public Task<JsonObject> DiscardAsync(string actor, CancellationToken ct) => Enqueue(async () =>
    {
        var dropped = changeLog.Count;
        files.Clear();
        dirty.Clear();
        changeLog.Clear();
        fieldSeq.Clear();
        prefabs.Clear();
        BaseRevision = ReadRevision = await source.HeadAsync(Workbench, ct);
        await PersistAsync();
        var data = new JsonObject { ["revision"] = BaseRevision, ["dropped"] = dropped };
        Events.Publish(new SessionEvent(++Seq, "discard", actor, data, DateTimeOffset.UtcNow));
        return data;
    });

    public JsonObject Describe() => new()
    {
        ["workbench"] = Workbench, ["baseRevision"] = BaseRevision, ["readRevision"] = ReadRevision, ["seq"] = Seq,
        ["dirty"] = new JsonArray(dirty.Order().Select(p => (JsonNode)p).ToArray()),
        ["changes"] = new JsonArray(changeLog.Select(c => c.DeepClone()).ToArray()),
        ["participants"] = Presence.List(),
    };

    public void Dispose()
    {
        queue.Writer.TryComplete();
        Presence.Dispose();
    }
}

/// <summary>The sessions of this service, one per workbench, with their drafts under one folder.</summary>
public sealed class Sessions(IWorkbenchSource source, string dataDir) : IDisposable
{
    readonly ConcurrentDictionary<string, Lazy<Task<Session>>> sessions = new(StringComparer.Ordinal);

    public string DataDir { get; } = dataDir;

    public Task<Session> GetAsync(string workbench, CancellationToken ct = default) =>
        sessions.GetOrAdd(workbench, wb => new Lazy<Task<Session>>(() => Session.OpenAsync(wb, source, dataDir, CancellationToken.None))).Value;

    public void Dispose()
    {
        foreach (var s in sessions.Values) if (s.IsValueCreated && s.Value.IsCompletedSuccessfully) s.Value.Result.Dispose();
    }
}
