using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Unity.DataModel.Yaml;

namespace Unity.DataModel
{
    /// <summary>
    /// A Unity YAML file (a scene, prefab, ScriptableObject or material) as objects you can read and change.
    /// Changes are kept as edits to the text that was read: <see cref="ToText"/> rewrites only the characters
    /// of the values that changed and the lines that were added, so the rest of the file stays byte for byte.
    /// </summary>
    /// <example>
    /// <code>
    /// var scene = UnityFile.Parse(File.ReadAllText("Level.unity"));
    /// var t = scene.Objects.OfType&lt;GameObject&gt;().First(g => g.Name == "Crate").Transform!;
    /// t.SetLocalPosition(new Vector3(0, 1, 0));
    /// File.WriteAllText("Level.unity", scene.ToText());
    /// </code>
    /// </example>
    public sealed class UnityFile
    {
        readonly YamlFile yaml;
        readonly Dictionary<long, UnityObject> byId = new Dictionary<long, UnityObject>();
        readonly Dictionary<(int Line, int Start), ValueChange> values = new Dictionary<(int, int), ValueChange>();
        // Structural changes, written out by ToText.
        readonly List<(PrefabInstance Instance, string Text)> addedModifications = new List<(PrefabInstance, string)>();
        readonly List<(long FileId, string Text)> addedDocuments = new List<(long, string)>();
        readonly List<long> addedRoots = new List<long>();
        Hierarchy? hierarchy;

        UnityFile(string text)
        {
            Text = text;
            yaml = UnityYamlParser.Parse(text);
            foreach (var doc in yaml.Documents)
            {
                var obj = UnityObject.Create(this, doc);
                byId[doc.FileId] = obj;   // a duplicated id: the later one wins, as when Unity loads it
            }
            Objects = yaml.Documents.Select(d => byId[d.FileId]).Distinct().ToList();
        }

        /// <summary>Read a file. It must be text (Asset Serialization: Force Text); see <see cref="IsYaml"/>.</summary>
        public static UnityFile Parse(string text)
        {
            if (!IsYaml(text)) throw new FormatException("This isn't a Unity YAML (text) file. Set Asset Serialization to Force Text to edit it.");
            return new UnityFile(text);
        }

        public static bool IsYaml(string text) => text.StartsWith("%YAML");

        /// <summary>The text that was read.</summary>
        public string Text { get; }

        public string LineEnding => yaml.Eol;
        public IReadOnlyList<string> Lines => yaml.Lines;

        /// <summary>Every object, in file order.</summary>
        public IReadOnlyList<UnityObject> Objects { get; }

        public UnityObject? Find(long fileId) => byId.TryGetValue(fileId, out var o) ? o : null;
        public T? Find<T>(long fileId) where T : UnityObject => Find(fileId) as T;
        public UnityObject? Find(ObjectReference r) => r.IsLocal && !r.IsNone ? Find(r.FileId) : null;

        /// <summary>GameObjects with their children, prefab instances under their parents, and other objects.</summary>
        public Hierarchy Hierarchy => hierarchy ??= new Hierarchy(this);

        /// <summary>The scene's root list (scenes saved by Unity 6.7 and later), or null.</summary>
        public SceneRoots? SceneRoots => Objects.OfType<SceneRoots>().FirstOrDefault();

        // ── changes ─────────────────────────────────────────────────────────

        public bool HasChanges => values.Count > 0 || addedModifications.Count > 0 || addedDocuments.Count > 0;

        /// <summary>The values changed so far (each value once, with its latest text).</summary>
        public IReadOnlyList<ValueChange> Changes => values.Values.OrderBy(v => v.Node.Line).ThenBy(v => v.Node.Start).ToList();

        /// <summary>Object IDs added with <see cref="AddPrefabInstance"/>, in order.</summary>
        public IReadOnlyList<long> AddedObjects => addedDocuments.Select(d => d.FileId).ToList();

        internal void SetRaw(UnityObject owner, YamlNode node, string raw)
        {
            if (node.ReadOnly || node.Kind != YamlNodeKind.Scalar)
                throw new InvalidOperationException($"{owner.TypeName} &{owner.FileId}: {node.Path} spans several lines or holds other values, so it can't be set in place.");
            if (raw.IndexOf('\n') >= 0 || raw.IndexOf('\r') >= 0)
                throw new ArgumentException("A value must fit on one line.", nameof(raw));
            node.Raw = raw;
            var key = (node.Line, node.Start);
            if (raw == node.OriginalRaw) values.Remove(key);
            else values[key] = new ValueChange(owner, node);
        }

        internal void AddModifications(PrefabInstance instance, string text) => addedModifications.Add((instance, text));

        /// <summary>A file ID not used in this file (a random positive 64-bit number, as Unity mints them).</summary>
        public long NewFileId()
        {
            lock (Rng)
            {
                for (;;)
                {
                    var bytes = new byte[8];
                    Rng.NextBytes(bytes);
                    var id = (long)(BitConverter.ToUInt64(bytes, 0) & ((1UL << 62) - 1)) + 1000000000L;
                    if (!byId.ContainsKey(id) && addedDocuments.All(d => d.FileId != id)) return id;
                }
            }
        }

        static readonly Random Rng = new Random();

        /// <summary>
        /// Add an instance of a prefab at the scene's root, named <paramref name="name"/> and placed at
        /// <paramref name="local"/>. Returns its file ID. It's in the text (<see cref="ToText"/>); read the text
        /// again (<see cref="ApplyChanges"/>) to use it as an object.
        /// </summary>
        public long AddPrefabInstance(string sourcePrefabGuid, PrefabRoot root, string? name, Pose local, long? fileId = null)
        {
            if (string.IsNullOrEmpty(sourcePrefabGuid)) throw new ArgumentException("The prefab's GUID is required.", nameof(sourcePrefabGuid));
            var id = fileId ?? NewFileId();
            if (byId.ContainsKey(id) || addedDocuments.Any(d => d.FileId == id)) throw new ArgumentException($"&{id} is already in this file.", nameof(fileId));
            var target = new ObjectReference(root.RootTransformId, sourcePrefabGuid, 3);
            var mods = new List<string>();
            if (root.RootGameObjectId != 0)
                mods.Add(PrefabInstance.ModificationText("    ", new ObjectReference(root.RootGameObjectId, sourcePrefabGuid, 3), "m_Name",
                    YamlText.FormatString(name ?? root.Name ?? "Prefab", "")));
            foreach (var (path, pick) in PrefabInstance.PoseProperties)
                if (!path.StartsWith("m_LocalScale") || Math.Abs(pick(local) - 1) >= 1e-6)
                    mods.Add(PrefabInstance.ModificationText("    ", target, path, YamlText.FormatNumber(pick(local))));
            var lines = new List<string>
            {
                $"--- !u!1001 &{id.ToString(CultureInfo.InvariantCulture)}",
                "PrefabInstance:",
                "  m_ObjectHideFlags: 0",
                "  serializedVersion: 2",
                "  m_Modification:",
                "    serializedVersion: 3",
                "    m_TransformParent: {fileID: 0}",
                "    m_Modifications:",
            };
            lines.AddRange(mods);
            lines.AddRange(new[]
            {
                "    m_RemovedComponents: []",
                "    m_RemovedGameObjects: []",
                "    m_AddedGameObjects: []",
                "    m_AddedComponents: []",
                $"  m_SourcePrefab: {{fileID: 100100000, guid: {sourcePrefabGuid}, type: 3}}",
            });
            addedDocuments.Add((id, string.Join("\n", lines)));
            addedRoots.Add(id);
            return id;
        }

        /// <summary>The file's text with every change.</summary>
        public string ToText()
        {
            if (!HasChanges) return Text;
            var edits = new List<TextEdit>();
            foreach (var v in values.Values) edits.Add(TextEdit.Replace(v.Node.Line, v.Node.Start, v.Node.End, v.Node.Raw));

            foreach (var group in addedModifications.GroupBy(m => m.Instance))
            {
                var mods = group.Key.ModificationsNode;
                if (mods == null) throw new InvalidOperationException($"PrefabInstance &{group.Key.FileId} has no m_Modifications list.");
                var text = string.Join("\n", group.Select(m => m.Text));
                if (mods.Kind == YamlNodeKind.FlowSequence) edits.Add(TextEdit.Replace(mods.Line, mods.Start - 1, mods.End, "\n" + text));
                else edits.Add(TextEdit.InsertAfter(mods.LastLine, text));
            }

            if (addedDocuments.Count > 0)
            {
                // New documents go before SceneRoots (Unity keeps it last), else at the end of the file.
                var rootsDoc = SceneRoots;
                var last = rootsDoc != null ? rootsDoc.Document.Line - 1 : yaml.Lines.Count - 1;
                while (last > 0 && yaml.Lines[last].Trim().Length == 0) last--;
                edits.Add(TextEdit.InsertAfter(last, string.Join("\n", addedDocuments.Select(d => d.Text))));
                var roots = rootsDoc?.RootsNode;
                if (roots != null)
                {
                    var indent = new string(' ', roots.ListIndent ?? roots.Indent);
                    var text = string.Join("\n", addedRoots.Select(id => $"{indent}- {{fileID: {id.ToString(CultureInfo.InvariantCulture)}}}"));
                    if (roots.Kind == YamlNodeKind.FlowSequence) edits.Add(TextEdit.Replace(roots.Line, roots.Start - 1, roots.End, "\n" + text));
                    else edits.Add(TextEdit.InsertAfter(roots.LastLine, text));
                }
            }
            return TextEdit.Apply(yaml.Lines, yaml.Eol, edits);
        }

        /// <summary>The file read again from <see cref="ToText"/>: added objects become objects of the model.</summary>
        public UnityFile ApplyChanges() => new UnityFile(ToText());

        /// <summary>Forget every change.</summary>
        public void Revert()
        {
            foreach (var v in values.Values) v.Node.Raw = v.Node.OriginalRaw;
            values.Clear();
            addedModifications.Clear();
            addedDocuments.Clear();
            addedRoots.Clear();
        }
    }

    /// <summary>A value that was changed: which object, which property, from what to what.</summary>
    public sealed class ValueChange
    {
        internal ValueChange(UnityObject owner, YamlNode node) { Object = owner; Node = node; }
        public UnityObject Object { get; }
        public YamlNode Node { get; }
        public string PropertyPath => Node.Path;
        public string OldRaw => Node.OriginalRaw;
        public string NewRaw => Node.Raw;
    }

    sealed class TextEdit
    {
        public int Line;
        public int Start;
        public int End;
        public string Text = "";
        public bool Insert;

        public static TextEdit Replace(int line, int start, int end, string text) => new TextEdit { Line = line, Start = start, End = end, Text = text };
        public static TextEdit InsertAfter(int line, string text) => new TextEdit { Line = line, Insert = true, Text = text };

        // Replacements on a line apply right to left; inserted lines go after the line, in order.
        public static string Apply(IReadOnlyList<string> original, string eol, List<TextEdit> edits)
        {
            var lines = original.ToArray();
            foreach (var group in edits.Where(e => !e.Insert).GroupBy(e => e.Line))
            {
                var s = lines[group.Key];
                foreach (var e in group.OrderByDescending(e => e.Start))
                    s = s.Substring(0, e.Start) + e.Text.Replace("\n", eol) + s.Substring(e.End);
                lines[group.Key] = s;
            }
            foreach (var group in edits.Where(e => e.Insert).GroupBy(e => e.Line))
            {
                var sb = new StringBuilder(lines[group.Key]);
                foreach (var e in group) sb.Append(eol).Append(e.Text.Replace("\n", eol));
                lines[group.Key] = sb.ToString();
            }
            return string.Join(eol, lines);
        }
    }
}
