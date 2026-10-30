using System.Collections.Generic;
using System.Linq;
using Unity.DataModel.Yaml;

namespace Unity.DataModel
{
    /// <summary>What changed between two versions of a file: objects created and deleted, and the fields changed in each other object.</summary>
    public sealed class FileDiff
    {
        FileDiff(List<UnityObject> created, List<UnityObject> deleted, Dictionary<long, List<string>> updated)
        {
            Created = created;
            Deleted = deleted;
            Updated = updated;
        }

        /// <summary>Objects in the new version only.</summary>
        public IReadOnlyList<UnityObject> Created { get; }

        /// <summary>Objects in the old version only.</summary>
        public IReadOnlyList<UnityObject> Deleted { get; }

        /// <summary>For each object in both: the property paths whose values differ.</summary>
        public IReadOnlyDictionary<long, List<string>> Updated { get; }

        public bool IsEmpty => Created.Count == 0 && Deleted.Count == 0 && Updated.Count == 0;

        public static FileDiff Between(UnityFile before, UnityFile after)
        {
            var old = before.Objects.ToDictionary(o => o.FileId);
            var now = after.Objects.ToDictionary(o => o.FileId);
            var updated = new Dictionary<long, List<string>>();
            foreach (var o in after.Objects)
            {
                if (!old.TryGetValue(o.FileId, out var was)) continue;
                var a = Values(was.Root);
                var b = Values(o.Root);
                var fields = a.Keys.Union(b.Keys).Where(k => !a.TryGetValue(k, out var x) || !b.TryGetValue(k, out var y) || x != y).ToList();
                if (fields.Count > 0) updated[o.FileId] = fields;
            }
            return new FileDiff(after.Objects.Where(o => !old.ContainsKey(o.FileId)).ToList(),
                before.Objects.Where(o => !now.ContainsKey(o.FileId)).ToList(), updated);
        }

        // Every value by property path: scalars and flow-map entries, and a value's whole text where it can't be split.
        static Dictionary<string, string> Values(YamlNode root)
        {
            var result = new Dictionary<string, string>();
            void Walk(YamlNode n)
            {
                if (n.Items != null) foreach (var i in n.Items) result[i.Path] = i.Raw;
                else if (n.Children.Count == 0 && n.Path.Length > 0) result[n.Path] = n.Raw;
                foreach (var c in n.Children) Walk(c);
                // A list's length (an item removed at the end leaves no other trace).
                if (n.Kind == YamlNodeKind.Sequence) result[n.Path + ".#"] = n.Children.Count.ToString();
            }
            Walk(root);
            return result;
        }
    }
}
