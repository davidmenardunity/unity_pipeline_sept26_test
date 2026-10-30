namespace CoAuthoring.Engine;

/// <summary>
/// The workbench a co-authoring session edits: where files are read at a revision and where a save is committed.
/// The Pipeline Explorer implements it with the Pipeline API; tests with a folder.
/// </summary>
public interface IWorkbenchSource
{
    /// <summary>The workbench's latest revision.</summary>
    Task<string> HeadAsync(string workbench, CancellationToken ct);

    /// <summary>A file's text at a revision, or null when there is no such file.</summary>
    Task<string?> ReadTextAsync(string workbench, string revision, string path, CancellationToken ct);

    Task<string?> GuidOfAsync(string workbench, string revision, string path, CancellationToken ct);
    Task<string?> PathOfAsync(string workbench, string revision, string guid, CancellationToken ct);

    /// <summary>The GUID of the script that declares a MonoBehaviour type (by class name), or null.</summary>
    Task<string?> FindScriptAsync(string workbench, string revision, string typeName, CancellationToken ct);

    /// <summary>Commits files as one new revision on top of <paramref name="baseRevision"/>; returns it.</summary>
    Task<string> CommitAsync(string workbench, string baseRevision, IReadOnlyList<(string Path, string Text)> files, string message, CancellationToken ct);
}
