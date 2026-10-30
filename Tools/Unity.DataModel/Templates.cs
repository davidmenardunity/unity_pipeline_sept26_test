using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Unity.DataModel
{
    /// <summary>
    /// Documents written by the Unity Editor (6000.7) that new objects are cloned from: an empty GameObject, the
    /// built-in primitives, and common components (Templates/Unity6000.7.unity, saved by the Editor). Cloning keeps
    /// every field and serializedVersion as the Editor writes them; only file IDs and the links between them change.
    /// </summary>
    public static class Templates
    {
        static readonly Lazy<UnityFile> File = new Lazy<UnityFile>(() =>
        {
            using var stream = typeof(Templates).Assembly.GetManifestResourceStream("Unity.DataModel.Templates.Unity6000.7.unity")
                ?? throw new InvalidOperationException("The templates resource is missing from Unity.DataModel.dll.");
            using var reader = new StreamReader(stream);
            return UnityFile.Parse(reader.ReadToEnd().Replace("\r\n", "\n"));
        });

        /// <summary>The built-in primitives, by the name the CLI takes (case-insensitive).</summary>
        public static IReadOnlyList<string> Primitives { get; } = new[] { "Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad" };

        /// <summary>The component types that can be added by name.</summary>
        public static IReadOnlyList<string> ComponentTypes => ComponentDocs.Value.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

        static readonly Lazy<Dictionary<string, UnityObject>> ComponentDocs = new Lazy<Dictionary<string, UnityObject>>(() =>
        {
            var docs = new Dictionary<string, UnityObject>(StringComparer.OrdinalIgnoreCase);
            // Components from the "Components…" objects (no mesh set), else from a primitive.
            foreach (var go in File.Value.Objects.OfType<GameObject>().OrderBy(g => g.Name?.StartsWith("Components") == true ? 0 : 1))
                foreach (var c in go.Components.Where(c => !(c is Transform)))
                    if (!docs.ContainsKey(c.TypeName)) docs[c.TypeName] = c;
            return docs;
        });

        static GameObject Named(string name) => File.Value.Objects.OfType<GameObject>().FirstOrDefault(g => g.Name == name)
            ?? throw new InvalidOperationException($"No template object {name}.");

        public static string? PrimitiveName(string? primitive) => string.IsNullOrEmpty(primitive) ? null
            : Primitives.FirstOrDefault(p => string.Equals(p, primitive, StringComparison.OrdinalIgnoreCase));

        public static bool HasComponent(string type) => ComponentDocs.Value.ContainsKey(type);

        public static string ComponentTypeName(string type) => ComponentDocs.Value.TryGetValue(type, out var c) ? c.TypeName : type;

        /// <summary>A GameObject (empty, or a primitive with its mesh, renderer and collider), as document lines with new IDs.</summary>
        internal static (List<string> Lines, long GameObjectId, long TransformId) GameObject(string? primitive, Func<long> newId)
        {
            // "Child" is the template's empty GameObject with no children.
            var go = Named(PrimitiveName(primitive) ?? "Child");
            var docs = new List<UnityObject> { go };
            docs.AddRange(go.Components);
            var map = docs.ToDictionary(d => d.FileId, _ => newId());
            return (Clone(docs, map), map[go.FileId], map[go.Transform!.FileId]);
        }

        /// <summary>A component for <paramref name="gameObjectId"/>, as document lines with a new ID.</summary>
        internal static (List<string> Lines, long Id) Component(string type, long gameObjectId, Func<long> newId)
        {
            if (!ComponentDocs.Value.TryGetValue(type, out var doc)) throw new KeyNotFoundException($"No component type {type}. Known: {string.Join(", ", ComponentTypes)}.");
            var id = newId();
            var map = new Dictionary<long, long> { [doc.FileId] = id, [doc.GetReference("m_GameObject")?.FileId ?? 0] = gameObjectId };
            return (Clone(new[] { doc }, map), id);
        }

        static readonly Regex LocalRef = new Regex(@"\{fileID: (-?\d+)\}");

        // Each document's lines, with its header's ID and every local reference to a mapped ID replaced.
        static List<string> Clone(IEnumerable<UnityObject> docs, Dictionary<long, long> map)
        {
            var lines = File.Value.Lines;
            var all = File.Value.Objects.Select(o => o.Document.Line).OrderBy(l => l).ToList();
            var result = new List<string>();
            foreach (var d in docs)
            {
                var start = d.Document.Line;
                var end = all.FirstOrDefault(l => l > start);
                if (end == 0) end = lines.Count;
                while (end > start && lines[end - 1].Trim().Length == 0) end--;
                for (var i = start; i < end; i++)
                {
                    var line = lines[i];
                    if (i == start) line = $"--- !u!{d.ClassId.ToString(CultureInfo.InvariantCulture)} &{map[d.FileId].ToString(CultureInfo.InvariantCulture)}";
                    // References inside the clone follow it; any other local reference (to a template object that
                    // isn't cloned, like a parent) becomes none.
                    else line = LocalRef.Replace(line, m => !long.TryParse(m.Groups[1].Value, out var old) || old == 0 ? m.Value
                        : $"{{fileID: {(map.TryGetValue(old, out var now) ? now : 0).ToString(CultureInfo.InvariantCulture)}}}");
                    result.Add(line);
                }
            }
            return result;
        }
    }
}
