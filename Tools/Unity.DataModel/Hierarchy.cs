using System.Collections.Generic;
using System.Linq;

namespace Unity.DataModel
{
    public enum HierarchyNodeKind { GameObject, PrefabInstance, Object }

    public sealed class HierarchyNode
    {
        internal HierarchyNode(UnityObject obj, HierarchyNodeKind kind, string label)
        {
            Object = obj;
            Kind = kind;
            Label = label;
        }

        public UnityObject Object { get; }
        public HierarchyNodeKind Kind { get; }
        public string Label { get; }
        public List<HierarchyNode> Children { get; } = new List<HierarchyNode>();
    }

    /// <summary>
    /// A file's objects as Unity's Hierarchy shows them: GameObjects under their parents (in m_Children
    /// order), prefab instances under the transform they hang from, and the objects that are neither
    /// (components aside) in <see cref="Others"/>.
    /// </summary>
    public sealed class Hierarchy
    {
        internal Hierarchy(UnityFile file)
        {
            var gos = file.Objects.OfType<GameObject>().Where(g => !g.IsStripped).ToList();
            var transformOf = new Dictionary<long, Transform>();
            var goOfTransform = new Dictionary<long, GameObject>();
            foreach (var go in gos)
            {
                var comps = go.Components;
                componentsOf[go.FileId] = comps;
                if (comps.OfType<Transform>().FirstOrDefault() is { } t)
                {
                    transformOf[go.FileId] = t;
                    goOfTransform[t.FileId] = go;
                }
            }
            var instances = file.Objects.OfType<PrefabInstance>().Where(d => !d.IsStripped).ToList();

            var goNode = gos.ToDictionary(g => g.FileId, g => new HierarchyNode(g, HierarchyNodeKind.GameObject, g.Name ?? "(unnamed)"));
            var instNode = instances.ToDictionary(d => d.FileId, d => new HierarchyNode(d, HierarchyNodeKind.PrefabInstance, d.Name ?? "Prefab instance"));
            // A stripped transform stands in for an object inside a prefab instance.
            HierarchyNode? StrippedOwner(long id) =>
                file.Find(id) is { IsStripped: true } d && d.GetReference("m_PrefabInstance") is { } inst && instNode.TryGetValue(inst.FileId, out var n) ? n : null;

            void Attach(long parentTransformId, HierarchyNode n)
            {
                var parent = goOfTransform.TryGetValue(parentTransformId, out var owner) ? goNode[owner.FileId] : StrippedOwner(parentTransformId);
                (parent?.Children ?? Roots).Add(n);
            }
            foreach (var go in gos) Attach(transformOf.TryGetValue(go.FileId, out var t) ? t.FatherId : 0, goNode[go.FileId]);
            foreach (var d in instances) Attach(d.TransformParentId, instNode[d.FileId]);

            // Keep each parent's children in its transform's m_Children order.
            void Order(HierarchyNode n)
            {
                var ids = n.Kind == HierarchyNodeKind.GameObject && transformOf.TryGetValue(n.Object.FileId, out var t) ? t.ChildIds.ToList() : new List<long>();
                int Rank(HierarchyNode c)
                {
                    if (c.Kind != HierarchyNodeKind.GameObject || !transformOf.TryGetValue(c.Object.FileId, out var ct)) return int.MaxValue;
                    var i = ids.IndexOf(ct.FileId);
                    return i < 0 ? int.MaxValue : i;
                }
                var sorted = n.Children.OrderBy(Rank).ToList();   // stable, like Array.sort
                n.Children.Clear();
                n.Children.AddRange(sorted);
                n.Children.ForEach(Order);
            }
            Roots.ForEach(Order);

            var used = new HashSet<long>(gos.Select(g => g.FileId).Concat(instances.Select(d => d.FileId)));
            foreach (var comps in componentsOf.Values) foreach (var c in comps) used.Add(c.FileId);
            Others = file.Objects.Where(d => !used.Contains(d.FileId) && !d.IsStripped)
                .Select(d => new HierarchyNode(d, HierarchyNodeKind.Object, d.Name ?? d.TypeName)).ToList();
        }

        readonly Dictionary<long, IReadOnlyList<UnityObject>> componentsOf = new Dictionary<long, IReadOnlyList<UnityObject>>();

        public List<HierarchyNode> Roots { get; } = new List<HierarchyNode>();
        public IReadOnlyList<HierarchyNode> Others { get; }

        public IReadOnlyList<UnityObject> ComponentsOf(GameObject go) =>
            componentsOf.TryGetValue(go.FileId, out var c) ? c : new List<UnityObject>();
    }
}
