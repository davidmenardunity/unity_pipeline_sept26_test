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
    readonly string collaborationBase;  // Unity Cloud Collaboration (annotations), same public host

    public PipelineConfig Config { get; }

    /// <summary>Raised as each call is sent (for showing pending calls).</summary>
    public event Action<ApiCallStarted>? CallStarted;

    /// <summary>Raised after every call, success or failure, with the id from <see cref="CallStarted"/>.</summary>
    public event Action<ApiCall>? CallCompleted;

    long nextCallId;

    // Calls made while polling (readiness, jobs) are flagged so the activity
    // panel can group them instead of listing every repeat.
    readonly AsyncLocal<bool> polling = new();

    /// <summary>Calls made inside this scope are reported as polls (hidden in the activity by default).</summary>
    public IDisposable AsPolling() => Polling();

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
        collaborationBase = $"{client}/collaboration/v1";
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
        HttpContent? content, string accept, TimeSpan timeout, CancellationToken ct, string? bearer = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Accept.ParseAdd(accept);
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
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
        url.Replace(clientBase, "…").Replace(internalBase, "…(internal)").Replace(internalHost, "(internal)")
            .Replace(collaborationBase, "(collaboration)");

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

    // ── VCS connections ─────────────────────────────────────────────────────

    /// <summary>
    /// The project's source-control connections (Build Automation v3: id, name, type, url, gitProvider):
    /// what a workbench's vcsConnectionId names. Read-only here; they're created in the dashboard.
    /// </summary>
    public async Task<byte[]> ListVcsConnectionsJsonAsync(CancellationToken ct = default)
    {
        var host = Config.Environment == "production" ? "build-automation.services.api.unity.com" : "build-automation.staging.services.api.unity.com";
        var url = $"https://{host}/v3/orgs/{Uri.EscapeDataString(Config.OrganizationId)}/projects/{Uri.EscapeDataString(Config.ProjectId)}/connections";
        var (status, bytes) = await SendAsync(HttpMethod.Get, url, null, null, "application/json", TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        if (status is < 200 or >= 300) throw PipelineApiException.FromResponse("GET", url, status, bytes);
        return bytes;
    }

    /// <summary>
    /// Create a git connection for the project (Build Automation's internal v2 API) that reaches
    /// <paramref name="repositoryUrl"/> over HTTPS as <paramref name="user"/> with <paramref name="password"/>
    /// (a personal access token that can push). Returns the connection as JSON (its id, name, type, url).
    /// </summary>
    /// <remarks>
    /// Sent directly, not through SendAsync: the body carries the token, and the activity log records
    /// bodies. The answer doesn't include it (Build Automation never returns credentials).
    /// </remarks>
    public async Task<byte[]> CreateVcsConnectionAsync(string name, string repositoryUrl, string? user, string? password,
        CancellationToken ct = default)
    {
        var url = $"{internalHost}/api/build-automation/v2/orgs/{Uri.EscapeDataString(Config.OrganizationId)}/projects/{Uri.EscapeDataString(Config.ProjectId)}/connections";
        // No credentials: a read-only connection to a public repo.
        var body = user is null ? JsonSerializer.Serialize(new { type = "git", url = repositoryUrl, name })
            : JsonSerializer.Serialize(new { type = "git", url = repositoryUrl, name, user, pass = password });
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        using var response = await http.SendAsync(request, cts.Token).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
        if (status is < 200 or >= 300)
        {
            // The whole answer (validation errors list the fields in `details`), with the token blanked
            // in case it's echoed back.
            var text = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 3000));
            if (!string.IsNullOrEmpty(password)) text = text.Replace(password, "(token)");
            throw new PipelineApiException("POST", ShortPath(url), status, null, text, null, false);
        }
        return bytes;
    }

    /// <summary>Build Automation checks the connection through its provider (nothing is stored). Null when it's fine, else why not.</summary>
    public async Task<string?> ValidateVcsConnectionAsync(string connectionId, CancellationToken ct = default)
    {
        var host = Config.Environment == "production" ? "build-automation.services.api.unity.com" : "build-automation.staging.services.api.unity.com";
        var url = $"https://{host}/v3/orgs/{Uri.EscapeDataString(Config.OrganizationId)}/projects/{Uri.EscapeDataString(Config.ProjectId)}/connections/{Uri.EscapeDataString(connectionId)}/validate";
        var (status, bytes) = await SendAsync(HttpMethod.Post, url, new { }, null, "application/json", TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        return status is >= 200 and < 300 ? null : $"HTTP {status}: {System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 1500))}";
    }

    public Task DeleteVcsConnectionAsync(string connectionId, CancellationToken ct = default) =>
        NoContentAsync(HttpMethod.Delete,
            $"{internalHost}/api/build-automation/v2/orgs/{Uri.EscapeDataString(Config.OrganizationId)}/projects/{Uri.EscapeDataString(Config.ProjectId)}/connections/{Uri.EscapeDataString(connectionId)}", ct: ct);

    // ── collaboration ───────────────────────────────────────────────────────

    /// <summary>
    /// A Unity Cloud Collaboration call (annotations, threads, attachments), passed through as is:
    /// <paramref name="path"/> is relative to /collaboration/v1 (e.g. "projects/{id}/annotations-search?…").
    /// Any status comes back; nothing is thrown for 4xx/5xx, so the caller can relay it.
    /// </summary>
    public async Task<(int Status, byte[] Body)> CollaborationAsync(HttpMethod method, string path, byte[]? body,
        string? contentType, CancellationToken ct = default)
    {
        HttpContent? content = null;
        if (body is { Length: > 0 })
        {
            content = new ByteArrayContent(body);
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType ?? "application/json");
        }
        // Collaboration only trusts Unity Cloud tokens, not the Genesis (unity-ads) token Pipeline takes:
        // a token given for it, else the configured one exchanged.
        // The gateway takes only JWTs: a Genesis access token (opaque) is exchanged for one first.
        var token = Config.CollaborationToken is { Length: > 0 } given
            ? given.StartsWith("eyJ", StringComparison.Ordinal) ? given
              : await ServicesTokenAsync(ct, given).ConfigureAwait(false)
                ?? throw new PipelineApiException("POST", "genesis-token-exchange", 401, null,
                    "the token for comments couldn't be exchanged for a Unity Services token: it may have expired", null, false)
            : await ServicesTokenAsync(ct).ConfigureAwait(false) ?? Config.Token;
        return await SendAsync(method, $"{collaborationBase}/{path.TrimStart('/')}", null, content, "application/json",
            TimeSpan.FromSeconds(60), ct, token).ConfigureAwait(false);
    }

    (string? Token, DateTime Until, string From)? servicesToken;

    /// <summary>
    /// The configured Genesis token exchanged for a Unity Services token (what com.unity.cloud.identity
    /// does), cached until shortly before it expires.
    /// </summary>
    /// <summary>Whether a Genesis access token can be exchanged for a Unity Services JWT (what comments need).</summary>
    public async Task<bool> CanExchangeGenesisAsync(string genesis, CancellationToken ct = default) =>
        await ServicesTokenAsync(ct, genesis).ConfigureAwait(false) is not null;

    /// <remarks>Null when the exchange refuses the token (not a Genesis token): it's then sent as is.</remarks>
    async Task<string?> ServicesTokenAsync(CancellationToken ct, string? genesis = null)
    {
        genesis ??= Config.Token;
        if (servicesToken is { } t && t.From == genesis && DateTime.UtcNow < t.Until) return t.Token;
        // Sent directly, not through SendAsync: both bodies are tokens, and the activity log records bodies.
        var url = $"{internalHost}/api/auth/v1/genesis-token-exchange/unity";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { token = genesis }), System.Text.Encoding.UTF8, "application/json"),
        };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        // A plain client: the exchange takes the token in its body, not as a bearer.
        using var plain = new HttpClient();
        using var response = await plain.SendAsync(request, cts.Token).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
        if (status is < 200 or >= 300)
        {
            servicesToken = (null, DateTime.UtcNow.AddMinutes(10), genesis);   // don't ask again on every call
            return null;
        }
        using var doc = JsonDocument.Parse(bytes);
        var token = doc.RootElement.GetProperty("token").GetString()
                    ?? throw new PipelineApiException("POST", ShortPath(url), status, null, "no token in the exchange response", null, false);
        var until = DateTime.UtcNow.AddMinutes(10);
        try
        {
            var payload = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var claims = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (claims.RootElement.TryGetProperty("exp", out var exp))
                until = DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64()).UtcDateTime - TimeSpan.FromMinutes(2);
        }
        catch (Exception e) when (e is FormatException or JsonException or IndexOutOfRangeException) { }
        servicesToken = (token, until, genesis);
        return token;
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
            if (job.Shown != shown) progress?.Report($"job {jobId[..Math.Min(8, jobId.Length)]}: {job.Shown}");
            shown = job.Shown;
            if (job.IsDone) return job;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"job {jobId} still {job.Shown} after {budget.TotalSeconds:0}s");
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads such as asset-guid and source can answer 202 the first time (the project service
    /// has to produce the data). With a jobId: wait for the job, then ask again. Without one
    /// (since October 2026 a plain "not yet"): wait a little and ask again, unless the answer
    /// says it's terminal (e.g. a preview for an asset type that has none).
    /// </summary>
    async Task<(int Status, byte[] Body)> ReadAsync(HttpMethod method, string url, object? body, string accept,
        IProgress<string>? progress, CancellationToken ct)
    {
        var jobWaits = 0;
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(10);
        while (true)
        {
            var (status, bytes) = await SendAsync(method, url, body, null, accept, TimeSpan.FromSeconds(120), ct)
                .ConfigureAwait(false);
            if (status != 202 || IsTerminal(bytes) || DateTime.UtcNow > deadline) return (status, bytes);
            if (TryJobId(bytes) is { } job)
            {
                if (jobWaits++ >= 3) return (status, bytes);
                await WaitForJobAsync(job, TimeSpan.FromMinutes(10), progress, ct).ConfigureAwait(false);
            }
            else
            {
                using (Polling())
                    await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            }
        }
    }

    static bool IsTerminal(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("disposition", out var d) && d.GetString() == "terminal";
        }
        catch (JsonException) { return false; }
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

    // Since October 2026 the project service is addressed per project (…/projects/{p}/projectservice/…);
    // until then it was per branch (…/branches/{b}/projectservice/…), which now answers a gateway 404
    // (code 54, no route). Try the project-level route, and fall back while either may be served.
    string ProjectServiceUrl(string action) => $"{internalBase}/projectservice/{action}";
    string BranchProjectServiceUrl(string action) =>
        $"{internalBase}/branches/{Uri.EscapeDataString(Config.Branch)}/projectservice/{action}";

    static bool IsNoRoute(int status, byte[] body) =>
        status == 404 && PipelineApiException.FromResponse("", "", status, body).Code == "54";

    public async Task<ProjectServiceStatus> GetProjectServiceStatusAsync(CancellationToken ct = default)
    {
        try
        {
            return await JsonAsync<ProjectServiceStatus>(HttpMethod.Get, ProjectServiceUrl("status"), ct: ct).ConfigureAwait(false);
        }
        catch (PipelineApiException e) when (e is { Status: 404, Code: "54" })
        {
            return await JsonAsync<ProjectServiceStatus>(HttpMethod.Get, BranchProjectServiceUrl("status"), ct: ct).ConfigureAwait(false);
        }
    }

    /// <summary>Ask for the project service to start, without waiting. A 409 (already starting or running) is fine.</summary>
    public async Task StartProjectServiceAsync(CancellationToken ct = default)
    {
        var url = ProjectServiceUrl("start");
        var (code, body) = await SendAsync(HttpMethod.Post, url, new { region = Config.Region }, null,
            "application/json", TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        if (IsNoRoute(code, body))
        {
            url = BranchProjectServiceUrl("start");
            (code, body) = await SendAsync(HttpMethod.Post, url, new { region = Config.Region }, null,
                "application/json", TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        }
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

    /// <summary>The org's members (id, name, email), as the access API returns them: for @mentions.</summary>
    public async Task<byte[]> GetMembersJsonAsync(CancellationToken ct = default)
    {
        var url = $"{internalHost}/api/access/legacy/v1/organizations/{Uri.EscapeDataString(Config.OrganizationId)}/members?limit=1000&offset=0";
        var (status, bytes) = await SendAsync(HttpMethod.Get, url, null, null, "application/json", TimeSpan.FromSeconds(30), ct)
            .ConfigureAwait(false);
        if (status is < 200 or >= 300) throw PipelineApiException.FromResponse("GET", ShortPath(url), status, bytes);
        return bytes;
    }

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
    public async Task<IReadOnlyList<Workbench>> ListWorkbenchesAsync(CancellationToken ct = default)
    {
        // Until October 2026 the list was {items:[…]} with each workbench's branchName; since then it is
        // {workbenches:[…]} without branchName. Accept both, and read a missing branch from the workbench.
        var list = await JsonAsync<WorkbenchList>(HttpMethod.Get, $"{clientBase}/workbenches", ct: ct).ConfigureAwait(false);
        var items = list.Items ?? list.Workbenches ?? [];
        var filled = await Task.WhenAll(items.Select(async w =>
        {
            if (!string.IsNullOrEmpty(w.BranchName)) return w;
            try
            {
                var details = await GetWorkbenchAsync(w.WorkbenchId, ct).ConfigureAwait(false);
                return details with { CreatedAt = details.CreatedAt ?? w.CreatedAt };
            }
            catch (PipelineApiException) { return w; }   // still listed, just without its branch
        })).ConfigureAwait(false);
        return filled;
    }

    /// <summary>
    /// One workbench. Since October 2026 the answer has neither workbenchId nor branchName:
    /// the id is the one asked for, and the branch is the plain key of its <c>branches</c> map
    /// (the others are "{branch}/{guid}" working branches).
    /// </summary>
    public async Task<Workbench> GetWorkbenchAsync(string workbenchId, CancellationToken ct = default)
    {
        var wb = await JsonAsync<Workbench>(HttpMethod.Get, Wb(workbenchId), timeout: TimeSpan.FromSeconds(90), ct: ct)
            .ConfigureAwait(false);
        return wb with
        {
            WorkbenchId = string.IsNullOrEmpty(wb.WorkbenchId) ? workbenchId : wb.WorkbenchId,
            BranchName = !string.IsNullOrEmpty(wb.BranchName) ? wb.BranchName
                : wb.Branches?.Keys.FirstOrDefault(k => !k.Contains('/')) ?? wb.Branches?.Keys.FirstOrDefault(),
        };
    }

    /// <summary>A git workbench on <paramref name="branch"/>; without a name Pipeline generates "wb-{uuid}".</summary>
    /// <remarks>
    /// With <paramref name="vcsConnectionId"/>, Pipeline reaches the repo through that VCS connection (its
    /// credentials), which is what lets it publish (push) back; without one it clones the public URL, read-only.
    /// </remarks>
    public Task<Workbench> CreateWorkbenchAsync(string repositoryUrl, string branch, string? name = null, CancellationToken ct = default,
        string? vcsConnectionId = null)
    {
        var body = new System.Text.Json.Nodes.JsonObject { ["type"] = "git", ["branch"] = branch, ["repo"] = repositoryUrl };
        if (!string.IsNullOrWhiteSpace(name)) body["name"] = name.Trim();
        if (!string.IsNullOrWhiteSpace(vcsConnectionId)) body["vcsConnectionId"] = vcsConnectionId.Trim();
        return JsonAsync<Workbench>(HttpMethod.Post, $"{clientBase}/workbenches", body, TimeSpan.FromSeconds(120), ct);
    }

    /// <summary>Soft stop: releases the worker; the workbench leaves the listing but can still be read.</summary>
    public Task RetireWorkbenchAsync(string workbenchId, CancellationToken ct = default) =>
        NoContentAsync(HttpMethod.Post, $"{Wb(workbenchId)}/stop", ct: ct);

    public Task DeleteWorkbenchAsync(string workbenchId, CancellationToken ct = default) =>
        NoContentAsync(HttpMethod.Delete, Wb(workbenchId), ct: ct);

    /// <summary>
    /// Where the workbench stands: settled (with the revision reads can use), settling, gone or unknown.
    /// Since October 2026 that's …/head {branch, head, settledRevision, settling}; …/readiness
    /// (gone since then) is still tried if …/head has no route. head's own fields changed later the same
    /// month (validatedRevision/validating); see <see cref="WorkbenchHead"/>.
    /// </summary>
    public async Task<WorkbenchReadiness> GetReadinessAsync(string workbenchId, CancellationToken ct = default)
    {
        try
        {
            var h = await JsonAsync<WorkbenchHead>(HttpMethod.Get, $"{Wb(workbenchId)}/head", ct: ct).ConfigureAwait(false);
            var readiness = h.Busy ? "settling" : string.IsNullOrEmpty(h.Revision) ? "unknown" : "settled";
            return new WorkbenchReadiness(workbenchId, readiness, h.Branch, h.Revision, h.Head);
        }
        catch (PipelineApiException e) when (e is { Status: 404, Code: "54" })
        {
            return await JsonAsync<WorkbenchReadiness>(HttpMethod.Get, $"{Wb(workbenchId)}/readiness", ct: ct).ConfigureAwait(false);
        }
    }

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

    // Since October 2026 environments are project-level (…/environments) and bind a build profile
    // (…/profiles, one per build target) to a workbench; until then they were per workbench
    // (…/workbenches/{wb}/environments {platform}). The new routes are tried first.

    /// <summary>Platform names this client uses (Windows64…) and the profiles' build targets (win64…).</summary>
    static readonly (string Platform, string BuildTarget)[] Platforms =
    [
        ("Windows64", "win64"), ("Linux64", "linux64"), ("MacOS64", "osxuniversal"),
        ("iOS", "ios"), ("Android", "android"), ("WebGL", "webgl"),
    ];

    static string BuildTargetFor(string platform) =>
        Platforms.FirstOrDefault(p => p.Platform.Equals(platform, StringComparison.OrdinalIgnoreCase)).BuildTarget
        ?? platform.ToLowerInvariant();

    static string? PlatformFor(string? buildTarget) =>
        buildTarget is null ? null
            : Platforms.FirstOrDefault(p => p.BuildTarget.Equals(buildTarget, StringComparison.OrdinalIgnoreCase)).Platform ?? buildTarget;

    public async Task<IReadOnlyList<BuildProfileInfo>> ListProfilesAsync(CancellationToken ct = default)
    {
        var list = await JsonAsync<ProfileList>(HttpMethod.Get, $"{clientBase}/profiles", ct: ct).ConfigureAwait(false);
        return list.Profiles ?? list.Items ?? [];
    }

    /// <summary>
    /// Environments of this workbench, with their platform. (The list can include other
    /// workbenches' environments, so filter.)
    /// </summary>
    public async Task<IReadOnlyList<PipelineEnvironment>> ListEnvironmentsAsync(string workbenchId,
        CancellationToken ct = default)
    {
        try
        {
            var list = await JsonAsync<EnvironmentList>(HttpMethod.Get, $"{clientBase}/environments", ct: ct).ConfigureAwait(false);
            var envs = (list.Environments ?? list.Items ?? []).Where(e => e.WorkbenchId == workbenchId).ToList();
            if (envs.Count == 0 || envs.All(e => e.Platform is not null)) return envs;
            var profiles = (await ListProfilesAsync(ct).ConfigureAwait(false)).ToDictionary(p => p.ProfileId, p => p);
            return envs.Select(e => e.Platform is not null ? e : e with
            {
                Platform = e.ProfileId is { } id && profiles.TryGetValue(id, out var p) ? PlatformFor(p.BuildTarget) : null,
            }).ToList();
        }
        catch (PipelineApiException e) when (e is { Status: 404, Code: "54" })
        {
            var page = await JsonAsync<Page<PipelineEnvironment>>(HttpMethod.Get, $"{Wb(workbenchId)}/environments", ct: ct)
                .ConfigureAwait(false);
            return page.Items.Where(x => x.WorkbenchId == workbenchId).ToList();
        }
    }

    /// <summary>An environment for <paramref name="platform"/> (Windows64…), creating its build profile if needed.</summary>
    public async Task<PipelineEnvironment> CreateEnvironmentAsync(string workbenchId, string platform,
        CancellationToken ct = default)
    {
        IReadOnlyList<BuildProfileInfo> profiles;
        try { profiles = await ListProfilesAsync(ct).ConfigureAwait(false); }
        catch (PipelineApiException e) when (e is { Status: 404, Code: "54" })
        {
            return await JsonAsync<PipelineEnvironment>(HttpMethod.Post, $"{Wb(workbenchId)}/environments", new { platform }, ct: ct)
                .ConfigureAwait(false);
        }

        var buildTarget = BuildTargetFor(platform);
        var profile = profiles.FirstOrDefault(p => string.Equals(p.BuildTarget, buildTarget, StringComparison.OrdinalIgnoreCase))
                      ?? await JsonAsync<BuildProfileInfo>(HttpMethod.Post, $"{clientBase}/profiles",
                          new { name = $"explorer-{buildTarget}", buildTarget }, ct: ct).ConfigureAwait(false);
        // The project-level create requires "platform" (400 "not well formed: Platform" without it). Its
        // spelling isn't documented: try the platform name (Windows64, as the per-workbench route took),
        // then the profile's build target (win64). The ids go under both spellings seen so far.
        PipelineEnvironment? env = null;
        PipelineApiException? rejected = null;
        foreach (var value in new[] { platform, buildTarget }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                env = await JsonAsync<PipelineEnvironment>(HttpMethod.Post, $"{clientBase}/environments", new
                {
                    platform = value,
                    workbenchId,
                    workbenchGuid = workbenchId,
                    profileId = profile.ProfileId,
                    profileGuid = profile.ProfileId,
                    name = $"explorer-{buildTarget}-{workbenchId[..8]}",
                }, ct: ct).ConfigureAwait(false);
                break;
            }
            catch (PipelineApiException e) when (e.Status == 400)
            {
                rejected = e;
            }
        }
        if (env is null) throw rejected!;
        return env with { Platform = env.Platform ?? platform, WorkbenchId = env.WorkbenchId ?? workbenchId };
    }

    // ── reads (all pinned to a revision) ────────────────────────────────────

    string Rev(string workbenchId, string revision) => $"{Wb(workbenchId)}/revisions/{Uri.EscapeDataString(revision)}";

    /// <summary>One level of a folder.</summary>
    public async Task<FileTree> GetFileTreeAsync(string workbenchId, string revision, string folder,
        CancellationToken ct = default)
    {
        var tree = await JsonAsync<FileTreeResponse>(HttpMethod.Get,
            $"{Rev(workbenchId, revision)}/file-tree/{Segment(folder)}", ct: ct).ConfigureAwait(false);
        var entries = (tree.Entries ?? []).Select(e => new FileTreeEntry(e.Path,
            e.IsFolder ?? string.Equals(e.Type, "folder", StringComparison.OrdinalIgnoreCase), e.Kind)).ToList();
        return new FileTree(entries, tree.Revision);
    }

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
        // The body field was "keys" until October 2026, now "paths" (required). Send both while either may be served.
        var paths = new[] { path };
        var body = EnsureOk("POST", url, await ReadAsync(HttpMethod.Post, url, new { paths, keys = paths },
            "application/json", progress, ct).ConfigureAwait(false));
        var results = JsonSerializer.Deserialize<GuidResults>(body, Json);
        return results?.Results?.FirstOrDefault()?.AssetGuid;
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
        // Since October 2026: GET …/environments/{env}/revisions/{rev}/previews/{guid} (project-level
        // environment), which answers 202 until the render is done. The POST trigger below is the old route.
        var url = $"{EnvRev(workbenchId, environmentId, revision)}/previews/{guid}";
        var (code, bytes) = await ReadAsync(HttpMethod.Get, url, null, "image/*, application/json", progress, ct)
            .ConfigureAwait(false);
        if (code == 200 && IsPng(bytes)) return bytes;
        if (code == 202) return null;   // no image for this asset type (or still rendering after 10 min)
        if (!IsNoRoute(code, bytes)) throw PipelineApiException.FromResponse("GET", ShortPath(url), code, bytes);

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

    // Environments are project-level since October 2026 (until then …/workbenches/{wb}/environments/…).
    // The workbench id stays in the signatures: the environment is bound to it.
    string EnvRev(string workbenchId, string environmentId, string revision) =>
        $"{clientBase}/environments/{environmentId}/revisions/{Uri.EscapeDataString(revision)}";

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
        // The body field was "addresses" until October 2026, now "importAddresses" (required). Send both.
        var addresses = new[] { address };
        var body = EnsureOk("POST", url, await ReadAsync(HttpMethod.Post, url, new { importAddresses = addresses, addresses },
            "application/json", progress, ct).ConfigureAwait(false));
        var text = System.Text.Encoding.UTF8.GetString(body);
        return JsonSerializer.Deserialize<ImportResults>(body, Json)?.Results?.FirstOrDefault()
               ?? throw new PipelineApiException("POST", ShortPath(url), 200, null,
                   $"no result slot in the answer: {text[..Math.Min(300, text.Length)]}", null, false);
    }

    /// <summary>One artifact of an import, by the name its manifest lists.</summary>
    public async Task<byte[]> GetImportArtifactAsync(string workbenchId, string environmentId, string revision,
        string address, string name, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        // The main import result's artifact has an empty name, which can't be a path segment: name it in
        // the query string instead (?artifactName=), as Project Service serves it. Named artifacts use the
        // path form, falling back to the query form when the path has no route.
        var import = $"{EnvRev(workbenchId, environmentId, revision)}/imports/{Segment(address)}";
        var byQuery = $"{import}?artifactName={Uri.EscapeDataString(name)}";
        if (string.IsNullOrEmpty(name))
        {
            // ?artifactName= (empty) answers the import manifest again, so ask for "." (the other spelling
            // Project Service gives the main artifact's name).
            var main = $"{import}?artifactName=.";
            return EnsureOk("GET", main, await ReadAsync(HttpMethod.Get, main, null, "*/*", progress, ct).ConfigureAwait(false));
        }

        var byPath = $"{import}/{Segment(name)}";
        var answer = await ReadAsync(HttpMethod.Get, byPath, null, "*/*", progress, ct).ConfigureAwait(false);
        if (IsNoRoute(answer.Status, answer.Body))
            return EnsureOk("GET", byQuery, await ReadAsync(HttpMethod.Get, byQuery, null, "*/*", progress, ct).ConfigureAwait(false));
        return EnsureOk("GET", byPath, answer);
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
        {
            try
            {
                return Revision(await CommitBatchAsync(workbenchId, staged, null, ct).ConfigureAwait(false));
            }
            catch (PipelineApiException e) when (e is { Status: 404, Code: "54" })
            {
                // …/batch has no route since October 2026: commit through a transaction instead.
            }
        }

        return await InTransactionAsync(workbenchId, branch, message, async tx =>
        {
            if (missing.Count > 0) await CreateFoldersAsync(workbenchId, tx, missing, ct).ConfigureAwait(false);
            await StageFilesAsync(workbenchId, tx, staged, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Ask the workbench to sync with its upstream git branch (PATCH …/workbenches/{wb} {type: "sync"}, as the
    /// Scene Preview sample did). Returns the answer as text; a 202 with a job is waited for.
    /// </summary>
    public async Task<(int Status, string Body)> SyncWorkbenchAsync(string workbenchId, IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var url = Wb(workbenchId);
        var (status, body) = await SendAsync(HttpMethod.Patch, url, new { type = "sync" }, null, "application/json",
            TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
        if (status == 202 && TryJobId(body) is { } job)
        {
            await WaitForJobAsync(job, TimeSpan.FromMinutes(15), progress, ct).ConfigureAwait(false);
            return (status, System.Text.Encoding.UTF8.GetString(body));
        }
        if (status is < 200 or >= 300) throw PipelineApiException.FromResponse("PATCH", ShortPath(url), status, body);
        return (status, System.Text.Encoding.UTF8.GetString(body));
    }

    /// <summary>
    /// Publish the workbench's changes to its upstream git branch (POST …/publishes). Synchronous, can take
    /// minutes; a 202 with a job is waited for. Pushes under a git token configured on Pipeline's side, not
    /// the caller's. 409 publish_drafted means it landed on a draft branch instead: that's an outcome, not an
    /// error. The answer's body isn't documented here, so it comes back as text.
    /// </summary>
    public async Task<PublishResult> PublishAsync(string workbenchId, string? message = null,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var url = $"{Wb(workbenchId)}/publishes";
        var (status, body) = await SendAsync(HttpMethod.Post, url, message is null ? new { } : new { message }, null,
            "application/json", TimeSpan.FromMinutes(15), ct).ConfigureAwait(false);
        if (status == 202 && TryJobId(body) is { } job)
        {
            progress?.Report($"publishing (job {job[..Math.Min(8, job.Length)]})");
            var done = await WaitForJobAsync(job, TimeSpan.FromMinutes(15), progress, ct).ConfigureAwait(false);
            return new PublishResult(done.Shown ?? "done", status, System.Text.Encoding.UTF8.GetString(body));
        }
        if (status is >= 200 and < 300)
            return new PublishResult("published", status, System.Text.Encoding.UTF8.GetString(body));
        var failure = PipelineApiException.FromResponse("POST", ShortPath(url), status, body);
        if (status == 409 && failure.Code == "publish_drafted")
            return new PublishResult("drafted", status, System.Text.Encoding.UTF8.GetString(body));
        throw failure;
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
