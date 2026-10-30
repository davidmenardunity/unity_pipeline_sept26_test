namespace Unity.DataModel.Yaml
{
    /// <summary>One <c>--- !u!{classId} &amp;{fileId}</c> document of a Unity YAML file.</summary>
    public sealed class YamlDocument
    {
        internal YamlDocument(int classId, long fileId, bool stripped, int line)
        {
            ClassId = classId;
            FileId = fileId;
            Stripped = stripped;
            Line = line;
        }

        /// <summary>The object's class (https://docs.unity3d.com/Manual/ClassIDReference.html).</summary>
        public int ClassId { get; }

        /// <summary>The object's local identifier in its file.</summary>
        public long FileId { get; }

        /// <summary>A stand-in for an object inside a prefab instance; it has only a few fields.</summary>
        public bool Stripped { get; }

        /// <summary>The header's line (0-based).</summary>
        public int Line { get; }

        /// <summary>The type name the body opens with ("GameObject", "MonoBehaviour"…).</summary>
        public string TypeName { get; internal set; } = "";

        /// <summary>The body: a map of the object's serialized fields.</summary>
        public YamlNode Root { get; internal set; } = null!;
    }
}
