using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Pipeline.Client;

// Pipeline Explorer: a small local web app for trying the Unity Pipeline APIs.
// The server holds the bearer token (read from pipeline-onboard.config) and
// calls the pipeline through the repo's typed client; the page only talks to
// this server, on 127.0.0.1.

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://127.0.0.1:5280");
builder.Services.AddSingleton<Pipelines>();
// Dropped files go through this server: allow assets bigger than the 30 MB default.
const long MaxUpload = 512L * 1024 * 1024;
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = MaxUpload);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = MaxUpload);
var app = builder.Build();

// Only this machine's browser, and only pages that send our header (a plain
// cross-site form or fetch can't), so other sites can't drive the API with your token.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Host.Host is not ("localhost" or "127.0.0.1"))
    {
        ctx.Response.StatusCode = 403;
        return;
    }
    if (ctx.Request.Path.StartsWithSegments("/api") && ctx.Request.Headers["X-Pipeline-Explorer"] != "1")
    {
        ctx.Response.StatusCode = 403;
        await ctx.Response.WriteAsJsonAsync(new { error = "missing X-Pipeline-Explorer header" });
        return;
    }
    try
    {
        await next();
    }
    catch (Exception e) when (!ctx.Response.HasStarted && e is PipelineApiException or ValidationFailedException
                                  or TimeoutException or InvalidOperationException or NotConfiguredException)
    {
        var api = e as PipelineApiException;
        ctx.Response.StatusCode = e is NotConfiguredException ? 409 : api is { Status: > 0 } ? api.Status : 502;
        await ctx.Response.WriteAsJsonAsync(new
        {
            error = e.Message,
            status = api?.Status,
            code = api?.Code,
            detail = api?.Detail,
            requestId = api?.RequestId,
            vpn = api?.IsVpnBlock,
            bugReport = api?.BugReport,
        });
    }
});

app.UseDefaultFiles();
// Revalidate the page's files on every load, so a rebuilt app is never run from a stale cache.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = f => f.Context.Response.Headers.CacheControl = "no-cache",
});

var api = app.MapGroup("/api");

// ── config and project service ──────────────────────────────────────────────

api.MapGet("/config", (Pipelines p) => p.Describe());

// A bearer token pasted in the page. Kept in this process's memory only: never written to disk,
// never sent back (the page only learns its kind and expiry).
api.MapPost("/token", (Pipelines p, PastedToken body) =>
{
    var token = Pipelines.CleanToken(body.Token);
    if (token.Length == 0) return Results.BadRequest(new { error = "paste a bearer token" });
    if (TokenInfo.Inspect(token).Kind == "unknown")
        return Results.BadRequest(new { error = "that doesn't look like a bearer token (a user JWT starts with eyJ, a service account bearer with unity_sa_bt.)" });
    p.SetToken(token);
    return Results.Ok(p.Describe());
});

// Forget the pasted token and go back to the one in the config file, if any.
api.MapDelete("/token", (Pipelines p) =>
{
    p.SetToken(null);
    return p.Describe();
});

api.MapPost("/config/reload", (Pipelines p) =>
{
    p.Reload();
    return p.Describe();
});

api.MapGet("/service", async (Pipelines p, string? branch, CancellationToken ct) =>
    Service(await p.For(branch).GetProjectServiceStatusAsync(ct)));

// wait=false only asks for the start; the page then polls /service to show the steps.
api.MapPost("/service/start", async (Pipelines p, string? branch, bool? wait, CancellationToken ct) =>
{
    var client = p.For(branch);
    if (wait == false) await client.StartProjectServiceAsync(ct);
    else await client.EnsureProjectServiceAsync(null, ct);
    return Service(await client.GetProjectServiceStatusAsync(ct));
});

// ── org and project picker ──────────────────────────────────────────────────

api.MapGet("/orgs/{org}", async (Pipelines p, string org, CancellationToken ct) =>
    await p.ForOrg(org).GetOrganizationAsync(ct));

api.MapGet("/orgs/{org}/projects", async (Pipelines p, string org, CancellationToken ct) =>
    (await p.ForOrg(org).ListProjectsAsync(ct))
        .Where(x => x.ArchivedAt is null)
        .OrderByDescending(x => x.CreatedAt));

// Point the app at another org/project (until the server restarts; .env is not changed).
api.MapPost("/select", (Pipelines p, Selection body) =>
{
    p.Select(body.OrganizationId.Trim(), body.ProjectId.Trim());
    return p.Describe();
});

// ── branches and workbenches ────────────────────────────────────────────────

api.MapGet("/branches", async (Pipelines p, CancellationToken ct) =>
{
    var c = p.Default;
    var known = new SortedSet<string>(StringComparer.Ordinal) { c.Config.Branch };
    IReadOnlyList<Workbench> workbenches = [];
    try { workbenches = await c.ListWorkbenchesAsync(ct); }
    catch (PipelineApiException) { /* the branch list is still useful without them */ }
    foreach (var wb in workbenches)
        if (wb.BranchName is { Length: > 0 } b) known.Add(b);

    // The repo this project's workbenches track; GIT_REPO_URL if there are none yet.
    var repo = workbenches.OrderByDescending(w => w.CreatedAt).Select(w => w.UpstreamRepository)
        .FirstOrDefault(r => !string.IsNullOrEmpty(r)) ?? c.Config.RepositoryUrl;
    string? gitError = null;
    IReadOnlyDictionary<string, string> heads = new Dictionary<string, string>();
    if (repo is not null)
    {
        try
        {
            heads = await Git.RemoteBranchesAsync(repo, ct);
            known.UnionWith(heads.Keys);
        }
        catch (Exception e) when (e is not OperationCanceledException) { gitError = e.Message; }
    }
    // heads: each branch's latest commit, so the page can tell a workbench made from an older one.
    return new { branches = known, heads, repository = repo, gitError };
});

api.MapGet("/workbenches", async (Pipelines p, CancellationToken ct) => await p.Default.ListWorkbenchesAsync(ct));

api.MapPost("/workbenches", async (Pipelines p, NewWorkbench body, CancellationToken ct) =>
{
    var c = p.Default;
    var repo = string.IsNullOrWhiteSpace(body.Repository) ? c.Config.RepositoryUrl : body.Repository.Trim();
    if (repo is null) return Results.BadRequest(new { error = "no repository: set GIT_REPO_URL in the config or pass one" });
    if (string.IsNullOrWhiteSpace(body.Branch)) return Results.BadRequest(new { error = "branch is required" });
    // Needs the branch's project service running: the page starts it first and shows its progress.
    return Results.Ok(await c.CreateWorkbenchAsync(repo, body.Branch.Trim(), ct));
});

api.MapDelete("/workbenches/{wb}", async (Pipelines p, string wb, CancellationToken ct) =>
{
    await p.Default.DeleteWorkbenchAsync(wb, ct);
    return Results.NoContent();
});

// The workbench plus where it stands: readiness says which revision reads can use.
api.MapGet("/workbenches/{wb}", async (Pipelines p, string wb, CancellationToken ct) =>
{
    var c = p.Default;
    var workbench = await c.GetWorkbenchAsync(wb, ct);
    WorkbenchReadiness? readiness = null;
    string? readinessError = null;
    try { readiness = await c.GetReadinessAsync(wb, ct); }
    catch (PipelineApiException e) { readinessError = e.Message; }
    return new { workbench, readiness, readinessError };
});

api.MapGet("/workbenches/{wb}/environments", async (Pipelines p, string wb, CancellationToken ct) =>
    await p.Default.ListEnvironmentsAsync(wb, ct));

api.MapPost("/workbenches/{wb}/environments", async (Pipelines p, string wb, NewEnvironment body, CancellationToken ct) =>
    await p.Default.CreateEnvironmentAsync(wb, string.IsNullOrWhiteSpace(body.Platform) ? p.Default.Config.Platform : body.Platform, ct));

// ── reads, pinned to a revision ─────────────────────────────────────────────

var rev = api.MapGroup("/workbenches/{wb}/revisions/{rev}");

rev.MapGet("/tree", async (Pipelines p, string wb, string rev, string path, CancellationToken ct) =>
    await p.Default.GetFileTreeAsync(wb, rev, path, ct));

rev.MapGet("/asset", async (Pipelines p, string wb, string rev, string path, CancellationToken ct) =>
{
    var c = p.Default;
    var guid = await c.ResolveGuidAsync(wb, rev, path, null, ct);
    AssetInfo? info = null;
    if (guid is not null)
    {
        try { info = await c.GetAssetAsync(wb, rev, guid, ct); }
        catch (PipelineApiException) { /* the GUID is what matters; facts are a bonus */ }
    }
    return new { path, guid, info };
});

rev.MapGet("/meta", async (Pipelines p, string wb, string rev, string guid, CancellationToken ct) =>
    Bytes(await p.Default.GetMetaAsync(wb, rev, guid, null, ct), $"{guid}.meta"));

rev.MapGet("/file", async (Pipelines p, string wb, string rev, string path, CancellationToken ct) =>
    Bytes(await p.Default.GetFileAsync(wb, rev, path, null, ct), Path.GetFileName(path)));

// Add (or overwrite) files in a folder, from a multipart form ("files"). All of them go
// in one commit, so one new revision, which then validates; returns its id.
rev.MapPost("/files", async (Pipelines p, string wb, string rev, string folder, string? branch, HttpRequest request,
    CancellationToken ct) =>
{
    if (!request.HasFormContentType) return Results.BadRequest(new { error = "expected a multipart form" });
    var form = await request.ReadFormAsync(ct);
    if (form.Files.Count == 0) return Results.BadRequest(new { error = "no files in the form" });
    var files = new List<(string Path, byte[] Content)>();
    foreach (var f in form.Files)
    {
        var name = Path.GetFileName(f.FileName);
        if (name.Length == 0) return Results.BadRequest(new { error = "a file has no name" });
        // The pipeline mints .meta files itself (they carry the GUID); uploading one is refused here.
        if (name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = $"{name}: .meta files are created by the pipeline; drop the asset alone" });
        using var buffer = new MemoryStream();
        await f.CopyToAsync(buffer, ct);
        files.Add(($"{folder.Trim('/')}/{name}", buffer.ToArray()));
    }
    var c = p.Default;
    var revision = await c.SaveFilesAsync(wb, rev, string.IsNullOrWhiteSpace(branch) ? c.Config.Branch : branch.Trim(),
        files, null, ct);
    return Results.Ok(new { revision, paths = files.Select(f => f.Path) });
});

// ── environment reads: previews and imports ─────────────────────────────────

var envRev = api.MapGroup("/workbenches/{wb}/environments/{env}/revisions/{rev}");

envRev.MapGet("/preview", async (Pipelines p, string wb, string env, string rev, string guid, CancellationToken ct) =>
    await p.Default.GetPreviewAsync(wb, env, rev, guid, null, ct) is { } png
        ? Results.File(png, "image/png")
        : Results.NoContent());   // this asset type has no preview

envRev.MapPost("/imports", async (Pipelines p, string wb, string env, string rev, ImportRequest body, CancellationToken ct) =>
{
    var slot = await p.Default.RequestImportAsync(wb, env, rev, body.Address, null, ct);
    return new { address = body.Address, error = slot.Error, artifacts = slot.ArtifactNames(), manifest = slot.Manifest };
});

envRev.MapGet("/imports/artifact", async (Pipelines p, string wb, string env, string rev, string address, string name,
    CancellationToken ct) =>
    Bytes(await p.Default.GetImportArtifactAsync(wb, env, rev, address, name, null, ct), Path.GetFileName(name)));

// ── activity: every pipeline call the server made ───────────────────────────

api.MapGet("/activity", (Pipelines p, long? after) => p.ActivitySince(after ?? 0));

app.Run();

static object Service(ProjectServiceStatus s) => new
{
    s.Status, s.Message, s.IsReady, s.IsStarting, s.IsStopped, summary = s.Describe(),
    jobStatus = s.ProjectService?.JobStatus, steps = s.ProjectService?.Steps,
};

// Bytes back to the page, typed so it can show text and images inline.
static IResult Bytes(byte[] bytes, string fileName)
{
    if (bytes is [0x89, (byte)'P', (byte)'N', (byte)'G', ..]) return Results.File(bytes, "image/png");
    if (Text.Looks(bytes)) return Results.File(bytes, "text/plain; charset=utf-8");
    return Results.File(bytes, "application/octet-stream", fileName);
}

record NewWorkbench(string Branch, string? Repository);

record ActivityItem(long Id, DateTimeOffset At, string Method, string Path, int Status, int Ms, string? RequestId,
    string? ErrorCode, string? ErrorDetail, bool IsPoll, string? Exchange, string? Curl);
record NewEnvironment(string? Platform);
record Selection(string OrganizationId, string ProjectId);
record PastedToken(string? Token);
record ImportRequest(string Address);

sealed class NotConfiguredException(IEnumerable<string> missing)
    : Exception($"pipeline-onboard.config is missing {string.Join(", ", missing)}; onboard first (README steps 3–6)");

/// <summary>The config and one client per branch (the project service is addressed by branch).</summary>
sealed class Pipelines
{
    readonly IWebHostEnvironment host;
    readonly ConcurrentDictionary<string, PipelineClient> clients = new(StringComparer.Ordinal);
    readonly List<ActivityItem> activity = [];
    long lastActivity;
    PipelineConfig config;
    string? pastedToken;   // overrides the config file's token while set

    public Pipelines(IWebHostEnvironment host)
    {
        this.host = host;
        config = Load();
    }

    // The env var wins, then the .env next to pipeline.http, then the onboarding
    // scripts' config in pipeline-main, each looked for in the folders above the
    // working directory and the binary; then the client's own search.
    PipelineConfig Load()
    {
        var explicitPath = Environment.GetEnvironmentVariable("PIPELINE_ONBOARD_CONFIG") is { Length: > 0 } env ? env
            : FindUp(".env") ?? FindUp(Path.Combine("pipeline-main", "pipeline-onboard.config"));
        return PipelineConfig.Load(explicitPath);
    }

    string? FindUp(string relative)
    {
        foreach (var start in (string[])[host.ContentRootPath, AppContext.BaseDirectory])
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                if (Path.Combine(dir.FullName, relative) is var path && File.Exists(path))
                    return path;
        return null;
    }

    /// <summary>Re-read the config, e.g. after a fresh token was written to .env. A pasted token still wins.</summary>
    public void Reload()
    {
        config = Load();
        if (pastedToken is not null) config = config with { Token = pastedToken };
        DropClients();
    }

    /// <summary>Use a pasted token (null: back to the config file's). Org/project picks are kept.</summary>
    public void SetToken(string? token)
    {
        pastedToken = string.IsNullOrEmpty(token) ? null : token;
        config = config with { Token = pastedToken ?? Load().Token };
        DropClients();
    }

    /// <summary>Strip whitespace, quotes and a copied "Bearer " prefix.</summary>
    public static string CleanToken(string? value)
    {
        var t = (value ?? "").Trim().Trim('"', '\'').Trim();
        return t.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? t[7..].Trim() : t;
    }

    void DropClients()
    {
        foreach (var key in clients.Keys)
            if (clients.TryRemove(key, out var c)) c.Dispose();
    }

    /// <summary>Use another org/project. Saved workbench/environment ids belong to the old one, so they go.</summary>
    public void Select(string organizationId, string projectId)
    {
        if (organizationId == config.OrganizationId && projectId == config.ProjectId) return;
        config = config with { OrganizationId = organizationId, ProjectId = projectId, WorkbenchId = null, EnvironmentId = null };
        DropClients();
    }

    /// <summary>A client for org-level calls (org details, project list): needs only the token.</summary>
    public PipelineClient ForOrg(string organizationId)
    {
        if (config.Token.Length == 0) throw new NotConfiguredException(["UNITY_JWT"]);
        return clients.GetOrAdd("org:" + organizationId, _ =>
        {
            var c = new PipelineClient(config with { OrganizationId = organizationId });
            c.CallCompleted += Record;
            return c;
        });
    }

    public PipelineClient Default => For(null);

    public PipelineClient For(string? branch)
    {
        if (config.Missing() is { Count: > 0 } missing) throw new NotConfiguredException(missing);
        var b = string.IsNullOrWhiteSpace(branch) ? config.Branch : branch.Trim();
        return clients.GetOrAdd(b, _ =>
        {
            var c = new PipelineClient(config with { Branch = b });
            c.CallCompleted += Record;
            return c;
        });
    }

    void Record(ApiCall call)
    {
        lock (activity)
        {
            var text = call.Exchange?.Format(call.At, call.Duration);
            if (text is { Length: > 16_000 }) text = text[..16_000] + "\n… (trimmed)";
            activity.Add(new ActivityItem(++lastActivity, call.At, call.Method, call.Path, call.Status,
                (int)call.Duration.TotalMilliseconds, call.RequestId, call.ErrorCode, call.ErrorDetail,
                call.IsPoll, text, call.Exchange?.ToCurl()));
            if (activity.Count > 300) activity.RemoveRange(0, activity.Count - 300);
        }
    }

    public object ActivitySince(long after)
    {
        lock (activity)
            return new { last = lastActivity, items = activity.Where(a => a.Id > after).ToList() };
    }

    public object Describe()
    {
        var token = config.Token.Length > 0 ? TokenInfo.Inspect(config.Token) : null;
        return new
        {
            missing = config.Missing(),
            source = config.SourcePath,
            organizationId = config.OrganizationId,
            projectId = config.ProjectId,
            environment = config.Environment,
            branch = config.Branch,
            repository = config.RepositoryUrl,
            platform = config.Platform,
            workbenchId = config.WorkbenchId,
            environmentId = config.EnvironmentId,
            token = token is null ? null : new { token.Kind, token.ExpiresAt, token.IsExpired },
            tokenSource = pastedToken is not null ? "pasted" : token is null ? null : "file",
        };
    }
}

static class Git
{
    /// <summary>Branch name → latest commit on the remote. The repo is public, so this needs no credentials.</summary>
    public static async Task<IReadOnlyDictionary<string, string>> RemoteBranchesAsync(string repository, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in (string[])["ls-remote", "--heads", repository]) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("couldn't start git");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var stdout = proc.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = proc.StandardError.ReadToEndAsync(timeout.Token);
        try { await proc.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            proc.Kill(true);
            throw new InvalidOperationException("git ls-remote timed out");
        }
        if (proc.ExitCode != 0) throw new InvalidOperationException($"git ls-remote: {(await stderr).Trim()}");
        return (await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim().Split('\t'))
            .Where(parts => parts is [_, var r] && r.StartsWith("refs/heads/"))
            .ToDictionary(parts => parts[1]["refs/heads/".Length..], parts => parts[0], StringComparer.Ordinal);
    }
}

static class Text
{
    static readonly UTF8Encoding Strict = new(false, true);

    /// <summary>Text if the start has no NULs and decodes as UTF-8 (allowing a cut character at the end).</summary>
    public static bool Looks(byte[] bytes)
    {
        var head = bytes.AsSpan(0, Math.Min(bytes.Length, 4096));
        if (head.IndexOf((byte)0) >= 0) return false;
        var maxCut = bytes.Length > head.Length ? 3 : 0;
        for (var cut = 0; cut <= maxCut && cut < head.Length; cut++)
        {
            try { Strict.GetString(head[..^cut]); return true; }
            catch (DecoderFallbackException) { }
        }
        return head.Length == 0;
    }
}
