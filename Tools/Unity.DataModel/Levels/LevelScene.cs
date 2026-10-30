using System;
using System.Collections.Generic;
using System.Linq;

namespace Unity.DataModel.Levels
{
    public enum PlaceableKind { Instance, Object }

    /// <summary>Something a scene places: a prefab instance, or an object with a mesh.</summary>
    public sealed class Placeable
    {
        internal Placeable(long id, PlaceableKind kind, string name, Pose parentWorld, Pose local)
        {
            Id = id;
            Kind = kind;
            Name = name;
            ParentWorld = parentWorld;
            Local = local;
            World = Pose.Compose(parentWorld, local);
        }

        /// <summary>The prefab instance's file ID, or the GameObject's.</summary>
        public long Id { get; }
        public PlaceableKind Kind { get; }
        public string Name { get; }

        /// <summary>The asset to show: the source prefab's GUID, or the mesh's (null for a built-in primitive).</summary>
        public string? AssetGuid { get; internal set; }

        /// <summary>"Cube", "Sphere"… for Unity's built-in meshes.</summary>
        public string? Primitive { get; internal set; }
        public long? MeshFileId { get; internal set; }

        /// <summary>For an object: its Transform.</summary>
        public long? TransformId { get; internal set; }

        public Pose ParentWorld { get; }
        public Pose Local { get; }
        public Pose World { get; }
    }

    public sealed class SkippedPlaceable
    {
        internal SkippedPlaceable(long id, string reason) { Id = id; Reason = reason; }
        public long Id { get; }
        public string Reason { get; }
    }

    /// <summary>A prefab instance to add to a level, placed in world space at the scene's root.</summary>
    public sealed class NewInstance
    {
        public NewInstance(string sourcePrefabGuid, string? name, Pose world, long? fileId = null)
        {
            SourcePrefabGuid = sourcePrefabGuid;
            Name = name;
            World = world;
            FileId = fileId;
        }

        public string SourcePrefabGuid { get; }
        public string? Name { get; }
        public Pose World { get; }
        public long? FileId { get; }
    }

    /// <summary>
    /// A scene as a level: what it places (prefab instances, objects with a mesh) and where in the world
    /// (through the parent chain), and the changes that move them and add prefab instances. Prefab roots
    /// (<see cref="PrefabRoot.Read"/>) are passed in by GUID: reading them is the caller's (they live in
    /// other files).
    /// </summary>
    public sealed class LevelScene
    {
        static readonly Dictionary<long, string> Primitives = new Dictionary<long, string>
        {
            [10202] = "Cube", [10206] = "Cylinder", [10207] = "Sphere", [10208] = "Capsule", [10209] = "Plane", [10210] = "Quad",
        };
        const string BuiltinExtra = "0000000000000000e000000000000000";

        readonly IReadOnlyDictionary<string, PrefabRoot?> prefabs;

        public LevelScene(UnityFile scene, IReadOnlyDictionary<string, PrefabRoot?> prefabs)
        {
            File = scene;
            this.prefabs = prefabs;
        }

        public UnityFile File { get; }

        /// <summary>The prefab roots it was given, by GUID.</summary>
        public IReadOnlyDictionary<string, PrefabRoot?> Prefabs => prefabs;

        public IReadOnlyList<PrefabInstance> Instances => File.Objects.OfType<PrefabInstance>().Where(i => !i.IsStripped).ToList();

        /// <summary>The prefabs the scene's instances come from.</summary>
        public static IReadOnlyList<string> PrefabGuids(UnityFile scene) => scene.Objects.OfType<PrefabInstance>()
            .Where(i => !i.IsStripped).Select(i => i.SourcePrefab.Guid).Where(g => g != null).Select(g => g!).Distinct().ToList();

        /// <summary>GameObjects with a Transform and a mesh (outside prefab instances).</summary>
        public IReadOnlyList<(GameObject GameObject, Transform Transform, ObjectReference Mesh)> MeshObjects
        {
            get
            {
                var list = new List<(GameObject, Transform, ObjectReference)>();
                foreach (var go in File.Objects.OfType<GameObject>().Where(g => !g.IsStripped))
                {
                    var comps = go.Components;
                    var t = comps.OfType<Transform>().FirstOrDefault();
                    var mf = comps.FirstOrDefault(c => c.ClassId == DataModel.ClassId.MeshFilter);
                    if (t == null || mf == null) continue;
                    var mesh = mf.GetReference("m_Mesh");
                    if (mesh == null || mesh.Value.IsNone) continue;
                    list.Add((go, t, mesh.Value));
                }
                return list;
            }
        }

        PrefabRoot? RootOf(string? guid) => guid != null && prefabs.TryGetValue(guid, out var r) ? r : null;

        Pose InstanceLocal(PrefabInstance inst) => RootOf(inst.SourcePrefab.Guid) is { } root ? inst.RootPose(root) : Pose.Identity;

        // World pose of a transform: through its fathers. A stripped transform (inside a prefab instance) is
        // placed at its instance's root; its offset inside the prefab isn't read.
        Pose WorldOf(long id, Dictionary<long, Pose> cache, int depth = 0)
        {
            if (id == 0 || depth > 64) return Pose.Identity;
            if (cache.TryGetValue(id, out var known)) return known;
            Pose w;
            var t = File.Find<Transform>(id);
            if (t == null) w = Pose.Identity;
            else if (t.IsStripped)
            {
                var inst = File.Find<PrefabInstance>(t.PrefabInstanceId);
                w = inst != null && !inst.IsStripped ? Pose.Compose(WorldOf(inst.TransformParentId, cache, depth + 1), InstanceLocal(inst)) : Pose.Identity;
            }
            else w = Pose.Compose(WorldOf(t.FatherId, cache, depth + 1), t.LocalPose);
            cache[id] = w;
            return w;
        }

        /// <summary>
        /// The world pose of a Transform (by its ID or its GameObject's), or of a prefab instance's root; identity
        /// for anything else (0: the scene's root).
        /// </summary>
        public Pose WorldPose(long id)
        {
            var cache = new Dictionary<long, Pose>();
            if (File.Find(id) is PrefabInstance inst) return Pose.Compose(WorldOf(inst.TransformParentId, cache), InstanceLocal(inst));
            if (File.Find(id) is GameObject go && go.Transform != null) id = go.Transform.FileId;
            return WorldOf(id, cache);
        }

        /// <summary>Everything placed, with world poses; instances whose prefab is unknown are skipped.</summary>
        public (IReadOnlyList<Placeable> Items, IReadOnlyList<SkippedPlaceable> Skipped) Placeables()
        {
            var cache = new Dictionary<long, Pose>();
            var items = new List<Placeable>();
            var skipped = new List<SkippedPlaceable>();
            foreach (var inst in Instances)
            {
                var root = RootOf(inst.SourcePrefab.Guid);
                if (root == null)
                {
                    skipped.Add(new SkippedPlaceable(inst.FileId, "its prefab couldn't be read (a prefab variant, or not in this workbench)"));
                    continue;
                }
                var name = inst.Name is { Length: > 0 } n ? n : root.Name is { Length: > 0 } rn ? rn : inst.FileId.ToString();
                items.Add(new Placeable(inst.FileId, PlaceableKind.Instance, name, WorldOf(inst.TransformParentId, cache), inst.RootPose(root))
                    { AssetGuid = inst.SourcePrefab.Guid });
            }
            foreach (var (go, t, mesh) in MeshObjects)
            {
                if (t.IsStripped) continue;
                var primitive = mesh.Guid == BuiltinExtra && Primitives.TryGetValue(mesh.FileId, out var p) ? p : null;
                items.Add(new Placeable(go.FileId, PlaceableKind.Object, go.Name ?? go.FileId.ToString(), WorldOf(t.FatherId, cache), t.LocalPose)
                    { AssetGuid = primitive != null ? null : mesh.Guid, Primitive = primitive, MeshFileId = mesh.FileId, TransformId = t.FileId });
            }
            return (items, skipped);
        }

        /// <summary>
        /// Move a placed item to <paramref name="world"/>: writes its local values (relative to its parent) to
        /// its Transform, or to its prefab instance's overrides.
        /// </summary>
        public void Move(Placeable item, Pose world)
        {
            var local = Pose.ToLocal(item.ParentWorld, world);
            if (item.Kind == PlaceableKind.Object)
            {
                var t = File.Find<Transform>(item.TransformId ?? 0) ?? throw new InvalidOperationException($"{item.Name} has no Transform.");
                t.SetLocalPose(local);
                return;
            }
            var inst = File.Find<PrefabInstance>(item.Id) ?? throw new InvalidOperationException($"&{item.Id} isn't a prefab instance.");
            var root = RootOf(inst.SourcePrefab.Guid) ?? throw new InvalidOperationException($"{item.Name}: its prefab's root is unknown.");
            inst.SetRootPose(root, local);
        }

        /// <summary>Add a prefab instance at the scene's root. Returns its file ID.</summary>
        public long Add(NewInstance added)
        {
            var root = RootOf(added.SourcePrefabGuid) ?? throw new InvalidOperationException($"The prefab {added.SourcePrefabGuid} has no plain root (a prefab variant?).");
            return File.AddPrefabInstance(added.SourcePrefabGuid, root, added.Name, Pose.ToLocal(Pose.Identity, added.World), added.FileId);
        }
    }
}
