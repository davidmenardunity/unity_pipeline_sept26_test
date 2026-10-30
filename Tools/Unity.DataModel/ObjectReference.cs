using System;
using System.Globalization;
using Unity.DataModel.Yaml;

namespace Unity.DataModel
{
    /// <summary>
    /// A reference to an object: <c>{fileID: …}</c> inside the same file, or <c>{fileID: …, guid: …, type: …}</c>
    /// to an object in another asset.
    /// </summary>
    public readonly struct ObjectReference : IEquatable<ObjectReference>
    {
        public ObjectReference(long fileId, string? guid = null, int? type = null)
        {
            FileId = fileId;
            Guid = string.IsNullOrEmpty(guid) ? null : guid;
            Type = type;
        }

        public long FileId { get; }

        /// <summary>The asset holding the object, or null for an object in the same file.</summary>
        public string? Guid { get; }

        /// <summary>2 for an asset's own object, 3 for an object in an imported asset (a prefab, a model).</summary>
        public int? Type { get; }

        /// <summary>No object (<c>{fileID: 0}</c>).</summary>
        public bool IsNone => FileId == 0;

        public bool IsLocal => Guid == null;

        public static readonly ObjectReference None = new ObjectReference(0);

        /// <summary>The reference a flow map holds, or null when it isn't one.</summary>
        public static ObjectReference? From(YamlNode? node)
        {
            if (node?.Kind != YamlNodeKind.Flow || node.Item("fileID") is not { } id) return null;
            if (!long.TryParse(id.Raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fileId)) return null;
            var guid = node.Item("guid")?.Raw.Trim();
            int? type = int.TryParse(node.Item("type")?.Raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) ? t : (int?)null;
            return new ObjectReference(fileId, guid, type);
        }

        /// <summary>As Unity writes it.</summary>
        public override string ToString() => Guid == null
            ? $"{{fileID: {FileId.ToString(CultureInfo.InvariantCulture)}}}"
            : $"{{fileID: {FileId.ToString(CultureInfo.InvariantCulture)}, guid: {Guid}, type: {(Type ?? 3).ToString(CultureInfo.InvariantCulture)}}}";

        public bool Equals(ObjectReference other) => FileId == other.FileId && Guid == other.Guid;
        public override bool Equals(object? obj) => obj is ObjectReference r && Equals(r);
        public override int GetHashCode() => (FileId, Guid).GetHashCode();
    }
}
