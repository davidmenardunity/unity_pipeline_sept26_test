using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pipeline.Client;

// Wire shapes as staging serves them (September 2026). Unknown fields are
// ignored, so extra fields upstream don't break anything.

public sealed record Page<T>(IReadOnlyList<T> Items);

/// <summary>The workbench list: {items:[…]} until October 2026, {workbenches:[…]} since.</summary>
public sealed record WorkbenchList(IReadOnlyList<Workbench>? Items, IReadOnlyList<Workbench>? Workbenches);

/// <summary>An org as the legacy Unity API serves it; <c>GenesisId</c> is the numeric id (ORG_ID).</summary>
public sealed record Organization(string Id, string? GenesisId, string? Name);

/// <summary>A Unity Cloud project; <c>Id</c> is the UUID the pipeline routes take (PROJECT_ID).</summary>
public sealed record CloudProject(string Id, string? GenesisId, string? Name, DateTimeOffset? CreatedAt, DateTimeOffset? ArchivedAt);

public sealed record ProjectPage(IReadOnlyList<CloudProject> Results, int Total, int Limit, int Offset);

public sealed record Workbench(
    string WorkbenchId,
    string? Name,
    string? Owner,
    string? BranchName,
    string? Type,
    string? UpstreamRepository,
    string? UpstreamRevision,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? RetiredAt,
    IReadOnlyDictionary<string, WorkbenchBranch>? Branches,
    WorkbenchValidation? Validation,
    bool? DefaultHeadValidated);

public sealed record WorkbenchBranch(string? Head, string? TransactionId, string? VerifiedRevision);

public sealed record WorkbenchValidation(string? Status, ValidationError? Error, ValidationError? PreviousError);

public sealed record ValidationError(string? Category, string? Message);

/// <summary><c>readiness</c> is one of settled / settling / gone / unknown.</summary>
public sealed record WorkbenchReadiness(string WorkbenchId, string Readiness, string? Branch, string? SettledRevision, string? Head)
{
    public bool IsSettled => Readiness == "settled";
}

/// <summary>GET …/workbenches/{wb}/head (since October 2026, in place of …/readiness).</summary>
public sealed record WorkbenchHead(string? Branch, string? Head, string? SettledRevision, bool Settling);

/// <summary>A build profile: the platform an environment imports for (since October 2026).</summary>
public sealed record BuildProfileInfo(string ProfileId, string? Name, string? BuildTarget);

public sealed record ProfileList(IReadOnlyList<BuildProfileInfo>? Profiles, IReadOnlyList<BuildProfileInfo>? Items);

public sealed record EnvironmentList(IReadOnlyList<PipelineEnvironment>? Environments, IReadOnlyList<PipelineEnvironment>? Items);

public sealed record PipelineEnvironment(string EnvironmentId, string? Platform, string? ProfileId, string? WorkbenchId, string? Name);

public sealed record FileTreeEntry(string Path, bool IsFolder, string? Kind)
{
    /// <summary>Path without the leading '/', as the other routes take it.</summary>
    [JsonIgnore] public string RelativePath => Path.TrimStart('/');
    [JsonIgnore] public string Name => System.IO.Path.GetFileName(RelativePath);
}

public sealed record FileTree(IReadOnlyList<FileTreeEntry> Entries, string? Revision);

/// <summary>One row of a revision's manifest: an imported asset (Assets/, resolved packages, settings).</summary>
public sealed record ManifestEntry(string Path, string? AssetGuid, bool IsFolder, string? MetafileHash)
{
    [JsonIgnore] public string RelativePath => Path.TrimStart('/');
}

public sealed record ManifestPage(IReadOnlyList<ManifestEntry> Items, string? NextCursor);

public sealed record SearchHit(string Path, string? AssetGuid, string? Name, bool IsFolder, string? Type)
{
    [JsonIgnore] public string RelativePath => Path.TrimStart('/');
}

public sealed record AssetInfo(
    string Path,
    string? AssetGuid,
    string? Name,
    bool IsFolder,
    string? FileHash,
    string? MetafileHash,
    long? Size,
    string? Revision,
    long? Timestamp);

public sealed record GuidResult(string? AssetGuid, BatchItemError? Error);

public sealed record BatchItemError(string? Code, string? Message);

public sealed record GuidResults(IReadOnlyList<GuidResult> Results, string? Revision);

/// <summary>One slot of a batch import answer: the import manifest, or why the address didn't resolve.</summary>
public sealed record ImportSlot(JsonElement? Manifest, BatchItemError? Error)
{
    /// <summary>
    /// The artifact names in the manifest's <c>artifacts</c>. Staging lists plain
    /// strings; objects with a name/path are accepted too, in case that changes.
    /// </summary>
    public IReadOnlyList<string> ArtifactNames()
    {
        if (Manifest is not { ValueKind: JsonValueKind.Object } m
            || !m.TryGetProperty("artifacts", out var arts) || arts.ValueKind != JsonValueKind.Array)
            return [];
        var names = new List<string>();
        foreach (var a in arts.EnumerateArray())
        {
            if (a.ValueKind == JsonValueKind.String) names.Add(a.GetString()!);
            else if (a.ValueKind == JsonValueKind.Object)
                foreach (var key in (string[])["name", "path", "fileName"])
                    if (a.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                    {
                        names.Add(v.GetString()!);
                        break;
                    }
        }
        return names;
    }
}

public sealed record ImportResults(IReadOnlyList<ImportSlot> Results);

/// <summary>
/// A background job. Until October 2026: state + isTerminal. Since then possibly status
/// (queued | processing | cancelling | cancelled | completed | failed); both are read.
/// </summary>
public sealed record Job(string? JobId, string? Kind, string? State, bool IsTerminal, string? Status)
{
    [JsonIgnore] public bool IsDone => IsTerminal || Status is "completed" or "failed" or "cancelled";
    [JsonIgnore] public string? Shown => State ?? Status;
}

public sealed record ProjectServiceStatus(string? Status, string? Message, ProjectServiceDetail? ProjectService)
{
    public bool IsReady => Status == "ready";

    /// <summary>
    /// Really starting: "starting" with a start job still running. After an idle
    /// shutdown the status keeps saying "starting" although its job has finished
    /// (friction log F46); that's stopped.
    /// </summary>
    public bool IsStarting => Status == "starting" && !IsJobFinished;

    public bool IsStopped => !IsReady && !IsStarting;

    bool IsJobFinished => ProjectService?.JobStatus is "Succeeded" or "Failed" or "Cancelled" or "Canceled";

    /// <summary>For the header: "project service ready" / "starting…" / "stopped".</summary>
    public string Describe() =>
        IsReady ? "project service ready" : IsStarting ? "project service starting…" : "project service stopped";
}

public sealed record ProjectServiceDetail(string? JobId, string? JobStatus, bool? IsReady,
    IReadOnlyList<ProjectServiceStep>? Steps, IReadOnlyList<ProjectServiceWorker>? Workers);

public sealed record ProjectServiceStep(string? Name, string? Status, int? Progress);

public sealed record ProjectServiceWorker(string? WorkerId, string? JobStatus, string? EditorVersion, string? TargetPlatform);

/// <summary>An API call that has just been sent (its <see cref="ApiCall"/> follows with the same id).</summary>
public sealed record ApiCallStarted(long Id, DateTimeOffset At, string Method, string Path, bool IsPoll = false,
    ApiExchange? Exchange = null);

/// <summary>One finished API call, for the activity panel.</summary>
public sealed record ApiCall(
    long Id,
    DateTimeOffset At,
    string Method,
    string Path,
    int Status,
    TimeSpan Duration,
    string? RequestId,
    string? ErrorCode,
    string? ErrorDetail,
    bool IsPoll = false,
    ApiExchange? Exchange = null);

/// <summary>The revision a commit produced.</summary>
public sealed record CommitResult(string? RevisionId, bool? NoNewRevision);

/// <summary>What a blob upload hands back; the handle binds the bytes to a path when staged.</summary>
public sealed record BlobUpload(string UploadHandle, string? ContentGuid, long? ByteCount);

public sealed record OpenedTransaction(string TransactionId);
