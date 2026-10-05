using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Pipeline.Client;

/// <summary>
/// Typed client for the Unity Pipeline, as served on staging through the
/// pipeline broker (plus projectservice start/status on the internal host).
///
/// Route shapes follow the broker's OpenAPI spec
/// (Unity-Technologies/pipeline-broker, docs/openapi/broker-api-v1.yaml) and
/// what staging actually does; the quirks are documented where they're handled.
/// </summary>
public sealed class PipelineClient : IDisposable
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly HttpClient http;
    readonly string clientBase;    // broker (public client host)
    readonly string internalBase;  // projectservice start/status
    readonly string internalHost;  // org and project directory (legacy Unity API)

    public PipelineConfig Config { get; }

    /// <summary>Raised as each call is sent (for showing pending calls).</summary>
    public event Action<ApiCallStarted>? CallStarted;

    /// <summary>Raised after every call, success or failure, with the id from <see cref="CallStarted"/>.</summary>
    public event Action<ApiCall>? CallCompleted;

    long nextCallId;

    // Calls made while polling (readiness, jobs) are flagged so the activity
    // panel can group them instead of listing every repeat.
    readonly AsyncLocal<bool> polling = new();

    IDisposable Polling()
    {
        var previous = polling.Value;
        polling.Value = true;
        return new Scope(() => polling.Value = previous);
    }

    sealed class Scope(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    public PipelineClient(PipelineConfig config, HttpMessageHandler? handler = null)
    {
        Config = config;
        http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = Timeout.InfiniteTimeSpan;   // per-call timeouts below
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.Token);
        var (client, @internal) = config.Environment == "production"
            ? ("https://services.api.unity.com", "https://services.unity.com")
            : ("https://staging.services.api.unity.com", "https://staging.services.unity.com");
        var scope = $"api/pipeline/v1/organizations/{config.OrganizationId}/projects/{config.ProjectId}";
        clientBase = $"{client}/{scope}";
        internalBase = $"{@internal}/{scope}";
        internalHost = @internal;
    }

    public void Dispose() => http.Dispose();

    /// <summary>
    /// A path inside the project as ONE URL segment. The gateway matches a path
    /// parameter as a single segment, so "Assets/Scenes" must go out as
    /// "Assets%2FScenes"; a literal '/' gives a gateway 404.
    /// </summary>
    public static string Segment(string projectPath) => Uri.EscapeDataString(projectPath.Trim('/'));

    // ── transport ───────────────────────────────────────────────────────────

    async Task<(int Status, byte[] Body)> SendAsync(HttpMethod method, string url, object? json,
        HttpContent? content, string accept, TimeSpan timeout, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Accept.ParseAdd(accept);
        byte[]? sentBytes = null;
        if (json is not null)
        {
            // Serialised up front (not JsonContent) so the details pane can show what went out.
            sentBytes = JsonSerializer.SerializeToUtf8Bytes(json, json.GetType(), Json);
            request.Content = new ByteArrayContent(sentBytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }
        else if (content is not null) request.Content = content;
        var exchange = await DescribeRequestAsync(request, sentBytes, ct).ConfigureAwait(false);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var id = Interlocked.Increment(ref nextCallId);
        var started = DateTimeOffset.Now;
        var isPoll = polling.Value;
        CallStarted?.Invoke(new ApiCallStarted(id, started, method.Method, ShortPath(url), isPoll, exchange));
        var watch = Stopwatch.StartNew();
        int status;
        byte[] body;
        try
        {
            using var response = await http.SendAsync(request, cts.Token).ConfigureAwait(false);
            status = (int)response.StatusCode;
            body = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            exchange = exchange with
            {
                Status = status,
                Reason = response.ReasonPhrase,
                ResponseHeaders = ApiExchange.Collect(response.Headers, response.Content.Headers),
                ResponseBody = ApiExchange.DescribeBody(body, response.Content.Headers.ContentType?.MediaType),
            };
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            var ex = new PipelineApiException(method.Method, ShortPath(url), 0, null,
                e is TaskCanceledException ? $"timed out after {timeout.TotalSeconds:0}s" : e.Message, null, false, e);
            Report(id, started, method.Method, url, 0, watch.Elapsed, ex, isPoll,
                exchange with { TransportError = ex.Detail ?? e.Message });
            throw ex;
        }

        PipelineApiException? failure = status is >= 200 and < 300
            ? null : PipelineApiException.FromResponse(method.Method, ShortPath(url), status, body);
        Report(id, started, method.Method, url, status, watch.Elapsed, failure, isPoll, exchange);
        return (status, body);
    }

    /// <summary>The request as it will go out: default headers included, token redacted.</summary>
    async Task<ApiExchange> DescribeRequestAsync(HttpRequestMessage request, byte[]? sentBytes, CancellationToken ct)
    {
        var content = request.Content;
        var type = content?.Headers.ContentType?.MediaType;
        string? body = null;
        var binary = false;
        if (content is not null)
        {
            // Read text bodies only; binary uploads (blobs) are described by their headers.
            var bytes = sentBytes ?? (type is null || !type.Contains("octet-stream")
                ? await content.ReadAsByteArrayAsync(ct).ConfigureAwait(false) : null);
            body = bytes is null ? $"({content.Headers.ContentLength:N0} bytes, {type})"
                : ApiExchange.DescribeBody(bytes, type);
            binary = bytes is null || !ApiExchange.LooksLikeText(bytes, type);
        }
        return new ApiExchange(request.Method.Method, request.RequestUri!.ToString(),
            ApiExchange.Collect(http.DefaultRequestHeaders, request.Headers, content?.Headers), body,
            RequestBodyIsBinary: binary);
    }

    void Report(long id, DateTimeOffset started, string method, string url, int status, TimeSpan elapsed,
        PipelineApiException? failure, bool isPoll, ApiExchange exchange) =>
        CallCompleted?.Invoke(new ApiCall(id, started, method, ShortPath(url), status, elapsed,
            failure?.RequestId, failure?.Code, failure?.Detail, isPoll, exchange));

    string ShortPath(string url) =>
        url.Replace(clientBase, "…").Replace(internalBase, "…(internal)").Replace(internalHost, "(internal)");

    async Task<T> JsonAsync<T>(HttpMethod method, string url, object? body = null,
        TimeSpan? timeout = null, CancellationToken ct = default, params int[] alsoOk)
    {
        var (status, bytes) = await SendAsync(method, url, body, null, "application/json",
            timeout ?? TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        if (status is < 200 or >= 300 && Array.IndexOf(alsoOk, status) < 0)
            throw PipelineApiException.FromResponse(method.Method, ShortPath(url), status, bytes);
        return JsonSerializer.Deserialize<T>(bytes, Json)
               ?? throw new PipelineApiException(method.Method, ShortPath(url), status, null, "empty body", null, false);
    }

    async Task NoContentAsync(HttpMethod method, string url, object? body = null, CancellationToken ct = default,
        TimeSpan? timeout = null)
    {
        var (status, bytes) = await SendAsync(method, url, body, null, "application/json",
            timeout ?? TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        if (status is < 200 or >= 300)
            throw PipelineApiException.FromResponse(method.Method, ShortPath(url), status, bytes);
    }

    // ── jobs and asynchronous reads ─────────────────────────────────────────

    public Task<Job> GetJobAsync(string jobId, CancellationToken ct = default) =>
        JsonAsync<Job>(HttpMethod.Get, $"{clientBase}/jobs/{jobId}", ct: ct);

    public async Task<Job> WaitForJobAsync(string jobId, TimeSpan budget, IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + budget;
        string? shown = null;
        using var _ = Polling();
        while (true)
        {
            var job = await GetJobAsync(jobId, ct).ConfigureAwait(false);
            if (job.State != shown) progress?.Report($"job {jobId[..8]}: {job.State}");
            shown = job.State;
            if (job.IsTerminal) return job;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"job {jobId} still {job.State} after {budget.TotalSeconds:0}s");
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads such as asset-guid and source can answer 202 + jobId the first time
    /// (the project service has to produce the data): wait for the job, then ask again.
    /// </summary>
    async Task<(int Status, byte[] Body)> ReadAsync(HttpMethod method, string url, object? body, string accept,
        IProgress<string>? progress, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var (status, bytes) = await SendAsync(method, url, body, null, accept, TimeSpan.FromSeconds(120), ct)
                .ConfigureAwait(false);
            if (status != 202 || attempt >= 3) return (status, bytes);
            var job = TryJobId(bytes);
            if (job is null) return (status, bytes);
            await WaitForJobAsync(job, TimeSpan.FromMinutes(10), progress, ct).ConfigureAwait(false);
        }
    }

    static string? TryJobId(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("jobId", out var j) ? j.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    byte[] EnsureOk(string method, string url, (int Status, byte[] Body) r) =>
        r.Status is >= 200 and < 300 ? r.Body : throw PipelineApiException.FromResponse(method, ShortPath(url), r.Status, r.Body);

    // ── project service (internal host) ─────────────────────────────────────

    public Task<ProjectServiceStatus> GetProjectServiceStatusAsync(CancellationToken ct = default) =>
        JsonAsync<ProjectServiceStatus>(HttpMethod.Get,
            $"{internalBase}/branches/{Uri.EscapeDataString(Config.Branch)}/projectservice/status", ct: ct);

    /// <summary>Ask for the project service to start, without waiting. A 409 (already starting or running) is fine.</summary>
    public async Task StartProjectServiceAsync(CancellationToken ct = default)
    {
        var url = $"{internalBase}/branches/{Uri.EscapeDataString(Config.Branch)}/projectservice/start";
        var (code, body) = await SendAsync(HttpMethod.Post, url, new { region = Config.Region }, null,
            "application/json", TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        if (code is < 200 or >= 300 && code != 409)
            throw PipelineApiException.FromResponse("POST", ShortPath(url), code, body);
    }

    /// <summary>Start the project service if it isn't ready, and wait until it is. It idles out overnight.</summary>
    public async Task EnsureProjectServiceAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var status = await GetProjectServiceStatusAsync(ct).ConfigureAwait(false);
        if (status.IsReady) return;
        progress?.Report("starting the project service…");
        if (!status.IsStarting)   // already starting (someone else asked): just wait
            await StartProjectServiceAsync(ct).ConfigureAwait(false);

        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(5);
        string? shown = null;
        using var _ = Polling();
        while (DateTime.UtcNow < deadline)
        {
            status = await GetProjectServiceStatusAsync(ct).ConfigureAwait(false);
            if (status.Status != shown) progress?.Report($"project service: {status.Status}");
            shown = status.Status;
            if (status.IsReady) return;
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }
        throw new TimeoutException($"project service not ready after 5 min (last: {shown})");
    }

    // ── org and projects (internal host, legacy Unity API) ──────────────────
    // The broker has no project list. These take the org's numeric id (ORG_ID)
    // and need only the token, so the config's project can still be empty.

    string Org => $"{internalHost}/api/unity/legacy/v1/organizations/{Uri.EscapeDataString(Config.OrganizationId)}";

    public Task<Organization> GetOrganizationAsync(CancellationToken ct = default) =>
        JsonAsync<Organization>(HttpMethod.Get, Org, ct: ct);

    /// <summary>The org's Unity Cloud projects, every page; archived ones included (see <see cref="CloudProject.ArchivedAt"/>).</summary>
    public async Task<IReadOnlyList<CloudProject>> ListProjectsAsync(CancellationToken ct = default)
    {
        var all = new List<CloudProject>();
        while (true)
        {
            var page = await JsonAsync<ProjectPage>(HttpMethod.Get, $"{Org}/projects?limit=100&offset={all.Count}", ct: ct)
                .ConfigureAwait(false);
            all.AddRange(page.Results);
            if (page.Results.Count == 0 || all.Count >= page.Total) return all;
        }
    }

    // ── workbenches ─────────────────────────────────────────────────────────

    string Wb(string workbenchId) => $"{clientBase}/workbenches/{workbenchId}";

    /// <summary>The caller's workbenches (owner-scoped; retired ones excluded).</summary>
    public async Task<IReadOnlyList<Workbench>> ListWorkbenchesAsync(CancellationToken ct = default) =>
        (await JsonAsync<Page<Workbench>>(HttpMethod.Get, $"{clientBase}/workbenches", ct: ct).ConfigureAwait(false)).Items;

    public Task<Workbench> GetWorkbenchAsync(string workbenchId, CancellationToken ct = default) =>
        JsonAsync<Workbench>(HttpMethod.Get, Wb(workbenchId), timeout: TimeSpan.FromSeconds(90), ct: ct);

    public Task<Workbench> CreateWorkbenchAsync(string repositoryUrl, string branch, CancellationToken ct = default) =>
        JsonAsync<Workbench>(HttpMethod.Post, $"{clientBase}/workbenches",
            new { type = "git", branch, repo = repositoryUrl }, TimeSpan.FromSeconds(120), ct);

    /// <summary>Soft stop: releases the worker; the workbench leaves the listing but can still be read.</summary>
    public Task RetireWorkbenchAsync(string workbenchId, CancellationToken ct = default) =>
        NoContentAsync(HttpMethod.Post, $"{Wb(workbenchId)}/stop", ct: ct);

    public Task DeleteWorkbenchAsync(string workbenchId, CancellationToken ct = default) =>
        NoContentAsync(HttpMethod.Delete, Wb(workbenchId), ct: ct);

    public Task<WorkbenchReadiness> GetReadinessAsync(string workbenchId, CancellationToken ct = default) =>
        JsonAsync<WorkbenchReadiness>(HttpMethod.Get, $"{Wb(workbenchId)}/readiness", ct: ct);

    /// <summary>
    /// Wait until the workbench has settled (at <paramref name="revision"/>, if given) and
    /// return the settled revision. Fails fast when validation fails: a failed validation
    /// never settles.
    /// </summary>
    public async Task<string> WaitUntilSettledAsync(string workbenchId, string? revision = null,
        TimeSpan? budget = null, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + (budget ?? TimeSpan.FromMinutes(15));
        string? shown = null;
        using var _ = Polling();
        while (true)
        {
            var r = await GetReadinessAsync(workbenchId, ct).ConfigureAwait(false);
            if (r.IsSettled && r.SettledRevision is { } settled && (revision is null || settled == revision))
                return settled;
            if (r.Readiness == "gone")
                throw new InvalidOperationException("the project service no longer has this workbench (readiness: gone)");

            var wb = await GetWorkbenchAsync(workbenchId, ct).ConfigureAwait(false);
            var v = wb.Validation;
            var state = $"readiness {r.Readiness}, head {r.Head}, validation {v?.Status}";
            if (state != shown) progress?.Report(state);
            shown = state;
            if (v?.Status == "failed")
                throw new ValidationFailedException(v.Error);
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"workbench didn't settle within {(budget ?? TimeSpan.FromMinutes(15)).TotalMinutes:0} min ({state})");
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }
    }

    // ── environments ────────────────────────────────────────────────────────

    /// <summary>
    /// Environments of this workbench. (The list can include other workbenches'
    /// environments, so filter; GET …/environments/{id} answers a gateway 404.)
    /// </summary>
    public async Task<IReadOnlyList<PipelineEnvironment>> ListEnvironmentsAsync(string workbenchId,
        CancellationToken ct = default)
    {
        var page = await JsonAsync<Page<PipelineEnvironment>>(HttpMethod.Get, $"{Wb(workbenchId)}/environments", ct: ct)
            .ConfigureAwait(false);
        return page.Items.Where(e => e.WorkbenchId == workbenchId).ToList();
    }

    public Task<PipelineEnvironment> CreateEnvironmentAsync(string workbenchId, string platform,
        CancellationToken ct = default) =>
        JsonAsync<PipelineEnvironment>(HttpMethod.Post, $"{Wb(workbenchId)}/environments", new { platform }, ct: ct);

    // ── reads (all pinned to a revision) ────────────────────────────────────

    string Rev(string workbenchId, string revision) => $"{Wb(workbenchId)}/revisions/{Uri.EscapeDataString(revision)}";

    /// <summary>One level of a folder.</summary>
    public Task<FileTree> GetFileTreeAsync(string workbenchId, string revision, string folder,
        CancellationToken ct = default) =>
        JsonAsync<FileTree>(HttpMethod.Get, $"{Rev(workbenchId, revision)}/file-tree/{Segment(folder)}", ct: ct);

    /// <summary>
    /// Every asset the revision's asset database holds (what the Editor imported,
    /// resolved packages included), following the cursor to the last page.
    /// The server caps pages at 500 rows; ~6,000 rows take ~25 s.
    /// </summary>
    public async Task<IReadOnlyList<ManifestEntry>> ListManifestAsync(string workbenchId, string revision,
        IProgress<int>? rows = null, CancellationToken ct = default)
    {
        var all = new List<ManifestEntry>();
        string? cursor = null;
        do
        {
            var url = $"{Rev(workbenchId, revision)}/manifest?limit=1000" +
                      (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
            var page = JsonSerializer.Deserialize<ManifestPage>(
                EnsureOk("GET", url, await ReadAsync(HttpMethod.Get, url, null, "application/json", null, ct)
                    .ConfigureAwait(false)), Json)!;
            all.AddRange(page.Items);
            rows?.Report(all.Count);
            cursor = string.IsNullOrEmpty(page.NextCursor) ? null : page.NextCursor;
        } while (cursor is not null);
        return all;
    }

    /// <summary>Server-side search by name; an empty query lists every asset (Assets/ and Packages/).</summary>
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string workbenchId, string revision, string query,
        CancellationToken ct = default) =>
        (await JsonAsync<Page<SearchHit>>(HttpMethod.Get,
            $"{Rev(workbenchId, revision)}/asset-search?q={Uri.EscapeDataString(query)}",
            timeout: TimeSpan.FromSeconds(120), ct: ct).ConfigureAwait(false)).Items;

    public async Task<string?> ResolveGuidAsync(string workbenchId, string revision, string path,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var url = $"{Rev(workbenchId, revision)}/asset-guid";
        var body = EnsureOk("POST", url, await ReadAsync(HttpMethod.Post, url, new { keys = new[] { path } },
            "application/json", progress, ct).ConfigureAwait(false));
        var results = JsonSerializer.Deserialize<GuidResults>(body, Json);
        return results?.Results.FirstOrDefault()?.AssetGuid;
    }

    public Task<AssetInfo> GetAssetAsync(string workbenchId, string revision, string guid,
        CancellationToken ct = default) =>
        JsonAsync<AssetInfo>(HttpMethod.Get, $"{Rev(workbenchId, revision)}/assets/{guid}", ct: ct);

    /// <summary>A file's bytes by path.</summary>
    public async Task<byte[]> GetFileAsync(string workbenchId, string revision, string path,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var url = $"{Rev(workbenchId, revision)}/files/{Segment(path)}";
        return EnsureOk("GET", url, await ReadAsync(HttpMethod.Get, url, null, "*/*", progress, ct).ConfigureAwait(false));
    }

    /// <summary>The asset's .meta (YAML bytes).</summary>
    public async Task<byte[]> GetMetaAsync(string workbenchId, string revision, string guid,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var url = $"{Rev(workbenchId, revision)}/assets/{guid}/meta";
        return EnsureOk("GET", url, await ReadAsync(HttpMethod.Get, url, null, "*/*", progress, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// A preview PNG, or null if the asset type doesn't render (scenes, scripts…).
    /// The trigger answers 200 + image (cache hit) or 202 + jobId; after the job,
    /// collect with allowAsync=false. A cold render takes about two minutes.
    /// </summary>
    public async Task<byte[]?> GetPreviewAsync(string workbenchId, string environmentId, string revision,
        string guid, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var baseUrl = $"{Wb(workbenchId)}/environments/{environmentId}/revisions/{Uri.EscapeDataString(revision)}/previews";
        var (status, body) = await SendAsync(HttpMethod.Post, baseUrl, new { assetGuid = guid }, null,
            "image/*, application/json", TimeSpan.FromSeconds(120), ct).ConfigureAwait(false);
        if (status == 202 && TryJobId(body) is { } job)
        {
            await WaitForJobAsync(job, TimeSpan.FromMinutes(10), progress, ct).ConfigureAwait(false);
            (status, body) = await SendAsync(HttpMethod.Get, $"{baseUrl}/{guid}?allowAsync=false", null, null,
                "image/*", TimeSpan.FromSeconds(120), ct).ConfigureAwait(false);
        }
        if (status == 200 && IsPng(body)) return body;
        if (status == 202) return null;   // no image for this asset type
        throw PipelineApiException.FromResponse("GET", ShortPath(baseUrl), status, body);
    }

    // ── imports (an importer's content files, per environment) ──────────────

    string EnvRev(string workbenchId, string environmentId, string revision) =>
        $"{Wb(workbenchId)}/environments/{environmentId}/revisions/{Uri.EscapeDataString(revision)}";

    /// <summary>
    /// Import one address in an environment and return its slot: the import manifest
    /// (which names the artifacts) or a per-slot error such as <c>import_not_found</c>.
    /// Addresses: <c>G:{guid}</c> is the primary import result, <c>T:{guid}+{importer type}</c>
    /// one importer's. The first requests after a cold start can answer
    /// <c>409 revision_not_validated</c> for minutes; re-issue them.
    /// </summary>
    public async Task<ImportSlot> RequestImportAsync(string workbenchId, string environmentId, string revision,
        string address, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var url = $"{EnvRev(workbenchId, environmentId, revision)}/imports";
        var body = EnsureOk("POST", url, await ReadAsync(HttpMethod.Post, url, new { addresses = new[] { address } },
            "application/json", progress, ct).ConfigureAwait(false));
        return JsonSerializer.Deserialize<ImportResults>(body, Json)?.Results.FirstOrDefault()
               ?? throw new PipelineApiException("POST", ShortPath(url), 200, null, "no result slot in the answer", null, false);
    }

    /// <summary>One artifact of an import, by the name its manifest lists.</summary>
    public async Task<byte[]> GetImportArtifactAsync(string workbenchId, string environmentId, string revision,
        string address, string name, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var url = $"{EnvRev(workbenchId, environmentId, revision)}/imports/{Segment(address)}/{Segment(name)}";
        return EnsureOk("GET", url, await ReadAsync(HttpMethod.Get, url, null, "*/*", progress, ct).ConfigureAwait(false));
    }

    // ── writes ──────────────────────────────────────────────────────────────
    // Every change becomes a new workbench revision, which the cloud validates
    // before it can be read. Nothing here pushes to git (that's Publish).

    /// <summary>Stage bytes under a content GUID (idempotent for identical bytes).</summary>
    public async Task<BlobUpload> UploadBlobAsync(string workbenchId, byte[] content, CancellationToken ct = default)
    {
        var contentGuid = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(content)).ToLowerInvariant();
        var url = $"{Wb(workbenchId)}/blobs/{contentGuid}";
        var body = new ByteArrayContent(content);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var (status, bytes) = await SendAsync(HttpMethod.Put, url, null, body, "application/json",
            TimeSpan.FromMinutes(3), ct).ConfigureAwait(false);
        if (status is < 200 or >= 300) throw PipelineApiException.FromResponse("PUT", ShortPath(url), status, bytes);
        return JsonSerializer.Deserialize<BlobUpload>(bytes, Json)!;
    }

    /// <summary>
    /// Stage and commit in one call (the broker owns the transaction). Only saves
    /// into EXISTING folders and deletes; no folder creation, no renames, no message.
    /// </summary>
    public Task<CommitResult> CommitBatchAsync(string workbenchId,
        IEnumerable<(string Path, string UploadHandle)>? stage, IEnumerable<string>? delete, CancellationToken ct = default) =>
        JsonAsync<CommitResult>(HttpMethod.Post, $"{Wb(workbenchId)}/batch", new
        {
            stage = stage?.Select(s => new { path = s.Path, uploadHandle = s.UploadHandle }).ToArray(),
            delete = delete?.ToArray(),
        }, TimeSpan.FromMinutes(3), ct);

    /// <summary>One open transaction per workbench; it auto-aborts after an idle timeout.</summary>
    public async Task<string> OpenTransactionAsync(string workbenchId, string branch, CancellationToken ct = default) =>
        (await JsonAsync<OpenedTransaction>(HttpMethod.Post, $"{Wb(workbenchId)}/transactions",
            new { branchName = branch }, ct: ct).ConfigureAwait(false)).TransactionId;

    string Tx(string workbenchId, string tx) => $"{Wb(workbenchId)}/transactions/{tx}";

    public Task StageFilesAsync(string workbenchId, string tx, IEnumerable<(string Path, string UploadHandle)> files,
        CancellationToken ct = default) =>
        NoContentAsync(HttpMethod.Post, $"{Tx(workbenchId, tx)}/assets",
            files.Select(f => new { path = f.Path, uploadHandle = f.UploadHandle }).ToArray(), ct, TimeSpan.FromMinutes(3));

    /// <summary>Folders must exist before files are staged into them.</summary>
    public Task CreateFoldersAsync(string workbenchId, string tx, IEnumerable<string> folders, CancellationToken ct = default) =>
        NoContentAsync(HttpMethod.Post, $"{Tx(workbenchId, tx)}/assets",
            new { type = "folders", paths = folders.ToArray() }, ct, TimeSpan.FromMinutes(3));

    /// <summary>
    /// Rename/move keeping the GUID (the .meta moves with it). Delete + re-upload
    /// would mint a new GUID and break references. Can take a while on a cold snapshot.
    /// </summary>
    public Task StageMovesAsync(string workbenchId, string tx, IEnumerable<(string From, string To)> moves,
        CancellationToken ct = default) =>
        NoContentAsync(HttpMethod.Patch, $"{Tx(workbenchId, tx)}/assets",
            new { type = "move", pathsToMove = moves.Select(m => new[] { m.From, m.To }).ToArray() }, ct, TimeSpan.FromMinutes(5));

    public Task<CommitResult> CommitTransactionAsync(string workbenchId, string tx, string? message,
        CancellationToken ct = default) =>
        JsonAsync<CommitResult>(HttpMethod.Patch, Tx(workbenchId, tx),
            message is null ? new { status = "committed" } : new { status = "committed", message }, TimeSpan.FromMinutes(3), ct);

    public Task AbortTransactionAsync(string workbenchId, string tx) =>
        NoContentAsync(HttpMethod.Delete, Tx(workbenchId, tx));

    /// <summary>Folders on the way to <paramref name="folder"/> that don't exist at <paramref name="revision"/>, outermost first.</summary>
    public async Task<IReadOnlyList<string>> MissingFoldersAsync(string workbenchId, string revision, string folder,
        CancellationToken ct = default)
    {
        var parts = folder.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var missing = new List<string>();
        for (var i = 1; i < parts.Length; i++)
        {
            var child = string.Join('/', parts[..(i + 1)]);
            if (missing.Count > 0) { missing.Add(child); continue; }
            var tree = await GetFileTreeAsync(workbenchId, revision, string.Join('/', parts[..i]), ct).ConfigureAwait(false);
            if (!tree.Entries.Any(e => e.IsFolder && e.RelativePath == child)) missing.Add(child);
        }
        return missing;
    }

    /// <summary>
    /// Add or overwrite files. Uses /batch when every target folder exists, otherwise a
    /// transaction that creates the missing folders first. Returns the new revision.
    /// </summary>
    public async Task<string> SaveFilesAsync(string workbenchId, string baseRevision, string branch,
        IReadOnlyList<(string Path, byte[] Content)> files, string? message = null, CancellationToken ct = default)
    {
        var staged = new List<(string, string)>();
        foreach (var (path, content) in files)
            staged.Add((path, (await UploadBlobAsync(workbenchId, content, ct).ConfigureAwait(false)).UploadHandle));

        var missing = new List<string>();
        foreach (var folder in files.Select(f => Path.GetDirectoryName(f.Path)!.Replace('\\', '/')).Distinct())
            foreach (var m in await MissingFoldersAsync(workbenchId, baseRevision, folder, ct).ConfigureAwait(false))
                if (!missing.Contains(m)) missing.Add(m);

        if (missing.Count == 0 && message is null)
            return Revision(await CommitBatchAsync(workbenchId, staged, null, ct).ConfigureAwait(false));

        return await InTransactionAsync(workbenchId, branch, message, async tx =>
        {
            if (missing.Count > 0) await CreateFoldersAsync(workbenchId, tx, missing, ct).ConfigureAwait(false);
            await StageFilesAsync(workbenchId, tx, staged, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Delete files (their .meta goes with them). Returns the new revision.</summary>
    public async Task<string> DeleteFilesAsync(string workbenchId, IReadOnlyList<string> paths, CancellationToken ct = default) =>
        Revision(await CommitBatchAsync(workbenchId, null, paths, ct).ConfigureAwait(false));

    /// <summary>Rename or move one asset, keeping its GUID. Returns the new revision.</summary>
    public async Task<string> MoveAsync(string workbenchId, string baseRevision, string branch, string from, string to,
        string? message = null, CancellationToken ct = default)
    {
        var missing = await MissingFoldersAsync(workbenchId, baseRevision,
            Path.GetDirectoryName(to)!.Replace('\\', '/'), ct).ConfigureAwait(false);
        return await InTransactionAsync(workbenchId, branch, message ?? $"Move {from} to {to}", async tx =>
        {
            if (missing.Count > 0) await CreateFoldersAsync(workbenchId, tx, missing, ct).ConfigureAwait(false);
            await StageMovesAsync(workbenchId, tx, [(from, to)], ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Open, stage, commit; abort if staging fails so the workbench isn't left holding a transaction.</summary>
    async Task<string> InTransactionAsync(string workbenchId, string branch, string? message, Func<string, Task> stage,
        CancellationToken ct)
    {
        var tx = await OpenTransactionAsync(workbenchId, branch, ct).ConfigureAwait(false);
        try
        {
            await stage(tx).ConfigureAwait(false);
        }
        catch
        {
            try { await AbortTransactionAsync(workbenchId, tx).ConfigureAwait(false); } catch { /* best effort */ }
            throw;
        }
        return Revision(await CommitTransactionAsync(workbenchId, tx, message, ct).ConfigureAwait(false));
    }

    static string Revision(CommitResult r) =>
        r.RevisionId ?? throw new InvalidOperationException("the commit answered without a revision");

    static bool IsPng(byte[] b) => b.Length > 8 && b[0] == 0x89 && b[1] == (byte)'P' && b[2] == (byte)'N' && b[3] == (byte)'G';
}

public sealed class ValidationFailedException(ValidationError? error)
    : Exception($"validation failed: {error?.Category}: {error?.Message}")
{
    public ValidationError? Error { get; } = error;
}
