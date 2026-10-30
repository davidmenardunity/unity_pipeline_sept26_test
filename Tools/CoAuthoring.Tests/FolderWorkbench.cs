using System.Text.RegularExpressions;
using CoAuthoring.Engine;

namespace CoAuthoring.Tests;

/// <summary>
/// A workbench made of a folder (a small Unity project): revision "1" is the folder; each commit is a new revision
/// holding the files it changed. GUIDs come from the .meta files.
/// </summary>
public sealed class FolderWorkbench : IWorkbenchSource
{
    readonly string root;
    readonly List<Dictionary<string, string>> revisions = [new()];
    readonly object gate = new();

    public FolderWorkbench(string root) => this.root = root;

    public List<(string Revision, string Message, string[] Paths)> Commits { get; } = [];

    /// <summary>Simulate someone else committing (for conflict tests).</summary>
    public string CommitExternally(string path, string text) =>
        CommitAsync("wb", Head, [(path, text)], "external", CancellationToken.None).Result;

    string Head { get { lock (gate) return revisions.Count.ToString(); } }

    public Task<string> HeadAsync(string workbench, CancellationToken ct) => Task.FromResult(Head);

    public Task<string?> ReadTextAsync(string workbench, string revision, string path, CancellationToken ct)
    {
        lock (gate)
        {
            for (var r = int.Parse(revision) - 1; r >= 0; r--)
                if (revisions[r].TryGetValue(path, out var t)) return Task.FromResult<string?>(t);
        }
        var file = Path.Combine(root, path);
        return Task.FromResult(File.Exists(file) ? File.ReadAllText(file) : null);
    }

    public Task<string?> GuidOfAsync(string workbench, string revision, string path, CancellationToken ct)
    {
        var meta = Path.Combine(root, path + ".meta");
        return Task.FromResult(File.Exists(meta) ? Regex.Match(File.ReadAllText(meta), @"guid: (\w+)").Groups[1].Value : null);
    }

    public Task<string?> PathOfAsync(string workbench, string revision, string guid, CancellationToken ct)
    {
        foreach (var meta in Directory.EnumerateFiles(root, "*.meta", SearchOption.AllDirectories))
            if (File.ReadAllText(meta).Contains("guid: " + guid))
                return Task.FromResult<string?>(Path.GetRelativePath(root, meta[..^5]).Replace('\\', '/'));
        return Task.FromResult<string?>(null);
    }

    public async Task<string?> FindScriptAsync(string workbench, string revision, string typeName, CancellationToken ct)
    {
        var hit = Directory.EnumerateFiles(root, typeName + ".cs", SearchOption.AllDirectories).FirstOrDefault();
        return hit == null ? null : await GuidOfAsync(workbench, revision, Path.GetRelativePath(root, hit).Replace('\\', '/'), ct);
    }

    public Task<string> CommitAsync(string workbench, string baseRevision, IReadOnlyList<(string Path, string Text)> files, string message, CancellationToken ct)
    {
        lock (gate)
        {
            if (baseRevision != Head) throw new InvalidOperationException($"commit on {baseRevision}, but the head is {Head}");
            revisions.Add(files.ToDictionary(f => f.Path, f => f.Text));
            Commits.Add((Head, message, files.Select(f => f.Path).ToArray()));
            return Task.FromResult(Head);
        }
    }
}
