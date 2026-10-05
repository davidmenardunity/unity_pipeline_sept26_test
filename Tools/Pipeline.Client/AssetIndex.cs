namespace Pipeline.Client;

/// <summary>
/// A revision's manifest as a folder tree, for browsing imported assets the way
/// Unity's Project window shows them. The manifest only has folder rows under
/// Assets/, so the folders of packages are implied by the paths below them.
/// </summary>
public sealed class AssetIndex
{
    public sealed record Node(string Name, string Path, bool IsFolder, string? AssetGuid);

    readonly Dictionary<string, List<Node>> children = new(StringComparer.Ordinal);
    readonly Dictionary<string, Node> byPath = new(StringComparer.Ordinal);

    public int Count { get; }

    public AssetIndex(IEnumerable<ManifestEntry> entries)
    {
        foreach (var e in entries)
        {
            var path = e.RelativePath.TrimEnd('/');
            if (path.Length == 0 || path.EndsWith(".meta", StringComparison.Ordinal)) continue;
            Add(path, e.IsFolder, e.AssetGuid);
            Count++;
        }
        foreach (var list in children.Values)
            list.Sort((a, b) => a.IsFolder != b.IsFolder ? (a.IsFolder ? -1 : 1) : string.CompareOrdinal(a.Name, b.Name));
    }

    void Add(string path, bool isFolder, string? guid)
    {
        if (byPath.TryGetValue(path, out var known))
        {
            // An implied folder that turns out to have its own row: keep the row's GUID.
            if (known.AssetGuid is null && guid is not null) Replace(known, known with { AssetGuid = guid });
            return;
        }
        var slash = path.LastIndexOf('/');
        var parent = slash < 0 ? "" : path[..slash];
        if (parent.Length > 0) Add(parent, true, null);
        var node = new Node(path[(slash + 1)..], path, isFolder, guid);
        byPath[path] = node;
        (children.TryGetValue(parent, out var list) ? list : children[parent] = []).Add(node);
    }

    void Replace(Node old, Node now)
    {
        byPath[now.Path] = now;
        var slash = now.Path.LastIndexOf('/');
        var list = children[slash < 0 ? "" : now.Path[..slash]];
        list[list.IndexOf(old)] = now;
    }

    /// <summary>Top-level folders: Assets and Packages first, then the rest (ProjectSettings, …).</summary>
    public IReadOnlyList<Node> Roots => Children("")
        .OrderBy(n => n.Name switch { "Assets" => 0, "Packages" => 1, _ => 2 }).ToList();

    /// <summary>One folder's rows, folders first; empty for an unknown path.</summary>
    public IReadOnlyList<Node> Children(string folder) =>
        children.TryGetValue(folder.Trim('/'), out var list) ? list : [];

    public Node? Find(string path) => byPath.GetValueOrDefault(path.Trim('/'));
}
