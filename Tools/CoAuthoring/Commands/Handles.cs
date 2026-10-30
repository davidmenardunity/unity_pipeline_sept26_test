using System.Globalization;
using System.Text.Json.Nodes;
using CoAuthoring.Engine;
using Unity.DataModel;

namespace CoAuthoring.Commands;

/// <summary>What a handle names: an object in the scene (a GameObject or a prefab instance), or an asset.</summary>
public sealed record Target(string Kind, string? ScenePath, long FileId, string? AssetPath, string? Guid)
{
    public const string GameObject = "GameObject", Instance = "PrefabInstance", Asset = "Asset";
    public bool IsSceneObject => Kind is GameObject or Instance;
}

/// <summary>
/// The pipeline package's object handles (ObjectRef), resolved against the draft. A handle is a string or an object
/// with one of: globalId (GlobalObjectId_V1-…), path (an asset path, or else a hierarchy path), guid (+ fileId),
/// hierarchyPath. Strings read like the package's: "GlobalObjectId_V1-…", "guid:…", "Assets/…", else a hierarchy
/// path ("/Root/Child" or a name). This service adds "fileId" (an object of the scene by its file ID).
/// instanceId has no meaning outside the Editor, so it's refused.
/// </summary>
public static class Handles
{
    public static async Task<Target> ResolveAsync(CommandScope scope, JsonNode? handle, string what = "target")
    {
        switch (handle)
        {
            case null:
                throw CommandException.Bad($"--{what} is required");
            case JsonValue v when v.TryGetValue<string>(out var s):
                return await FromStringAsync(scope, s.Trim(), what);
            case JsonValue n when n.TryGetValue<long>(out var id):
                return SceneObject(scope, scope.Scene, scope.ScenePath, id, what);
            case JsonObject o:
                if (o["globalId"]?.GetValue<string>() is { Length: > 0 } g) return await FromStringAsync(scope, g, what);
                if (o["instanceId"] != null) throw new CommandException(ErrorCodes.Unsupported, "instanceId only means something in a running Editor; use globalId, hierarchyPath or path.");
                if (o["hierarchyPath"]?.GetValue<string>() is { Length: > 0 } hp) return ByHierarchyPath(scope, hp, what);
                if (o["guid"]?.GetValue<string>() is { Length: > 0 } guid)
                    return await AssetAsync(scope, guid, o["fileId"] is JsonValue fv && fv.TryGetValue<long>(out var f) ? f : 0, what);
                if (o["path"]?.GetValue<string>() is { Length: > 0 } p) return await FromStringAsync(scope, p, what);
                if (o["fileId"] is JsonValue fid && long.TryParse(fid.ToString(), out var fileId))
                {
                    var scene = o["scenePath"]?.GetValue<string>() is { Length: > 0 } sp ? Session.NormalizePath(sp) : scope.ScenePath;
                    return SceneObject(scope, await scope.OpenAsync(scene), scene, fileId, what);
                }
                throw CommandException.Bad($"--{what}: an empty handle");
            default:
                throw CommandException.Bad($"--{what}: '{handle.ToJsonString()}' isn't a handle");
        }
    }

    static async Task<Target> FromStringAsync(CommandScope scope, string s, string what)
    {
        if (s.StartsWith("GlobalObjectId_V1-", StringComparison.Ordinal))
        {
            // GlobalObjectId_V1-{type}-{assetGuid}-{targetObjectId}-{targetPrefabId}
            var parts = s.Split('-');
            if (parts.Length != 5 || !long.TryParse(parts[3], out var obj) || !long.TryParse(parts[4], out var prefab))
                throw CommandException.Bad($"--{what}: '{s}' isn't a GlobalObjectId");
            var guid = parts[2];
            var path = await scope.PathOfAsync(guid) ?? throw CommandException.NotFound($"--{what}: no asset with GUID {guid}.");
            if (path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                return SceneObject(scope, await scope.OpenAsync(path), path, prefab != 0 ? prefab : obj, what);
            return new Target(Target.Asset, null, obj, path, guid);
        }
        if (s.StartsWith("guid:", StringComparison.Ordinal))
        {
            var parts = s.Split(':');
            return await AssetAsync(scope, parts[1], parts.Length > 2 && long.TryParse(parts[2], out var f) ? f : 0, what);
        }
        if (s.StartsWith("instanceId:", StringComparison.Ordinal))
            throw new CommandException(ErrorCodes.Unsupported, "instanceId only means something in a running Editor; use globalId, hierarchyPath or path.");
        if (s.StartsWith("fileId:", StringComparison.Ordinal) && long.TryParse(s[7..], out var id))
            return SceneObject(scope, scope.Scene, scope.ScenePath, id, what);
        if (s.StartsWith("Assets/", StringComparison.Ordinal) || s.StartsWith("Packages/", StringComparison.Ordinal))
        {
            var guid = await scope.GuidOfAsync(s) ?? throw CommandException.NotFound($"--{what}: no asset at '{s}'.");
            return new Target(Target.Asset, null, 0, s, guid);
        }
        // A bare string: a hierarchy path first, else an asset path under Assets/ (as the package tries both).
        var nodes = scope.Scene.Hierarchy.FindPath(s);
        if (nodes.Count > 0) return ByHierarchyPath(scope, s, what);
        if (s.Contains('.') && await scope.GuidOfAsync("Assets/" + s) is { } g2) return new Target(Target.Asset, null, 0, "Assets/" + s, g2);
        throw CommandException.NotFound($"Could not resolve '{s}': no GameObject at hierarchy path '{s}' in {scope.ScenePath}, and no asset at 'Assets/{s}'.");
    }

    static Target ByHierarchyPath(CommandScope scope, string path, string what)
    {
        var h = scope.Scene.Hierarchy;
        var nodes = h.FindPath(path);
        if (nodes.Count == 0) throw CommandException.NotFound($"--{what}: no GameObject at hierarchy path '{path}' in {scope.ScenePath}.");
        if (nodes.Count > 1)
            throw CommandException.Bad($"--{what}: '{path}' matches {nodes.Count} objects: {string.Join(", ", nodes.Take(6).Select(n => h.PathOf(n.Object.FileId)))}. Use a full path or a globalId.");
        return SceneObject(scope, scope.Scene, scope.ScenePath, nodes[0].Object.FileId, what);
    }

    static Target SceneObject(CommandScope scope, UnityFile scene, string scenePath, long id, string what)
    {
        var node = scene.Hierarchy.NodeOf(id);
        if (node == null)
        {
            // A component: its GameObject.
            if (scene.Find(id)?.GetReference("m_GameObject")?.FileId is { } go && scene.Hierarchy.NodeOf(go) is { } n) node = n;
            else throw CommandException.NotFound($"--{what}: {scenePath} has no GameObject &{id}.");
        }
        return new Target(node.Kind == HierarchyNodeKind.PrefabInstance ? Target.Instance : Target.GameObject, scenePath, node.Object.FileId, null, null);
    }

    static async Task<Target> AssetAsync(CommandScope scope, string guid, long fileId, string what)
    {
        var path = await scope.PathOfAsync(guid) ?? throw CommandException.NotFound($"--{what}: unknown GUID '{guid}'.");
        if (path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) && fileId != 0)
            return SceneObject(scope, await scope.OpenAsync(path), path, fileId, what);
        return new Target(Target.Asset, null, fileId, path, guid);
    }

    /// <summary>
    /// An object's identity, as the pipeline package's AuthoringResult: globalId, hierarchyPath, type, plus this
    /// service's fileId and scenePath (it has no instance IDs).
    /// </summary>
    public static async Task<JsonObject> DescribeAsync(CommandScope scope, string scenePath, long fileId)
    {
        var scene = await scope.OpenAsync(scenePath);
        var guid = await scope.GuidOfAsync(scenePath) ?? "00000000000000000000000000000000";
        var node = scene.Hierarchy.NodeOf(fileId);
        var o = scene.Find(fileId);
        var result = new JsonObject();
        if (o is PrefabInstance inst)
        {
            var root = inst.SourcePrefab.Guid is { } pg ? await scope.PrefabAsync(pg) : null;
            result["globalId"] = $"GlobalObjectId_V1-2-{guid}-{Id(root?.RootGameObjectId ?? 0)}-{Id(fileId)}";
            result["type"] = "GameObject";
            result["prefab"] = inst.SourcePrefab.Guid is { } g ? await scope.PathOfAsync(g) : null;
            result["prefabInstance"] = true;
        }
        else
        {
            result["globalId"] = $"GlobalObjectId_V1-2-{guid}-{Id(fileId)}-0";
            result["type"] = o?.TypeName ?? "GameObject";
        }
        result["hierarchyPath"] = scene.Hierarchy.PathOf(fileId);
        result["name"] = node?.Label;
        result["fileId"] = Id(fileId);
        result["scenePath"] = scenePath;
        return result;
    }

    static string Id(long v) => v.ToString(CultureInfo.InvariantCulture);
}
