using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Unity.DataModel.Yaml;

namespace Unity.DataModel
{
    /// <summary>
    /// One object of a file (a GameObject, a component, a prefab instance, a ScriptableObject…). Its
    /// serialized fields are addressed by property path: keys and list indices joined with ".", as in
    /// <c>m_LocalPosition.x</c> or <c>m_Materials.0</c>.
    /// </summary>
    public class UnityObject
    {
        internal UnityObject(UnityFile file, YamlDocument document)
        {
            File = file;
            Document = document;
        }

        internal static UnityObject Create(UnityFile file, YamlDocument doc) => doc.ClassId switch
        {
            DataModel.ClassId.GameObject => new GameObject(file, doc),
            DataModel.ClassId.Transform or DataModel.ClassId.RectTransform => new Transform(file, doc),
            DataModel.ClassId.PrefabInstance => new PrefabInstance(file, doc),
            DataModel.ClassId.SceneRoots => new SceneRoots(file, doc),
            _ => new UnityObject(file, doc),
        };

        public UnityFile File { get; }
        public YamlDocument Document { get; }
        public long FileId => Document.FileId;
        public int ClassId => Document.ClassId;
        public string TypeName => Document.TypeName;
        public bool IsStripped => Document.Stripped;

        /// <summary>The serialized fields.</summary>
        public YamlNode Root => Document.Root;

        /// <summary>m_Name, or null when it's missing or empty.</summary>
        public string? Name => GetString("m_Name") is { Length: > 0 } n ? n : null;

        /// <summary>A reference to this object from inside the same file.</summary>
        public ObjectReference Reference => new ObjectReference(FileId);

        // ── reading ─────────────────────────────────────────────────────────

        /// <summary>The value at a property path, or null.</summary>
        public YamlNode? Find(string path)
        {
            var node = Root;
            foreach (var key in path.Split('.'))
            {
                var next = node.Child(key) ?? node.Item(key);
                if (next == null) return null;
                node = next;
            }
            return node;
        }

        public string? GetString(string path) => Find(path) is { } n ? YamlText.Unquote(n.Raw) : null;
        public double? GetNumber(string path) => Find(path) is { } n ? YamlText.ToNumber(n.Raw) : (double?)null;
        public bool? GetBool(string path) => Find(path) is { } n ? n.Raw.Trim() != "0" : (bool?)null;
        public ObjectReference? GetReference(string path) => ObjectReference.From(Find(path));

        public Vector3? GetVector3(string path) => Find(path) is { Items: { } } n
            ? new Vector3(Num(n, "x"), Num(n, "y"), Num(n, "z")) : (Vector3?)null;

        /// <summary>A quaternion, normalized.</summary>
        public Quaternion? GetQuaternion(string path) => Find(path) is { Items: { } } n
            ? new Quaternion(Num(n, "x"), Num(n, "y"), Num(n, "z"), Num(n, "w")).Normalized : (Quaternion?)null;

        static double Num(YamlNode flow, string key) => YamlText.ToNumber(flow.Item(key)?.Raw);

        // ── writing ─────────────────────────────────────────────────────────

        YamlNode Require(string path) => Find(path) ?? throw new KeyNotFoundException($"{TypeName} has no property {path}.");

        /// <summary>Set a value's text exactly as it should be written.</summary>
        public void SetRaw(string path, string raw) => File.SetRaw(this, Require(path), raw);

        /// <summary>Set a string, quoted as Unity would write it.</summary>
        public void SetString(string path, string text)
        {
            var node = Require(path);
            File.SetRaw(this, node, YamlText.FormatString(text, node.OriginalRaw));
        }

        /// <summary>Set a number, written as typed (it must be a number literal).</summary>
        public void SetNumber(string path, string literal)
        {
            if (!YamlText.IsNumber(literal)) throw new FormatException($"{literal} isn't a number.");
            SetRaw(path, literal.Trim());
        }

        /// <summary>Set a number. Unchanged when it's within 1e-6 of the current value.</summary>
        public void SetNumber(string path, double value)
        {
            var node = Require(path);
            if (Math.Abs(YamlText.ToNumber(node.Raw) - value) < 1e-6) return;
            File.SetRaw(this, node, YamlText.FormatNumber(value));
        }

        public void SetBool(string path, bool value) => SetRaw(path, value ? "1" : "0");

        /// <summary>Set x, y and z; only the ones that differ (by 1e-6 or more) are written.</summary>
        public void SetVector3(string path, Vector3 v)
        {
            SetNumber(path + ".x", v.X);
            SetNumber(path + ".y", v.Y);
            SetNumber(path + ".z", v.Z);
        }

        public void SetQuaternion(string path, Quaternion q)
        {
            SetNumber(path + ".x", q.X);
            SetNumber(path + ".y", q.Y);
            SetNumber(path + ".z", q.Z);
            SetNumber(path + ".w", q.W);
        }

        public override string ToString() => $"{TypeName} &{FileId}{(Name != null ? " " + Name : "")}";
    }

    public sealed class GameObject : UnityObject
    {
        internal GameObject(UnityFile file, YamlDocument doc) : base(file, doc) { }

        public bool IsActive => GetBool("m_IsActive") ?? true;

        /// <summary>Its components, in order (the Transform first).</summary>
        public IReadOnlyList<UnityObject> Components => (Find("m_Component")?.Children ?? new List<YamlNode>())
            .Select(item => ObjectReference.From(item.Child("component") ?? item))
            .Where(r => r.HasValue)
            .Select(r => File.Find(r!.Value.FileId))
            .Where(o => o != null)
            .Select(o => o!)
            .ToList();

        public Transform? Transform => Components.OfType<Transform>().FirstOrDefault();
    }

    public sealed class Transform : UnityObject
    {
        internal Transform(UnityFile file, YamlDocument doc) : base(file, doc) { }

        public bool IsRectTransform => ClassId == DataModel.ClassId.RectTransform;

        public long GameObjectId => GetReference("m_GameObject")?.FileId ?? 0;
        public GameObject? GameObject => File.Find<GameObject>(GameObjectId);

        /// <summary>The parent transform's file ID; 0 at the root.</summary>
        public long FatherId => GetReference("m_Father")?.FileId ?? 0;
        public Transform? Father => File.Find<Transform>(FatherId);

        public IReadOnlyList<long> ChildIds => (Find("m_Children")?.Children ?? new List<YamlNode>())
            .Select(c => ObjectReference.From(c)?.FileId ?? 0).Where(id => id != 0).ToList();

        /// <summary>For a stripped transform: the prefab instance it belongs to.</summary>
        public long PrefabInstanceId => GetReference("m_PrefabInstance")?.FileId ?? 0;

        public Vector3 LocalPosition => GetVector3("m_LocalPosition") ?? Vector3.Zero;
        public Quaternion LocalRotation => GetQuaternion("m_LocalRotation") ?? Quaternion.Identity;
        public Vector3 LocalScale => GetVector3("m_LocalScale") ?? Vector3.Zero;
        public Pose LocalPose => new Pose(LocalPosition, LocalRotation, LocalScale);

        /// <summary>Write a local pose; only the values that changed are written.</summary>
        public void SetLocalPose(Pose local)
        {
            SetVector3("m_LocalPosition", local.Position);
            SetQuaternion("m_LocalRotation", local.Rotation);
            SetVector3("m_LocalScale", local.Scale);
        }
    }

    /// <summary>One override a prefab instance makes to its source prefab.</summary>
    public sealed class PropertyModification
    {
        internal PropertyModification(YamlNode item)
        {
            Node = item;
            Target = ObjectReference.From(item.Child("target")) ?? ObjectReference.None;
            PropertyPath = YamlText.Unquote(item.Child("propertyPath")?.Raw);
            ValueNode = item.Child("value");
            ObjectReference = ObjectReference.From(item.Child("objectReference")) ?? DataModel.ObjectReference.None;
        }

        public YamlNode Node { get; }
        public ObjectReference Target { get; }
        public string PropertyPath { get; }
        public YamlNode? ValueNode { get; }
        public string? Value => ValueNode is { } v ? YamlText.Unquote(v.Raw) : null;
        public ObjectReference ObjectReference { get; }
    }

    public sealed class PrefabInstance : UnityObject
    {
        internal PrefabInstance(UnityFile file, YamlDocument doc) : base(file, doc) { }

        public ObjectReference SourcePrefab => GetReference("m_SourcePrefab") ?? ObjectReference.None;

        /// <summary>The transform it hangs under; 0 at the scene's root.</summary>
        public long TransformParentId => GetReference("m_Modification.m_TransformParent")?.FileId ?? 0;

        internal YamlNode? ModificationsNode => Find("m_Modification.m_Modifications");

        public IReadOnlyList<PropertyModification> Modifications =>
            (ModificationsNode?.Children ?? new List<YamlNode>()).Select(i => new PropertyModification(i)).ToList();

        /// <summary>The override of <paramref name="propertyPath"/> on <paramref name="target"/> (the last, if several).</summary>
        public PropertyModification? FindModification(ObjectReference target, string propertyPath) =>
            Modifications.LastOrDefault(m => m.Target.Equals(target) && m.PropertyPath == propertyPath);

        /// <summary>The instance's name: its (first) m_Name override.</summary>
        public new string? Name => Modifications.FirstOrDefault(m => m.PropertyPath == "m_Name")?.Value;

        /// <summary>
        /// Override a property of an object in the source prefab: changes the existing override, or adds one.
        /// <paramref name="raw"/> is the value's text as written.
        /// </summary>
        public void SetModification(ObjectReference target, string propertyPath, string raw)
        {
            if (FindModification(target, propertyPath)?.ValueNode is { } existing) File.SetRaw(this, existing, raw);
            else File.AddModifications(this, ModificationText(new string(' ', ModificationsNode?.ListIndent ?? ModificationsNode?.Indent ?? 4),
                target, propertyPath, raw));
        }

        /// <summary>The local pose this instance gives its prefab's root, starting from the root's own.</summary>
        public Pose RootPose(PrefabRoot root)
        {
            var target = new ObjectReference(root.RootTransformId, SourcePrefab.Guid);
            double Get(string path, double fallback) => FindModification(target, path)?.ValueNode is { } v ? YamlText.ToNumber(v.Raw) : fallback;
            var p = root.Pose.Position;
            var q = root.Pose.Rotation;
            var s = root.Pose.Scale;
            return new Pose(
                new Vector3(Get("m_LocalPosition.x", p.X), Get("m_LocalPosition.y", p.Y), Get("m_LocalPosition.z", p.Z)),
                new Quaternion(Get("m_LocalRotation.x", q.X), Get("m_LocalRotation.y", q.Y), Get("m_LocalRotation.z", q.Z), Get("m_LocalRotation.w", q.W)).Normalized,
                new Vector3(Get("m_LocalScale.x", s.X), Get("m_LocalScale.y", s.Y), Get("m_LocalScale.z", s.Z)));
        }

        /// <summary>
        /// Place the prefab's root at <paramref name="local"/>: changes the pose overrides that differ, and adds
        /// overrides only where the value differs from the prefab's own.
        /// </summary>
        public void SetRootPose(PrefabRoot root, Pose local)
        {
            var target = new ObjectReference(root.RootTransformId, SourcePrefab.Guid, 3);
            foreach (var (path, pick) in PoseProperties)
            {
                var v = pick(local);
                var existing = FindModification(target, path)?.ValueNode;
                if (existing != null)
                {
                    if (Math.Abs(YamlText.ToNumber(existing.Raw) - v) >= 1e-6) File.SetRaw(this, existing, YamlText.FormatNumber(v));
                }
                else if (Math.Abs(pick(root.Pose) - v) >= 1e-6) SetModification(target, path, YamlText.FormatNumber(v));
            }
        }

        internal static readonly (string Path, Func<Pose, double> Pick)[] PoseProperties =
        {
            ("m_LocalPosition.x", l => l.Position.X), ("m_LocalPosition.y", l => l.Position.Y), ("m_LocalPosition.z", l => l.Position.Z),
            ("m_LocalRotation.x", l => l.Rotation.X), ("m_LocalRotation.y", l => l.Rotation.Y), ("m_LocalRotation.z", l => l.Rotation.Z), ("m_LocalRotation.w", l => l.Rotation.W),
            ("m_LocalScale.x", l => l.Scale.X), ("m_LocalScale.y", l => l.Scale.Y), ("m_LocalScale.z", l => l.Scale.Z),
        };

        internal static string ModificationText(string indent, ObjectReference target, string path, string value) => string.Join("\n",
            $"{indent}- target: {{fileID: {target.FileId.ToString(CultureInfo.InvariantCulture)}, guid: {target.Guid}, type: 3}}",
            $"{indent}  propertyPath: {path}",
            $"{indent}  value: {value}",
            $"{indent}  objectReference: {{fileID: 0}}");
    }

    /// <summary>A scene's list of root objects (Unity 6.7 and later).</summary>
    public sealed class SceneRoots : UnityObject
    {
        internal SceneRoots(UnityFile file, YamlDocument doc) : base(file, doc) { }
        internal YamlNode? RootsNode => Find("m_Roots");
        public IReadOnlyList<long> RootIds => (RootsNode?.Children ?? new List<YamlNode>())
            .Select(c => ObjectReference.From(c)?.FileId ?? 0).Where(id => id != 0).ToList();
    }

    /// <summary>A prefab file's root: the ids an instance's overrides target, its name and default pose.</summary>
    public sealed class PrefabRoot
    {
        public PrefabRoot(long rootTransformId, long rootGameObjectId, string? name, Pose pose)
        {
            RootTransformId = rootTransformId;
            RootGameObjectId = rootGameObjectId;
            Name = name;
            Pose = pose;
        }

        public long RootTransformId { get; }
        public long RootGameObjectId { get; }
        public string? Name { get; }
        public Pose Pose { get; }

        /// <summary>The root of a prefab file, or null when it has no plain root (a prefab variant).</summary>
        public static PrefabRoot? Read(UnityFile prefab)
        {
            var root = prefab.Objects.OfType<Transform>().FirstOrDefault(t => !t.IsStripped && t.GetReference("m_Father")?.FileId == 0);
            if (root == null) return null;
            return new PrefabRoot(root.FileId, root.GameObjectId, root.GameObject?.Name, root.LocalPose);
        }
    }
}
