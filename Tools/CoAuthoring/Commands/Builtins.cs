using System.Globalization;
using System.Text.Json.Nodes;
using CoAuthoring.Engine;
using Unity.DataModel;
using Unity.DataModel.Levels;
using Unity.DataModel.Yaml;

namespace CoAuthoring.Commands;

/// <summary>
/// The pipeline package's scene commands, run on the Unity data model instead of in the Editor: same names, same
/// parameters (catalog.json), results shaped like the package's (globalId, hierarchyPath, type).
/// </summary>
public static class Builtins
{
    public sealed record Handler(bool Writes, Func<CommandScope, JsonObject, Task<JsonNode?>> Run);

    public static readonly IReadOnlyDictionary<string, Handler> All = new Dictionary<string, Handler>
    {
        ["open_scene"] = new(false, OpenScene),
        ["list_open_scenes"] = new(false, ListOpenScenes),
        ["get_scene_hierarchy"] = new(false, GetSceneHierarchy),
        ["find_gameobjects"] = new(false, FindGameObjects),
        ["create_gameobject"] = new(true, CreateGameObject),
        ["create_gameobjects"] = new(true, CreateGameObjects),
        ["instantiate_prefab"] = new(true, InstantiatePrefab),
        ["set_transform"] = new(true, SetTransform),
        ["set_parent"] = new(true, SetParent),
        ["set_active"] = new(true, SetActive),
        ["set_tag"] = new(true, SetTag),
        ["set_layer"] = new(true, SetLayer),
        ["rename_gameobject"] = new(true, Rename),
        ["delete_gameobject"] = new(true, Delete),
        ["add_component"] = new(true, AddComponent),
        ["remove_component"] = new(true, RemoveComponent),
        ["get_component_properties"] = new(false, GetComponentProperties),
        ["set_component_properties"] = new(true, SetComponentProperties),
        ["get_serialized_fields"] = new(false, GetSerializedFields),
        ["set_serialized_field"] = new(true, SetSerializedField),
        ["batch"] = new(true, (_, _) => throw new InvalidOperationException("batch is run by the runner")),
    };

    // ── scenes ────────────────────────────────────────────────────────────

    static async Task<JsonNode?> OpenScene(CommandScope s, JsonObject a)
    {
        var path = Session.NormalizePath(Str(a, "path") ?? throw CommandException.Bad("--path is required"));
        if (!path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) throw CommandException.Bad($"{path} isn't a scene (.unity).");
        await s.OpenAsync(path);
        s.UseScene(path);
        return new JsonObject { ["path"] = path, ["active"] = true, ["note"] = "Your commands now edit this scene (in the shared draft)." };
    }

    static Task<JsonNode?> ListOpenScenes(CommandScope s, JsonObject a)
    {
        var list = new JsonArray();
        if (s.ScenePathOrNull is { } p) list.Add(new JsonObject { ["path"] = p, ["active"] = true, ["hasUnsavedChanges"] = s.Session.DirtyFiles.Contains(p) });
        return Task.FromResult<JsonNode?>(new JsonObject { ["scenes"] = list });
    }

    static async Task<JsonNode?> GetSceneHierarchy(CommandScope s, JsonObject a)
    {
        var path = Str(a, "path") is { } p ? Session.NormalizePath(p) : s.ScenePath;
        var scene = await s.OpenAsync(path);
        var guid = await s.GuidOfAsync(path);
        var h = scene.Hierarchy;
        JsonObject Node(HierarchyNode n)
        {
            var o = new JsonObject
            {
                ["name"] = n.Label, ["fileId"] = Id(n.Object.FileId), ["hierarchyPath"] = h.PathOf(n.Object.FileId),
                ["globalId"] = n.Object is PrefabInstance ? null : $"GlobalObjectId_V1-2-{guid}-{Id(n.Object.FileId)}-0",
                ["active"] = IsActive(n),
            };
            if (n.Object is GameObject go) o["components"] = new JsonArray(h.ComponentsOf(go).Select(c => (JsonNode)c.TypeName).ToArray());
            if (n.Object is PrefabInstance inst) o["prefabInstance"] = inst.SourcePrefab.Guid;
            if (n.Children.Count > 0) o["children"] = new JsonArray(n.Children.Select(c => (JsonNode)Node(c)).ToArray());
            return o;
        }
        return new JsonObject { ["path"] = path, ["roots"] = new JsonArray(h.Roots.Select(r => (JsonNode)Node(r)).ToArray()) };
    }

    static async Task<JsonNode?> FindGameObjects(CommandScope s, JsonObject a)
    {
        var scene = s.Scene;
        var h = scene.Hierarchy;
        string? name = Str(a, "name"), tag = Str(a, "tag"), type = Str(a, "type"), path = Str(a, "hierarchy_path");
        var inactive = a["include_inactive"]?.GetValue<bool>() ?? false;
        var activeOf = new Dictionary<HierarchyNode, bool>();
        void Walk(HierarchyNode n, bool parentActive) { activeOf[n] = parentActive && IsActive(n); foreach (var c in n.Children) Walk(c, activeOf[n]); }
        foreach (var r in h.Roots) Walk(r, true);
        var found = new JsonArray();
        foreach (var n in h.All)
        {
            if (!inactive && !activeOf[n]) continue;
            if (name != null && n.Label != name) continue;
            if (path != null && !h.FindPath(path).Contains(n)) continue;
            if (tag != null && (n.Object as GameObject)?.GetString("m_TagString") != tag) continue;
            if (type != null && !(n.Object is GameObject go && await HasComponentAsync(s, h.ComponentsOf(go), type))) continue;
            found.Add(await Handles.DescribeAsync(s, s.ScenePath, n.Object.FileId));
            if (found.Count >= 500) break;
        }
        return new JsonObject { ["count"] = found.Count, ["gameObjects"] = found };
    }

    // ── creating ──────────────────────────────────────────────────────────

    static async Task<JsonNode?> CreateGameObject(CommandScope s, JsonObject a)
    {
        var (scenePath, scene, parent) = await ParentAsync(s, a);
        var primitive = Str(a, "primitive");
        var id = scene.CreateGameObject(Str(a, "name") ?? Templates.PrimitiveName(primitive) ?? "GameObject", primitive, parent);
        return await Handles.DescribeAsync(s, scenePath, id);
    }

    static async Task<JsonNode?> CreateGameObjects(CommandScope s, JsonObject a)
    {
        var (scenePath, scene, parent) = await ParentAsync(s, a);
        var count = (int)(a["count"]?.GetValue<long>() ?? 1);
        if (count is < 1 or > 1000) throw CommandException.Bad("--count must be between 1 and 1000");
        var positions = a["positions"] as JsonArray;
        var rotations = a["rotations"] as JsonArray;
        var scales = a["scales"] as JsonArray;
        foreach (var (list, label) in new[] { (positions, "positions"), (rotations, "rotations"), (scales, "scales") })
            if (list != null && list.Count != count) throw CommandException.Bad($"--{label} has {list.Count} entries; --count is {count}");
        var primitive = Str(a, "primitive");
        var name = Str(a, "name") ?? Templates.PrimitiveName(primitive) ?? "GameObject";
        var created = new JsonArray();
        for (var i = 0; i < count; i++)
        {
            var id = scene.CreateGameObject(name, primitive, parent);
            var t = scene.Find<GameObject>(id)!.Transform!;
            if (positions != null) t.SetVector3("m_LocalPosition", Vec(positions[i], "positions"));
            if (rotations != null) t.SetQuaternion("m_LocalRotation", Euler(rotations[i], "rotations"));
            if (scales != null) t.SetVector3("m_LocalScale", Vec(scales[i], "scales"));
            created.Add(await Handles.DescribeAsync(s, scenePath, id));
        }
        return new JsonObject { ["count"] = count, ["created"] = created };
    }

    static async Task<JsonNode?> InstantiatePrefab(CommandScope s, JsonObject a)
    {
        var prefab = await Handles.ResolveAsync(s, a["prefab"], "prefab");
        if (prefab.Kind != Target.Asset || prefab.AssetPath?.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) != true || prefab.Guid == null)
            throw CommandException.Bad("--prefab must name a .prefab asset (a path, guid or globalId).");
        var root = await s.PrefabAsync(prefab.Guid) ?? throw new CommandException(ErrorCodes.Unsupported, $"{prefab.AssetPath} has no plain root (a prefab variant?).");
        var scenePath = Str(a, "scene_path") is { } sp ? Session.NormalizePath(sp) : s.ScenePath;
        var scene = await s.OpenAsync(scenePath);
        var id = scene.InstantiatePrefab(prefab.Guid, root, Str(a, "name") ?? root.Name, root.Pose);
        return await Handles.DescribeAsync(s, scenePath, id);
    }

    // ── changing objects ──────────────────────────────────────────────────

    static async Task<JsonNode?> SetTransform(CommandScope s, JsonObject a)
    {
        var t = await SceneTargetAsync(s, a);
        var scene = await s.OpenAsync(t.ScenePath!);
        if (t.Kind == Target.Instance)
        {
            var inst = scene.Find<PrefabInstance>(t.FileId)!;
            var root = await RootOfAsync(s, inst);
            var pose = inst.RootPose(root);
            pose = new Pose(a["position"] is { } p ? Vec(p, "position") : pose.Position, a["rotation"] is { } r ? Euler(r, "rotation") : pose.Rotation,
                a["scale"] is { } sc ? Vec(sc, "scale") : pose.Scale);
            inst.SetRootPose(root, pose);
        }
        else
        {
            var tr = scene.Find<GameObject>(t.FileId)!.Transform!;
            if (a["position"] is { } p) tr.SetVector3("m_LocalPosition", Vec(p, "position"));
            if (a["rotation"] is { } r) tr.SetQuaternion("m_LocalRotation", Euler(r, "rotation"));
            if (a["scale"] is { } sc) tr.SetVector3("m_LocalScale", Vec(sc, "scale"));
        }
        return await Handles.DescribeAsync(s, t.ScenePath!, t.FileId);
    }

    static async Task<JsonNode?> SetParent(CommandScope s, JsonObject a)
    {
        var t = await SceneTargetAsync(s, a);
        var scene = await s.OpenAsync(t.ScenePath!);
        long parentTransform = 0;
        if (a["parent"] is { } ph && !(ph is JsonValue pv && pv.TryGetValue<string>(out var ps) && ps.Length == 0))
        {
            var p = await Handles.ResolveAsync(s, ph, "parent");
            if (p.Kind != Target.GameObject || p.ScenePath != t.ScenePath) throw CommandException.Bad("--parent must be a GameObject in the same scene (not a prefab instance).");
            parentTransform = scene.Find<GameObject>(p.FileId)!.Transform!.FileId;
        }
        var stays = a["world_position_stays"]?.GetValue<bool>() ?? true;
        var prefabs = await s.PrefabsOfAsync(scene);
        var level = new LevelScene(scene, prefabs);
        Pose? local = stays ? Pose.ToLocal(parentTransform == 0 ? Pose.Identity : level.WorldPose(parentTransform), level.WorldPose(t.FileId)) : null;
        PrefabRoot? root = t.Kind == Target.Instance ? await RootOfAsync(s, scene.Find<PrefabInstance>(t.FileId)!) : null;
        scene.SetParent(t.FileId, parentTransform, local, root);
        return await Handles.DescribeAsync(s, t.ScenePath!, t.FileId);
    }

    static async Task<JsonNode?> SetActive(CommandScope s, JsonObject a)
    {
        var active = a["active"]?.GetValue<bool>() ?? throw CommandException.Bad("--active is required");
        return await OnGameObject(s, a, (go) => go.SetBool("m_IsActive", active), (inst, root) =>
            inst.SetModification(new ObjectReference(root.RootGameObjectId, inst.SourcePrefab.Guid, 3), "m_IsActive", active ? "1" : "0"));
    }

    static async Task<JsonNode?> SetTag(CommandScope s, JsonObject a)
    {
        var tag = Str(a, "tag") ?? throw CommandException.Bad("--tag is required");
        var tags = await TagsAsync(s);
        if (!tags.Contains(tag)) throw CommandException.Bad($"Tag '{tag}' isn't defined. Tags: {string.Join(", ", tags)}.");
        return await OnGameObject(s, a, go => go.SetString("m_TagString", tag), (inst, root) =>
            inst.SetModification(new ObjectReference(root.RootGameObjectId, inst.SourcePrefab.Guid, 3), "m_TagString", YamlText.FormatString(tag)));
    }

    static async Task<JsonNode?> SetLayer(CommandScope s, JsonObject a)
    {
        var layer = Str(a, "layer") ?? throw CommandException.Bad("--layer is required");
        var layers = await LayersAsync(s);
        var index = int.TryParse(layer, out var n) && n is >= 0 and < 32 ? n : layers.IndexOf(layer);
        if (index < 0) throw CommandException.Bad($"Layer '{layer}' isn't defined. Layers: {string.Join(", ", layers.Where(l => l.Length > 0))}.");
        var text = index.ToString(CultureInfo.InvariantCulture);
        return await OnGameObject(s, a, go => go.SetRaw("m_Layer", text), (inst, root) =>
            inst.SetModification(new ObjectReference(root.RootGameObjectId, inst.SourcePrefab.Guid, 3), "m_Layer", text));
    }

    static async Task<JsonNode?> Rename(CommandScope s, JsonObject a)
    {
        var name = Str(a, "name") ?? throw CommandException.Bad("--name is required");
        return await OnGameObject(s, a, go => go.SetString("m_Name", name), (inst, root) =>
            inst.SetModification(new ObjectReference(root.RootGameObjectId, inst.SourcePrefab.Guid, 3), "m_Name", YamlText.FormatString(name, inst.Modifications.FirstOrDefault(m => m.PropertyPath == "m_Name")?.ValueNode?.Raw)));
    }

    static async Task<JsonNode?> Delete(CommandScope s, JsonObject a)
    {
        var t = await SceneTargetAsync(s, a);
        var scene = await s.OpenAsync(t.ScenePath!);
        var described = await Handles.DescribeAsync(s, t.ScenePath!, t.FileId);
        var gone = scene.Delete(t.FileId);
        described["deleted"] = true;
        described["objectsRemoved"] = gone.Count;
        return described;
    }

    static async Task<JsonNode?> OnGameObject(CommandScope s, JsonObject a, Action<GameObject> plain, Action<PrefabInstance, PrefabRoot> instance)
    {
        var t = await SceneTargetAsync(s, a);
        var scene = await s.OpenAsync(t.ScenePath!);
        if (t.Kind == Target.Instance)
        {
            var inst = scene.Find<PrefabInstance>(t.FileId)!;
            instance(inst, await RootOfAsync(s, inst));
        }
        else plain(scene.Find<GameObject>(t.FileId)!);
        return await Handles.DescribeAsync(s, t.ScenePath!, t.FileId);
    }

    // ── components ────────────────────────────────────────────────────────

    static async Task<JsonNode?> AddComponent(CommandScope s, JsonObject a)
    {
        var t = await PlainGameObjectAsync(s, a, "add components to");
        var type = Str(a, "type") ?? throw CommandException.Bad("--type is required");
        var scene = await s.OpenAsync(t.ScenePath!);
        long id;
        if (Templates.HasComponent(type)) id = scene.AddComponent(t.FileId, type);
        else
        {
            var shortName = type.Split('.').Last();
            var guid = await s.FindScriptAsync(shortName) ?? throw CommandException.NotFound(
                $"No component type '{type}': not a built-in this service knows ({string.Join(", ", Templates.ComponentTypes)}), and no script {shortName}.cs in the workbench.");
            id = scene.AddScript(t.FileId, guid);
        }
        var result = await Handles.DescribeAsync(s, t.ScenePath!, t.FileId);
        result["component"] = new JsonObject { ["type"] = Templates.HasComponent(type) ? Templates.ComponentTypeName(type) : type, ["fileId"] = Id(id) };
        return result;
    }

    static async Task<JsonNode?> RemoveComponent(CommandScope s, JsonObject a)
    {
        var t = await PlainGameObjectAsync(s, a, "remove components from");
        var scene = await s.OpenAsync(t.ScenePath!);
        var type = Str(a, "type") ?? throw CommandException.Bad("--type is required (which component)");
        var comps = await MatchComponentsAsync(s, scene.Find<GameObject>(t.FileId)!.Components, type);
        var c = comps.FirstOrDefault(x => x is not Transform) ?? throw CommandException.NotFound($"{scene.Hierarchy.PathOf(t.FileId)} has no {type} (that can be removed).");
        scene.RemoveComponent(c.FileId);
        var result = await Handles.DescribeAsync(s, t.ScenePath!, t.FileId);
        result["removed"] = new JsonObject { ["type"] = c.TypeName, ["fileId"] = Id(c.FileId) };
        return result;
    }

    static async Task<JsonNode?> GetComponentProperties(CommandScope s, JsonObject a)
    {
        var t = await PlainGameObjectAsync(s, a, "read the components of");
        var scene = await s.OpenAsync(t.ScenePath!);
        var comps = scene.Find<GameObject>(t.FileId)!.Components.ToList();
        if (Str(a, "type") is { } type) comps = await MatchComponentsAsync(s, comps, type);
        var list = new JsonArray();
        foreach (var c in comps)
            list.Add(new JsonObject { ["type"] = await TypeNameAsync(s, c), ["fileId"] = Id(c.FileId), ["properties"] = Properties(c, null) });
        return new JsonObject { ["target"] = await Handles.DescribeAsync(s, t.ScenePath!, t.FileId), ["components"] = list };
    }

    static async Task<JsonNode?> SetComponentProperties(CommandScope s, JsonObject a)
    {
        var t = await PlainGameObjectAsync(s, a, "set properties on");
        var scene = await s.OpenAsync(t.ScenePath!);
        var props = a["properties"] as JsonObject ?? throw CommandException.Bad("--properties must be an object of {name: value}");
        var comps = scene.Find<GameObject>(t.FileId)!.Components.ToList();
        if (Str(a, "type") is { } type) comps = await MatchComponentsAsync(s, comps, type);
        var set = new JsonArray();
        foreach (var (name, value) in props)
        {
            var (comp, path) = FindProperty(comps, name) ?? throw CommandException.NotFound($"No component of {scene.Hierarchy.PathOf(t.FileId)} has a property '{name}'.");
            SetValue(comp, path, value);
            set.Add(new JsonObject { ["component"] = comp.TypeName, ["property"] = path });
        }
        return new JsonObject { ["target"] = await Handles.DescribeAsync(s, t.ScenePath!, t.FileId), ["set"] = set };
    }

    static async Task<JsonNode?> GetSerializedFields(CommandScope s, JsonObject a)
    {
        var t = await PlainGameObjectAsync(s, a, "read the fields of");
        var scene = await s.OpenAsync(t.ScenePath!);
        var comps = scene.Find<GameObject>(t.FileId)!.Components.ToList();
        if (Str(a, "component") is { } type) comps = await MatchComponentsAsync(s, comps, type);
        var field = Str(a, "field");
        var list = new JsonArray();
        foreach (var c in comps)
            list.Add(new JsonObject { ["component"] = await TypeNameAsync(s, c), ["fileId"] = Id(c.FileId), ["fields"] = Properties(c, field) });
        return new JsonObject { ["target"] = await Handles.DescribeAsync(s, t.ScenePath!, t.FileId), ["components"] = list };
    }

    static async Task<JsonNode?> SetSerializedField(CommandScope s, JsonObject a)
    {
        var t = await PlainGameObjectAsync(s, a, "set fields on");
        var scene = await s.OpenAsync(t.ScenePath!);
        var field = Str(a, "field") ?? throw CommandException.Bad("--field is required");
        var comps = scene.Find<GameObject>(t.FileId)!.Components.ToList();
        if (Str(a, "component") is { } type) comps = await MatchComponentsAsync(s, comps, type);
        var (comp, path) = FindProperty(comps, field) ?? throw CommandException.NotFound($"No component of {scene.Hierarchy.PathOf(t.FileId)} has a field '{field}'.");
        SetValue(comp, path, a["value"]);
        var result = await Handles.DescribeAsync(s, t.ScenePath!, t.FileId);
        result["set"] = new JsonObject { ["component"] = await TypeNameAsync(s, comp), ["field"] = path };
        return result;
    }

    // ── helpers ───────────────────────────────────────────────────────────

    static string? Str(JsonObject a, string key) => a[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
    static string Id(long v) => v.ToString(CultureInfo.InvariantCulture);
    static bool IsActive(HierarchyNode n) => n.Object is not GameObject go || go.IsActive;

    static async Task<(string ScenePath, UnityFile Scene, long ParentTransform)> ParentAsync(CommandScope s, JsonObject a)
    {
        if (a["parent"] is not { } ph) return (s.ScenePath, s.Scene, 0);
        var p = await Handles.ResolveAsync(s, ph, "parent");
        if (p.Kind != Target.GameObject) throw CommandException.Bad("--parent must be a GameObject (not a prefab instance or an asset).");
        var scene = await s.OpenAsync(p.ScenePath!);
        return (p.ScenePath!, scene, scene.Find<GameObject>(p.FileId)!.Transform!.FileId);
    }

    static async Task<Target> SceneTargetAsync(CommandScope s, JsonObject a)
    {
        var t = await Handles.ResolveAsync(s, a["target"]);
        if (!t.IsSceneObject) throw CommandException.Bad($"--target names an asset ({t.AssetPath}); this command takes a GameObject in a scene.");
        return t;
    }

    static async Task<Target> PlainGameObjectAsync(CommandScope s, JsonObject a, string what)
    {
        var t = await SceneTargetAsync(s, a);
        if (t.Kind == Target.Instance)
            throw new CommandException(ErrorCodes.Unsupported, $"The target is a prefab instance; to {what} it, edit the prefab (overrides on instances aren't supported here yet).");
        return t;
    }

    static async Task<PrefabRoot> RootOfAsync(CommandScope s, PrefabInstance inst) =>
        inst.SourcePrefab.Guid is { } g && await s.PrefabAsync(g) is { } root ? root
            : throw new CommandException(ErrorCodes.Unsupported, $"The prefab of {inst.Name ?? "this instance"} couldn't be read (a variant, or not in the workbench).");

    static Vector3 Vec(JsonNode? n, string what) => n is JsonArray arr && arr.Count == 3
        ? new Vector3(Num(arr[0], what), Num(arr[1], what), Num(arr[2], what)) : throw CommandException.Bad($"--{what}: expected [x,y,z]");

    static Quaternion Euler(JsonNode? n, string what) { var v = Vec(n, what); return Quaternion.Euler(v.X, v.Y, v.Z); }

    static double Num(JsonNode? n, string what) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d
        : n is JsonValue sv && sv.TryGetValue<string>(out var t) && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p
        : throw CommandException.Bad($"--{what}: '{n?.ToJsonString()}' isn't a number");

    static async Task<List<string>> TagsAsync(CommandScope s)
    {
        var tags = new List<string> { "Untagged", "Respawn", "Finish", "EditorOnly", "MainCamera", "Player", "GameController" };
        try
        {
            var tm = await s.OpenAsync("ProjectSettings/TagManager.asset");
            tags.AddRange(tm.Objects.SelectMany(o => o.Find("tags")?.Children ?? []).Select(c => c.Text).Where(t => t.Length > 0));
        }
        catch (CommandException) { /* no TagManager: the built-in tags */ }
        return tags;
    }

    static async Task<List<string>> LayersAsync(CommandScope s)
    {
        try
        {
            var tm = await s.OpenAsync("ProjectSettings/TagManager.asset");
            var layers = tm.Objects.Select(o => o.Find("layers")).FirstOrDefault(l => l != null);
            if (layers != null) return layers.Children.Select(c => c.Text).ToList();
        }
        catch (CommandException) { /* the built-in layers */ }
        return ["Default", "TransparentFX", "Ignore Raycast", "", "Water", "UI"];
    }

    // A component's type: its class, or for a script, the script's name.
    static async Task<string> TypeNameAsync(CommandScope s, UnityObject c)
    {
        if (c.ClassId != ClassId.MonoBehaviour || c.GetReference("m_Script")?.Guid is not { } g) return c.TypeName;
        return await s.PathOfAsync(g) is { } path ? Path.GetFileNameWithoutExtension(path) : c.TypeName;
    }

    static async Task<bool> HasComponentAsync(CommandScope s, IEnumerable<UnityObject> comps, string type) => (await MatchComponentsAsync(s, comps, type)).Count > 0;

    static async Task<List<UnityObject>> MatchComponentsAsync(CommandScope s, IEnumerable<UnityObject> comps, string type)
    {
        var shortName = type.Split('.').Last();
        var result = new List<UnityObject>();
        foreach (var c in comps)
            if (string.Equals(c.TypeName, shortName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(await TypeNameAsync(s, c), shortName, StringComparison.OrdinalIgnoreCase)) result.Add(c);
        return result;
    }

    static readonly HashSet<string> Internal = ["m_ObjectHideFlags", "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset", "m_GameObject", "serializedVersion", "m_EditorHideFlags", "m_EditorClassIdentifier"];

    // A component's fields by serialized name, in the CLI's shapes: vectors and colours as arrays, references as {fileID, guid, type}.
    static JsonObject Properties(UnityObject c, string? only)
    {
        var o = new JsonObject();
        foreach (var n in c.Root.Children)
        {
            if (Internal.Contains(n.Key)) continue;
            if (only != null && n.Key != only && n.Key != "m_" + Upper(only) && n.Key != only.TrimStart('_')) continue;
            o[n.Key] = ToJson(n);
        }
        return o;
    }

    static string Upper(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    static JsonNode? ToJson(YamlNode n)
    {
        switch (n.Kind)
        {
            case YamlNodeKind.Scalar:
                return YamlText.IsNumber(n.Raw) && double.TryParse(n.Raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? JsonValue.Create(d) : JsonValue.Create(n.Text);
            case YamlNodeKind.Flow when n.Items != null:
                if (ObjectReference.From(n) is { } r) return new JsonObject { ["fileID"] = Id(r.FileId), ["guid"] = r.Guid, ["type"] = r.Type };
                var keys = n.Items.Select(i => i.Key).ToList();
                if (keys.SequenceEqual(["x", "y", "z"]) || keys.SequenceEqual(["x", "y", "z", "w"]) || keys.SequenceEqual(["x", "y"]) || keys.SequenceEqual(["r", "g", "b", "a"]))
                    return new JsonArray(n.Items.Select(i => (JsonNode)YamlText.ToNumber(i.Raw)).ToArray());
                return new JsonObject(n.Items.Select(i => KeyValuePair.Create(i.Key, (JsonNode?)JsonValue.Create(i.Text))));
            case YamlNodeKind.Sequence:
                return new JsonArray(n.Children.Select(c => c.Children.Count == 0 ? ToJson(c) : ToJson(c)).ToArray());
            case YamlNodeKind.Map:
                return new JsonObject(n.Children.Select(c => KeyValuePair.Create(c.Key, ToJson(c))));
            case YamlNodeKind.FlowSequence:
                return n.Raw.Trim() == "[]" ? new JsonArray() : JsonValue.Create(n.Raw.Trim());
            default:
                return n.ReadOnly ? JsonValue.Create(n.Raw.Trim()) : null;
        }
    }

    // The component and property path for a name: "m_Mass", "mass", or a dotted path ("m_Center.x").
    static (UnityObject Comp, string Path)? FindProperty(IEnumerable<UnityObject> comps, string name)
    {
        var first = name.Split('.')[0];
        var rest = name[first.Length..];
        foreach (var candidate in new[] { first, "m_" + Upper(first), "_" + Upper(first) })
            foreach (var c in comps)
                if (c.Find(candidate + rest) != null) return (c, candidate + rest);
        return null;
    }

    static void SetValue(UnityObject c, string path, JsonNode? value)
    {
        var node = c.Find(path) ?? throw CommandException.NotFound($"{c.TypeName} has no {path}");
        switch (value)
        {
            case JsonObject o when node.Kind == YamlNodeKind.Flow && ObjectReference.From(node) != null:
                foreach (var key in new[] { "fileID", "guid", "type" })
                    if (o[key] is { } v)
                    {
                        if (node.Item(key) == null) throw new CommandException(ErrorCodes.Unsupported, $"{path} is a local reference; it can't be pointed at another asset here.");
                        c.SetRaw($"{path}.{key}", v.ToString());
                    }
                return;
            case JsonObject o:
                foreach (var (k, v) in o) SetValue(c, $"{path}.{k}", v);
                return;
            case JsonArray arr when node.Kind == YamlNodeKind.Flow && node.Items != null:
                if (arr.Count != node.Items.Count) throw CommandException.Bad($"{path} takes {node.Items.Count} values ({string.Join(",", node.Items.Select(i => i.Key))}), not {arr.Count}");
                for (var i = 0; i < arr.Count; i++) c.SetNumber($"{path}.{node.Items[i].Key}", Num(arr[i], path));
                return;
            case JsonArray arr when node.Kind == YamlNodeKind.Sequence:
                if (arr.Count != node.Children.Count) throw new CommandException(ErrorCodes.Unsupported, $"{path} has {node.Children.Count} items; changing a list's length isn't supported here.");
                for (var i = 0; i < arr.Count; i++) SetValue(c, $"{path}.{i}", arr[i]);
                return;
            case JsonValue v when node.Kind == YamlNodeKind.Flow && v.TryGetValue<string>(out var hex) && hex.StartsWith('#') && node.Item("r") != null:
                var rgba = Hex(hex);
                foreach (var (k, x) in new[] { ("r", rgba[0]), ("g", rgba[1]), ("b", rgba[2]), ("a", rgba[3]) }) c.SetNumber($"{path}.{k}", x);
                return;
            case JsonValue v when v.TryGetValue<bool>(out var b):
                c.SetBool(path, b);
                return;
            case JsonValue v when v.TryGetValue<double>(out var d):
                c.SetNumber(path, d.ToString("R", CultureInfo.InvariantCulture));
                return;
            case JsonValue v when v.TryGetValue<string>(out var str):
                if (YamlText.IsNumber(node.Raw) && YamlText.IsNumber(str)) c.SetNumber(path, str);
                else c.SetString(path, str);
                return;
            default:
                throw CommandException.Bad($"{path}: can't set {value?.ToJsonString() ?? "null"}");
        }
    }

    static double[] Hex(string s)
    {
        var h = s.TrimStart('#');
        if (h.Length is not (6 or 8) || !uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var u)) throw CommandException.Bad($"'{s}' isn't #RRGGBB or #RRGGBBAA");
        if (h.Length == 6) u = (u << 8) | 0xFF;
        return [((u >> 24) & 0xFF) / 255.0, ((u >> 16) & 0xFF) / 255.0, ((u >> 8) & 0xFF) / 255.0, (u & 0xFF) / 255.0];
    }
}
