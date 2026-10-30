using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// The transform hierarchy Unity makes from an .fbx: every node's path from the imported model's root, as an
/// AnimationClip binds it ("Dog_001_rig/Root/spine.004"). Reads the FBX itself (binary or ASCII): its Model
/// objects and the parent links between them. With preserveHierarchy off (the default), Unity drops a single
/// top-level node into the root, so its name isn't part of the paths.
/// </summary>
static class FbxHierarchy
{
    public record Node(string Path, string Type);

    public static List<Node> Read(byte[] bytes, bool preserveHierarchy = false)
    {
        var models = new Dictionary<long, (string name, string type)>();
        var parentOf = new Dictionary<long, long>();
        if (bytes.Length > 27 && Encoding.ASCII.GetString(bytes, 0, 18) == "Kaydara FBX Binary")
            ReadBinary(bytes, models, parentOf);
        else
            ReadAscii(Encoding.UTF8.GetString(bytes), models, parentOf);

        var children = models.Keys.GroupBy(id => parentOf.TryGetValue(id, out var p) && models.ContainsKey(p) ? p : 0L)
            .ToDictionary(g => g.Key, g => g.ToList());
        var tops = children.GetValueOrDefault(0L) ?? [];
        var nodes = new List<Node>();
        void Walk(long id, string prefix)
        {
            var (name, type) = models[id];
            var path = prefix.Length == 0 ? name : $"{prefix}/{name}";
            nodes.Add(new Node(path, type));
            foreach (var c in children.GetValueOrDefault(id) ?? []) Walk(c, path);
        }
        if (tops.Count == 1 && !preserveHierarchy)
            foreach (var c in children.GetValueOrDefault(tops[0]) ?? []) Walk(c, "");
        else
            foreach (var t in tops) Walk(t, "");
        return nodes;
    }

    // "Name\0\x01Model" (binary) or "Model::Name" (ASCII): Unity uses the name part.
    static string Clean(string s)
    {
        var i = s.IndexOf("\0\u0001", StringComparison.Ordinal);
        if (i >= 0) s = s[..i];
        if (s.StartsWith("Model::")) s = s[7..];
        return s;
    }

    static void ReadBinary(byte[] b, Dictionary<long, (string, string)> models, Dictionary<long, long> parentOf)
    {
        var version = BitConverter.ToUInt32(b, 23);
        var wide = version >= 7500;
        var pos = 27;
        // Top-level records; only Objects (its Model children) and Connections (its C children) matter.
        while (pos < b.Length)
        {
            var rec = Record(b, ref pos, wide);
            if (rec is null) break;
            if (rec.Value.name is "Objects" or "Connections")
            {
                var p = rec.Value.childrenAt;
                while (p < rec.Value.end)
                {
                    var child = Record(b, ref p, wide);
                    if (child is null) break;
                    var props = child.Value.props;
                    if (rec.Value.name == "Objects" && child.Value.name == "Model" && props.Count >= 3 && props[0] is long id)
                        models[id] = (Clean(props[1] as string ?? ""), props[2] as string ?? "");
                    else if (child.Value.name == "C" && props.Count >= 3 && props[0] is "OO" && props[1] is long c && props[2] is long parent)
                        parentOf.TryAdd(c, parent);
                    p = child.Value.end;
                }
            }
            pos = rec.Value.end;
        }
    }

    // One node record: its name, properties, where its children start and where it ends. Null at the end marker.
    static (string name, List<object?> props, int childrenAt, int end)? Record(byte[] b, ref int pos, bool wide)
    {
        long end, count;
        if (wide) { end = BitConverter.ToInt64(b, pos); count = BitConverter.ToInt64(b, pos + 8); pos += 24; }
        else { end = BitConverter.ToUInt32(b, pos); count = BitConverter.ToUInt32(b, pos + 4); pos += 12; }
        var nameLen = b[pos++];
        if (end == 0) return null;
        var name = Encoding.ASCII.GetString(b, pos, nameLen);
        pos += nameLen;
        var props = new List<object?>();
        for (var i = 0; i < count; i++)
        {
            var t = (char)b[pos++];
            switch (t)
            {
                case 'Y': props.Add((long)BitConverter.ToInt16(b, pos)); pos += 2; break;
                case 'C': props.Add(b[pos] != 0); pos += 1; break;
                case 'I': props.Add((long)BitConverter.ToInt32(b, pos)); pos += 4; break;
                case 'F': props.Add(BitConverter.ToSingle(b, pos)); pos += 4; break;
                case 'D': props.Add(BitConverter.ToDouble(b, pos)); pos += 8; break;
                case 'L': props.Add(BitConverter.ToInt64(b, pos)); pos += 8; break;
                case 'S' or 'R':
                    var len = BitConverter.ToInt32(b, pos);
                    props.Add(t == 'S' ? Encoding.UTF8.GetString(b, pos + 4, len) : null);
                    pos += 4 + len;
                    break;
                default:   // arrays (f d l i b): length, encoding, byte size, data — skipped
                    var size = BitConverter.ToInt32(b, pos + 8);
                    props.Add(null);
                    pos += 12 + size;
                    break;
            }
        }
        return (name, props, pos, (int)end);
    }

    static readonly Regex AsciiModel = new(@"^\s*Model:\s*(-?\d+),\s*""([^""]*)"",\s*""([^""]*)""", RegexOptions.Multiline);
    static readonly Regex AsciiLink = new(@"^\s*C:\s*""OO"",\s*(-?\d+),\s*(-?\d+)", RegexOptions.Multiline);

    static void ReadAscii(string text, Dictionary<long, (string, string)> models, Dictionary<long, long> parentOf)
    {
        foreach (Match m in AsciiModel.Matches(text))
            models[long.Parse(m.Groups[1].Value)] = (Clean(m.Groups[2].Value), m.Groups[3].Value);
        foreach (Match m in AsciiLink.Matches(text))
            parentOf.TryAdd(long.Parse(m.Groups[1].Value), long.Parse(m.Groups[2].Value));
    }
}
