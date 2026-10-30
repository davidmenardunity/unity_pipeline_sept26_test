using System.Text.Json.Nodes;
using CoAuthoring.Commands;
using Unity.DataModel;
using Unity.DataModel.Commands;

namespace CoAuthoring.Engine;

/// <summary>
/// One command's (or one transaction's) view of a session: the files it opens, read from the draft. The session
/// keeps what it changed when it succeeds, and drops it when it fails.
/// </summary>
public sealed class CommandScope : ICommandContext
{
    readonly Session session;
    readonly Dictionary<string, UnityFile> opened = new(StringComparer.Ordinal);
    readonly CancellationToken ct;
    string? scenePath;

    internal CommandScope(Session session, string actor, string actorKind, string? scenePath, CancellationToken ct)
    {
        this.session = session;
        Actor = actor;
        ActorKind = actorKind;
        this.scenePath = scenePath;
        this.ct = ct;
    }

    public string Actor { get; }
    public string ActorKind { get; }
    public Session Session => session;
    public CancellationToken Cancellation => ct;

    public string ScenePath => scenePath ?? throw new CommandException(ErrorCodes.Invalid,
        "No scene is open: send open_scene --path Assets/…/Scene.unity first (or pass the scene with the request).");

    internal string? ScenePathOrNull => scenePath;

    /// <summary>Change the scene this caller works in (open_scene).</summary>
    public void UseScene(string path) => scenePath = Session.NormalizePath(path);

    public UnityFile Scene => Open(ScenePath);

    /// <summary>Files opened by this scope, by path.</summary>
    internal IReadOnlyDictionary<string, UnityFile> Opened => opened;

    public UnityFile Open(string path) => OpenAsync(path).GetAwaiter().GetResult();

    public async Task<UnityFile> OpenAsync(string path)
    {
        path = Session.NormalizePath(path);
        if (opened.TryGetValue(path, out var f)) return f;
        var text = await session.ReadAsync(path, ct) ?? throw CommandException.NotFound($"No file {path} in the workbench.");
        if (!UnityFile.IsYaml(text)) throw new CommandException(ErrorCodes.Unsupported, $"{path} isn't saved as text (YAML).");
        return opened[path] = UnityFile.Parse(text);
    }

    public string? GuidOf(string path) => GuidOfAsync(path).GetAwaiter().GetResult();
    public Task<string?> GuidOfAsync(string path) => session.GuidOfAsync(Session.NormalizePath(path), ct);

    public string? PathOf(string guid) => PathOfAsync(guid).GetAwaiter().GetResult();
    public Task<string?> PathOfAsync(string guid) => session.PathOfAsync(guid, ct);

    public PrefabRoot? Prefab(string guid) => PrefabAsync(guid).GetAwaiter().GetResult();
    public Task<PrefabRoot?> PrefabAsync(string guid) => session.PrefabAsync(guid, ct);

    public Task<string?> FindScriptAsync(string typeName) => session.FindScriptAsync(typeName, ct);

    /// <summary>Prefab roots for every prefab a scene's instances come from (for world poses).</summary>
    public async Task<Dictionary<string, PrefabRoot?>> PrefabsOfAsync(UnityFile scene)
    {
        var result = new Dictionary<string, PrefabRoot?>();
        foreach (var g in Unity.DataModel.Levels.LevelScene.PrefabGuids(scene)) result[g] = await PrefabAsync(g);
        return result;
    }

    /// <summary>A scene's GUID (for globalIds), or null.</summary>
    public Task<string?> SceneGuidAsync() => GuidOfAsync(ScenePath);
}
