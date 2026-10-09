using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Pipeline.Client;

// Pipeline Explorer: a small local web app for trying the Unity Pipeline APIs.
// The server holds the bearer token (read from pipeline-onboard.config) and
// calls the pipeline through the repo's typed client; the page only talks to
// this server, on 127.0.0.1.

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://127.0.0.1:5280");
builder.Services.AddSingleton<Pipelines>();
builder.Services.AddDataProtection().SetApplicationName("PipelineExplorer");
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
    // The page names the operation a request belongs to, so the pipeline calls it causes can be grouped under it.
    var op = ctx.Request.Headers["X-Op"].ToString();
    Operation.Current.Value = op.Length is > 0 and <= 40 ? op : null;
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

// The Scene Preview WebGL player, served from the Unity project's build folder (not copied into the
// repo), at /player. Unity compresses its build files (.br or .gz): serve them with the matching
// Content-Encoding and the type of the file inside, which is what the Unity loader expects.
var playerDir = WebGlPlayer.FindBuild(app.Environment.ContentRootPath);
// The level editor's runtime (LevelEditor_WebGL build profile), at /level-player.
var levelPlayerDir = WebGlPlayer.FindBuild(app.Environment.ContentRootPath, WebGlPlayer.LevelEditorFolder, "PIPELINE_EXPLORER_LEVEL_PLAYER");
// The scene viewer's runtime (SceneViewer_WebGL build profile), at /scene-player.
var scenePlayerDir = WebGlPlayer.FindBuild(app.Environment.ContentRootPath, WebGlPlayer.SceneViewerFolder, "PIPELINE_EXPLORER_SCENE_PLAYER");
foreach (var (dir, at) in new[] { (playerDir, "/player"), (levelPlayerDir, "/level-player"), (scenePlayerDir, "/scene-player") })
{
    if (dir is null) continue;
    var types = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
    foreach (var ext in (string[])[".br", ".gz", ".unityweb", ".data", ".symbols.json"]) types.Mappings[ext] = "application/octet-stream";
    types.Mappings[".wasm"] = "application/wasm";
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(dir),
        RequestPath = at,
        ContentTypeProvider = types,
        OnPrepareResponse = f =>
        {
            var headers = f.Context.Response.Headers;
            headers.CacheControl = "no-cache";
            var name = f.File.Name;
            var encoding = name.EndsWith(".br") ? "br" : name.EndsWith(".gz") ? "gzip" : null;
            if (encoding is null) return;
            headers.ContentEncoding = encoding;
            var inner = name[..name.LastIndexOf('.')];
            f.Context.Response.ContentType = inner.EndsWith(".js") ? "application/javascript"
                : inner.EndsWith(".wasm") ? "application/wasm" : "application/octet-stream";
        },
    });
}

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

// A second token, for Collaboration (annotations) only: it doesn't accept the pipeline's Genesis tokens.
// Kept like the first one (encrypted for this Windows user), and never sent back.
api.MapPost("/collab-token", async (Pipelines p, PastedToken body, CancellationToken ct) =>
{
    var token = Pipelines.CleanToken(body.Token);
    if (token.Length == 0) return Results.BadRequest(new { error = "paste a bearer token" });
    // A Genesis access token is opaque (not a JWT): take any single token of a plausible length.
    if (token.Length < 20 || token.Any(char.IsWhiteSpace))
        return Results.BadRequest(new { error = "that doesn't look like a token (one word, at least 20 characters)" });
    // An opaque Genesis token is exchanged for a JWT on each use: check now that it can be.
    if (!token.StartsWith("eyJ", StringComparison.Ordinal) && !await p.Default.CanExchangeGenesisAsync(token, ct))
        return Results.BadRequest(new { error = "Unity's token exchange refused it: check it's a current Genesis access token for staging" });
    p.SetCollaborationToken(token);
    return Results.Ok(p.Describe());
});

api.MapDelete("/collab-token", (Pipelines p) =>
{
    p.SetCollaborationToken(null);
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
        if (p.GitBranchOf(wb.WorkbenchId) is { Length: > 0 } b) known.Add(b);

    // The repo this project's workbenches track; GIT_REPO_URL if there are none yet.
    var repo = workbenches.OrderByDescending(w => w.CreatedAt).Select(w => w.UpstreamRepository)
        .FirstOrDefault(r => !string.IsNullOrEmpty(r)) ?? c.Config.RepositoryUrl;
    string? gitError = null;
    IReadOnlyDictionary<string, string> heads = new Dictionary<string, string>();
    if (repo is not null)
    {
        try
        {
            heads = await p.HeadsAsync(repo, ct);
            known.UnionWith(heads.Keys);
        }
        catch (Exception e) when (e is not OperationCanceledException) { gitError = e.Message; }
    }
    // heads: each branch's latest commit, so the page can tell a workbench made from an older one.
    return new { branches = known, heads, repository = repo, gitError };
});

// Each workbench with gitBranch: the git branch it was made from. Pipeline no longer says (a workbench's
// own branch, branchName, is always "main"), so it's the branch this app created it on, else the one
// branch whose latest commit it was made from, else null (unknown, or several branches share that commit).
api.MapGet("/workbenches", async (Pipelines p, CancellationToken ct) =>
{
    var c = p.Default;
    var list = await c.ListWorkbenchesAsync(ct);
    var repo = list.Select(w => w.UpstreamRepository).FirstOrDefault(r => !string.IsNullOrEmpty(r)) ?? c.Config.RepositoryUrl;
    IReadOnlyDictionary<string, string> heads = new Dictionary<string, string>();
    if (repo is not null)
    {
        try { heads = await p.HeadsAsync(repo, ct); }
        catch (Exception e) when (e is not OperationCanceledException) { /* known branches still come through */ }
    }
    return list.Select(w => p.WithGitBranch(w, heads));
});

api.MapPost("/workbenches", async (Pipelines p, NewWorkbench body, CancellationToken ct) =>
{
    var c = p.Default;
    var repo = string.IsNullOrWhiteSpace(body.Repository) ? c.Config.RepositoryUrl : body.Repository.Trim();
    if (repo is null) return Results.BadRequest(new { error = "no repository: set GIT_REPO_URL in the config or pass one" });
    if (string.IsNullOrWhiteSpace(body.Branch)) return Results.BadRequest(new { error = "branch is required" });
    // Needs the branch's project service running: the page starts it first and shows its progress.
    var branch = body.Branch.Trim();
    var name = string.IsNullOrWhiteSpace(body.Name) ? null : body.Name.Trim();
    // Only this request knows the git branch. Note it under the name first (the list matches it up if
    // the answer never comes), and finish even if the page goes away mid-request (a reload): the
    // workbench gets created either way, and its branch must be recorded.
    if (name is not null) p.RememberGitBranch(Pipelines.PendingKey(name), branch);
    if (body.VcsConnectionId is { Length: > 0 } vcs && !Guid.TryParse(vcs.Trim(), out _))
        return Results.BadRequest(new { error = "a VCS connection id is a UUID" });
    var created = await c.CreateWorkbenchAsync(repo, branch, name, CancellationToken.None, body.VcsConnectionId);
    p.RememberGitBranch(created.WorkbenchId, branch);
    return Results.Ok(p.WithGitBranch(created, new Dictionary<string, string>()));
});

api.MapDelete("/workbenches/{wb}", async (Pipelines p, string wb, CancellationToken ct) =>
{
    await p.Default.DeleteWorkbenchAsync(wb, ct);
    return Results.NoContent();
});

// The workbench plus where it stands: readiness says which revision reads can use.
api.MapGet("/workbenches/{wb}", async (Pipelines p, string wb, HttpRequest request, CancellationToken ct) =>
{
    var c = p.Default;
    // X-Poll: the page is only checking for new revisions; mark the calls as polls.
    using var _ = request.Headers["X-Poll"] == "1" ? c.AsPolling() : null;
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

// An asset by GUID (its path, hashes, size): resolves a reference to another asset.
rev.MapGet("/assets/{guid}", async (Pipelines p, string wb, string rev, string guid, CancellationToken ct) =>
    await p.Default.GetAssetAsync(wb, rev, guid, ct));

rev.MapGet("/meta", async (Pipelines p, string wb, string rev, string guid, CancellationToken ct) =>
    Bytes(await p.Default.GetMetaAsync(wb, rev, guid, null, ct), $"{guid}.meta"));

rev.MapGet("/file", async (Pipelines p, string wb, string rev, string path, CancellationToken ct) =>
    Bytes(await p.Default.GetFileAsync(wb, rev, path, null, ct), Path.GetFileName(path)));

// Add (or overwrite) files in a folder, from a multipart form ("files"). All of them go
// in one commit, so one new revision (with `message`, if given), which then validates; returns its id.
rev.MapPost("/files", async (Pipelines p, string wb, string rev, string folder, string? branch, string? message, HttpRequest request,
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
    var revision = await c.SaveFilesAsync(wb, rev, await WorkbenchBranchAsync(c, wb, branch, ct),
        files, string.IsNullOrWhiteSpace(message) ? null : message.Trim(), ct);
    return Results.Ok(new { revision, paths = files.Select(f => f.Path) });
});

// Save edited text files (e.g. a prefab whose values were changed) as one new revision, with a message.
rev.MapPost("/save", async (Pipelines p, string wb, string rev, SaveRequest body, CancellationToken ct) =>
{
    if (body.Files is not { Count: > 0 }) return Results.BadRequest(new { error = "no files to save" });
    if (body.Files.Any(f => string.IsNullOrWhiteSpace(f.Path) || f.Path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)))
        return Results.BadRequest(new { error = "every file needs a path, and .meta files are the pipeline's own" });
    var c = p.Default;
    var files = body.Files.Select(f => (f.Path.Trim('/'), new UTF8Encoding(false).GetBytes(f.Text ?? ""))).ToList();
    var message = string.IsNullOrWhiteSpace(body.Message) ? null : body.Message.Trim();
    var revision = await c.SaveFilesAsync(wb, rev, await WorkbenchBranchAsync(c, wb, body.Branch, ct), files, message, ct);
    return Results.Ok(new { revision, paths = files.Select(f => f.Item1) });
});

// Publish the workbench's changes to the git branch it was made from (POST …/publishes). Reads that branch's
// latest commit before and after, so the page can say what landed. Pushes under Pipeline's git token.
api.MapPost("/workbenches/{wb}/publish", async (Pipelines p, string wb, PublishRequest body, CancellationToken ct) =>
{
    var c = p.Default;
    var workbench = await c.GetWorkbenchAsync(wb, ct);
    var repo = workbench.UpstreamRepository ?? c.Config.RepositoryUrl;
    var branch = p.GitBranchOf(wb);
    string? Head(IReadOnlyDictionary<string, string> heads) => branch is not null && heads.TryGetValue(branch, out var h) ? h : null;
    IReadOnlyDictionary<string, string> before = new Dictionary<string, string>();
    if (repo is not null) try { before = await p.HeadsAsync(repo, ct, fresh: true); } catch (InvalidOperationException) { }
    var result = await c.PublishAsync(wb, string.IsNullOrWhiteSpace(body.Message) ? null : body.Message.Trim(), null, ct);
    IReadOnlyDictionary<string, string> after = before;
    if (repo is not null) try { after = await p.HeadsAsync(repo, ct, fresh: true); } catch (InvalidOperationException) { }
    // A new branch on the remote after a drafted publish is most likely the draft.
    var newBranches = after.Keys.Except(before.Keys).ToList();
    // The workbench revision that was published; empty when there was nothing new to publish.
    string? published = null;
    try
    {
        using var doc = System.Text.Json.JsonDocument.Parse(result.Body);
        if (doc.RootElement.TryGetProperty("publishedRevision", out var pr)) published = pr.ToString();
    }
    catch (System.Text.Json.JsonException) { }
    return Results.Ok(new
    {
        result.Outcome, result.Status, response = result.Body, gitBranch = branch, publishedRevision = published,
        headBefore = Head(before), headAfter = Head(after), newBranches,
        upstreamRevision = workbench.UpstreamRevision,
    });
});

// Bring the workbench up to date with its git branch (PATCH …/workbenches/{wb} {type: "sync"}). Says which commit
// it was made from before and after, and where the branch is, so the page can tell whether anything came in.
api.MapPost("/workbenches/{wb}/sync", async (Pipelines p, string wb, CancellationToken ct) =>
{
    var c = p.Default;
    var before = await c.GetWorkbenchAsync(wb, ct);
    var (status, body) = await c.SyncWorkbenchAsync(wb, null, ct);
    var after = await c.GetWorkbenchAsync(wb, ct);
    var branch = p.GitBranchOf(wb);
    string? head = null;
    if (branch is not null && (after.UpstreamRepository ?? c.Config.RepositoryUrl) is { } repo)
        try { head = (await p.HeadsAsync(repo, ct, fresh: true)).GetValueOrDefault(branch); } catch (InvalidOperationException) { }
    return Results.Ok(new
    {
        status, response = body, gitBranch = branch, head,
        commitBefore = before.UpstreamRevision, commitAfter = after.UpstreamRevision,
        validation = after.Validation?.Status,
    });
});

// ── environment reads: previews and imports ─────────────────────────────────

var envRev = api.MapGroup("/workbenches/{wb}/environments/{env}/revisions/{rev}");

envRev.MapGet("/preview", async (Pipelines p, string wb, string env, string rev, string guid, CancellationToken ct) =>
    await p.Default.GetPreviewAsync(wb, env, rev, guid, null, ct) is { } png
        ? Results.File(png, "image/png")
        : Results.NoContent());   // this asset type has no preview

// ── the embedded Scene Preview player ───────────────────────────────────────

// Which WebGL build files the player page should load (names vary with the build's compression).
api.MapGet("/player", () => WebGlPlayer.Describe(playerDir));
api.MapGet("/level-player", () => WebGlPlayer.Describe(levelPlayerDir, "level-player", WebGlPlayer.LevelEditorFolder, "LevelEditor_WebGL"));
api.MapGet("/scene-player", () => WebGlPlayer.Describe(scenePlayerDir, "scene-player", WebGlPlayer.SceneViewerFolder, "SceneViewer_WebGL"));

// One call for "preview this asset in the player": a content archive (.ca) built by the pipeline for
// the player's platform, and the URL the player downloads it from. Finds or creates the workbench's
// environment for that platform, then runs the importer on the asset. The first import for an asset
// (or in a new environment) can take minutes: the request is re-sent while the pipeline is still
// producing it, up to 15 minutes.
var readyToRetry = new HashSet<int> { 0, 409, 502, 503, 504 };
rev.MapPost("/player-archive", async (Pipelines p, string wb, string rev, PlayerArchiveRequest body, CancellationToken ct) =>
{
    var c = p.Default;
    var platform = string.IsNullOrWhiteSpace(body.Platform) ? "WebGL" : body.Platform.Trim();
    var importer = string.IsNullOrWhiteSpace(body.Importer) ? WebGlPlayer.ContentImporter : body.Importer.Trim();
    var steps = new List<string>();

    var env = (await c.ListEnvironmentsAsync(wb, ct))
        .FirstOrDefault(e => string.Equals(e.Platform, platform, StringComparison.OrdinalIgnoreCase));
    if (env is null)
    {
        env = await c.CreateEnvironmentAsync(wb, platform, ct);
        steps.Add($"created a {platform} environment, {env.EnvironmentId}");
    }
    else steps.Add($"using the {platform} environment {env.EnvironmentId}");

    var address = $"T:{body.Guid}+{importer}";
    var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(15);
    ImportSlot slot;
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            slot = await c.RequestImportAsync(wb, env.EnvironmentId, rev, address, null, ct);
            steps.Add($"import {address}: done (attempt {attempt})");
            break;
        }
        catch (PipelineApiException e) when (readyToRetry.Contains(e.Status) && DateTime.UtcNow < deadline)
        {
            steps.Add($"import attempt {attempt}: {(e.Status == 0 ? "still producing (timed out)" : $"HTTP {e.Status} {e.Code}")}; re-sending");
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }

    if (!string.IsNullOrEmpty(slot.Error?.Code))
        return Results.Json(new { error = $"{slot.Error.Code}: {slot.Error.Message}", steps }, statusCode: 422);
    var names = slot.ArtifactNames();
    var artifact = names.FirstOrDefault(n => n.EndsWith(".ca", StringComparison.OrdinalIgnoreCase));
    if (artifact is null)
        return Results.Json(new { error = $"the import produced no .ca (outputs: {string.Join(", ", names.Select(n => n.Length == 0 ? "(main)" : n))})", steps }, statusCode: 422);

    var url = $"/api/workbenches/{wb}/environments/{env.EnvironmentId}/revisions/{Uri.EscapeDataString(rev)}/imports/artifact" +
              $"?address={Uri.EscapeDataString(address)}&name={Uri.EscapeDataString(artifact)}";
    // contentHash covers everything in the archive (what the asset uses too), so two archives can be compared.
    return Results.Ok(new { environmentId = env.EnvironmentId, platform, address, artifact, url, steps, contentHash = slot.ContentHashOf(artifact) });
});

envRev.MapPost("/imports", async (Pipelines p, string wb, string env, string rev, ImportRequest body, CancellationToken ct) =>
{
    var slot = await p.Default.RequestImportAsync(wb, env, rev, body.Address, null, ct);
    return new { address = body.Address, error = slot.Error, artifacts = slot.ArtifactNames(), manifest = slot.Manifest };
});

// name may be empty: the main import result's artifact has no name.
envRev.MapGet("/imports/artifact", async (Pipelines p, string wb, string env, string rev, string address, string? name,
    CancellationToken ct) =>
    Bytes(await p.Default.GetImportArtifactAsync(wb, env, rev, address, name ?? "", null, ct),
        string.IsNullOrEmpty(name) ? $"{address.Replace(':', '_').Replace('+', '_')}.bin" : Path.GetFileName(name)));

// ── VCS connections (what lets Pipeline push to git) ────────────────────────

api.MapGet("/vcs-connections", async (Pipelines p, CancellationToken ct) =>
    Results.Bytes(await p.Default.ListVcsConnectionsJsonAsync(ct), "application/json"));

// A git connection for the repo, with a token that can push (typed into the page by its owner, passed on to
// Build Automation and never stored or logged here).
api.MapPost("/vcs-connections", async (Pipelines p, NewVcsConnection body, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(body.Url)) return Results.BadRequest(new { error = "a connection needs the repository URL" });
    var anonymous = string.IsNullOrWhiteSpace(body.User) && string.IsNullOrWhiteSpace(body.Token);
    if (!anonymous && (string.IsNullOrWhiteSpace(body.User) || string.IsNullOrWhiteSpace(body.Token)))
        return Results.BadRequest(new { error = "give both a user name and a token, or neither (a read-only connection)" });
    var name = string.IsNullOrWhiteSpace(body.Name) ? body.Url.Trim().TrimEnd('/').Split('/').Last().Replace(".git", "") : body.Name.Trim();
    try
    {
        // Build Automation's examples are clone URLs (…/repo.git); git takes both forms.
        var url = body.Url.Trim().TrimEnd('/');
        if (url.Contains("github.com/", StringComparison.OrdinalIgnoreCase) && !url.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) url += ".git";
        var json = await p.Default.CreateVcsConnectionAsync(name, url, anonymous ? null : body.User!.Trim(), anonymous ? null : body.Token!.Trim(), ct);
        return Results.Bytes(json, "application/json");
    }
    catch (PipelineApiException e)
    {
        // Build Automation's answer only (never the request: it holds the token).
        app.Logger.LogWarning("Creating the VCS connection for {Url} failed: HTTP {Status} {Code} {Detail}", body.Url, e.Status, e.Code, e.Detail ?? e.Message);
        throw;
    }
});

api.MapPost("/vcs-connections/{id}/validate", async (Pipelines p, string id, CancellationToken ct) =>
    await p.Default.ValidateVcsConnectionAsync(id, ct) is { } problem ? Results.Ok(new { ok = false, problem }) : Results.Ok(new { ok = true }));

api.MapDelete("/vcs-connections/{id}", async (Pipelines p, string id, CancellationToken ct) =>
{
    await p.Default.DeleteVcsConnectionAsync(id, ct);
    return Results.NoContent();
});

// ── collaboration: annotations on assets ────────────────────────────────────
// A pass-through to Unity Cloud Collaboration (/collaboration/v1/…), with this server's token. The
// Scene Preview player's Collaboration SDK and the page both call it here: the player has no token
// of its own, and the service doesn't allow calls from this origin (CORS).

api.MapMethods("/collab/{**path}", ["GET", "POST", "PUT", "PATCH", "DELETE"], async (Pipelines p, string path,
    HttpRequest request, HttpResponse response, CancellationToken ct) =>
{
    byte[]? body = null;
    if (request.ContentLength is > 0 || request.Headers.TransferEncoding.Count > 0)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, ct);
        body = buffer.ToArray();
    }
    // Players built before the switch target comments at assets/projects/…, which the Collaboration dashboard
    // doesn't list: send them to unity/project/… like the rest.
    if (body is { Length: > 0 })
    {
        var text = Encoding.UTF8.GetString(body);
        if (text.Contains("\"assets/projects/", StringComparison.Ordinal))
            body = Encoding.UTF8.GetBytes(text.Replace("\"assets/projects/", "\"unity/project/"));
    }
    // The SDK's URLs carry the API prefix too (…/api/collab/collaboration/v1/projects/…).
    var rest = path.StartsWith("collaboration/v1/", StringComparison.Ordinal) ? path["collaboration/v1/".Length..] : path;
    var (status, bytes) = await p.Default.CollaborationAsync(new HttpMethod(request.Method), rest + request.QueryString,
        body, request.ContentType, ct);
    response.StatusCode = status;
    if (bytes.Length > 0) response.ContentType = "application/json";
    await response.Body.WriteAsync(bytes, ct);
});

// Uploads to a pre-signed storage URL Collaboration handed out (a comment's screenshot): the player can't
// PUT there itself (the storage doesn't allow this origin). Only HTTPS cloud-storage hosts.
api.MapPut("/collab-upload", async (string url, HttpRequest request, CancellationToken ct) =>
{
    if (!Uri.TryCreate(url, UriKind.Absolute, out var target) || target.Scheme != "https"
        || !(target.Host.EndsWith(".googleapis.com") || target.Host.EndsWith(".amazonaws.com") || target.Host.EndsWith(".blob.core.windows.net")
             || target.Host.EndsWith(".unity.com") || target.Host.EndsWith(".unity3d.com")))
        return Results.BadRequest(new { error = $"not a storage URL: {target?.Host}" });
    using var buffer = new MemoryStream();
    await request.Body.CopyToAsync(buffer, ct);
    using var content = new ByteArrayContent(buffer.ToArray());
    content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(request.ContentType ?? "application/octet-stream");
    using var http = new HttpClient();
    using var put = new HttpRequestMessage(HttpMethod.Put, target) { Content = content };
    put.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");   // Azure Blob storage requires it (others ignore it)
    using var response = await http.SendAsync(put, ct);
    var text = await response.Content.ReadAsStringAsync(ct);
    return response.IsSuccessStatusCode ? Results.NoContent()
        : Results.Json(new { error = $"storage answered HTTP {(int)response.StatusCode}: {text[..Math.Min(text.Length, 500)]}" }, statusCode: 502);
});

// The org's members, for @mentions (cached for 10 min).
api.MapGet("/members", async (Pipelines p, CancellationToken ct) =>
    Results.Bytes(await p.MembersAsync(ct), "application/json"));

// ── activity: every pipeline call the server made ───────────────────────────

api.MapGet("/activity", (Pipelines p, long? after) => p.ActivitySince(after ?? 0));

app.Run();

// The branch a change is committed to inside the workbench: its own (Pipeline names it "main"), not the
// git branch it was made from. `given` overrides it.
static async Task<string> WorkbenchBranchAsync(PipelineClient c, string wb, string? given, CancellationToken ct)
{
    if (!string.IsNullOrWhiteSpace(given)) return given.Trim();
    try { return (await c.GetWorkbenchAsync(wb, ct)).BranchName ?? "main"; }
    catch (PipelineApiException) { return "main"; }
}

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

record NewVcsConnection(string? Name, string? Url, string? User, string? Token);
record NewWorkbench(string Branch, string? Repository, string? Name, string? VcsConnectionId = null);

record ActivityItem(long Id, DateTimeOffset At, string Method, string Path, int Status, int Ms, string? RequestId,
    string? ErrorCode, string? ErrorDetail, bool IsPoll, string? Exchange, string? Curl, string? Op);
record SaveRequest(List<SavedFile>? Files, string? Message, string? Branch);
record PublishRequest(string? Message);
record SavedFile(string Path, string? Text);

/// <summary>The page operation the current request belongs to (its X-Op header), for grouping activity.</summary>
static class Operation
{
    public static readonly AsyncLocal<string?> Current = new();
}
record NewEnvironment(string? Platform);
record Selection(string OrganizationId, string ProjectId);
record PastedToken(string? Token);
record ImportRequest(string Address);
record PlayerArchiveRequest(string Guid, string? Platform, string? Importer);

/// <summary>Finding and describing the Scene Preview WebGL build the app embeds.</summary>
static class WebGlPlayer
{
    public const string ContentImporter = "Unity.Pipeline.Samples.ScenePreview.Importer.PreviewContentImporter";
    const string BuildFolder = "ScenePreview_WebGL";
    public const string LevelEditorFolder = "LevelEditor_WebGL";
    public const string SceneViewerFolder = "SceneViewer_WebGL";

    /// <summary>
    /// The build folder: PIPELINE_EXPLORER_PLAYER, else Builds/ScenePreview_WebGL in the Unity project the
    /// app sits in (Tools/ is next to Builds/).
    /// </summary>
    public static string? FindBuild(string contentRoot, string folder = BuildFolder, string variable = "PIPELINE_EXPLORER_PLAYER")
    {
        if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } configured)
            return Directory.Exists(configured) ? Path.GetFullPath(configured) : null;
        foreach (var start in (string[])[contentRoot, AppContext.BaseDirectory])
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                if (Path.Combine(dir.FullName, "Builds", folder) is var path && Directory.Exists(Path.Combine(path, "Build")))
                    return path;
        return null;
    }

    public static object Describe(string? dir, string at = "player", string folder = BuildFolder, string profile = "ScenePreview_WebGL")
    {
        if (dir is null)
            return new { available = false, message = $"No WebGL player build yet: build the {profile} profile to Builds/{folder} in the Unity project, then restart the app." };
        // Newest first: a build switched between compressed and uncompressed leaves the old files behind.
        var files = new DirectoryInfo(Path.Combine(dir, "Build")).GetFiles().OrderByDescending(f => f.LastWriteTimeUtc).Select(f => f.Name).ToList();
        string? Find(string infix) => files.FirstOrDefault(f => f.Contains(infix, StringComparison.Ordinal));
        var loader = Find(".loader.js");
        var data = Find(".data");
        var framework = Find(".framework.js");
        var code = Find(".wasm");
        if (loader is null || data is null || framework is null || code is null)
            return new { available = false, message = $"The build in {dir} is incomplete (found: {string.Join(", ", files)})." };
        return new
        {
            available = true,
            folder = dir,
            loaderUrl = $"{at}/Build/{loader}",
            dataUrl = $"{at}/Build/{data}",
            frameworkUrl = $"{at}/Build/{framework}",
            codeUrl = $"{at}/Build/{code}",
            streamingAssetsUrl = $"{at}/StreamingAssets",
        };
    }
}

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
    readonly IDataProtector protector;
    readonly ILogger<Pipelines> log;

    // A pasted token is kept between runs, encrypted with ASP.NET Core data protection (its keys
    // are protected by Windows for the current user), outside the repo.
    static readonly string SavedTokenPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PipelineExplorer", "token.dat");
    // The git branch each workbench this app created was made from (Pipeline only says so when creating).
    static readonly string GitBranchesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PipelineExplorer", "workbench-branches.json");
    Dictionary<string, string>? gitBranches;
    (string Repo, DateTime At, IReadOnlyDictionary<string, string> Heads)? headsCache;

    /// <summary>Where a workbench being created is noted, by name, until its id is known.</summary>
    public static string PendingKey(string name) => "name:" + name;

    public string? GitBranchOf(string workbenchId)
    {
        lock (GitBranchesPath)
        {
            gitBranches ??= ReadJson<Dictionary<string, string>>(GitBranchesPath) ?? [];
            return gitBranches.GetValueOrDefault(workbenchId);
        }
    }

    public void RememberGitBranch(string workbenchId, string branch)
    {
        lock (GitBranchesPath)
        {
            gitBranches ??= ReadJson<Dictionary<string, string>>(GitBranchesPath) ?? [];
            gitBranches[workbenchId] = branch;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(GitBranchesPath)!);
                File.WriteAllText(GitBranchesPath, System.Text.Json.JsonSerializer.Serialize(gitBranches));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log.LogWarning("Couldn't save the workbench's branch ({Message}).", e.Message);
            }
        }
    }

    T? ReadJson<T>(string path) where T : class
    {
        try { return File.Exists(path) ? System.Text.Json.JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            log.LogWarning("Couldn't read {Path} ({Message}).", path, e.Message);
            return null;
        }
    }

    /// <summary>Branch → latest commit (git ls-remote), cached for 20 s.</summary>
    public async Task<IReadOnlyDictionary<string, string>> HeadsAsync(string repo, CancellationToken ct, bool fresh = false)
    {
        if (!fresh && headsCache is { } h && h.Repo == repo && DateTime.UtcNow - h.At < TimeSpan.FromSeconds(20)) return h.Heads;
        var heads = await Git.RemoteBranchesAsync(repo, ct);
        headsCache = (repo, DateTime.UtcNow, heads);
        return heads;
    }

    /// <summary>The workbench as JSON, plus gitBranch: the branch it was made from, or null when that can't be told.</summary>
    public System.Text.Json.Nodes.JsonObject WithGitBranch(Workbench w, IReadOnlyDictionary<string, string> heads)
    {
        var node = System.Text.Json.JsonSerializer.SerializeToNode(w, System.Text.Json.JsonSerializerOptions.Web)!.AsObject();
        var known = GitBranchOf(w.WorkbenchId);
        // Created from this app, but its answer was lost: the branch was noted under its name.
        if (known is null && w.Name is { Length: > 0 } name && GitBranchOf(PendingKey(name)) is { } pending)
            RememberGitBranch(w.WorkbenchId, known = pending);
        var fromHead = heads.Where(kv => kv.Value == w.UpstreamRevision).Select(kv => kv.Key).ToList();
        node["gitBranch"] = known ?? (fromHead.Count == 1 ? fromHead[0] : null);
        node["gitBranchKnown"] = known is not null;
        // Several branches at that commit: the page can say which, instead of guessing.
        if (known is null && fromHead.Count > 1) node["gitBranchCandidates"] = new System.Text.Json.Nodes.JsonArray(fromHead.Select(b => (System.Text.Json.Nodes.JsonNode?)b).ToArray());
        return node;
    }

    // The last org/project picked in the page, so other clients (the Blender add-on) see it after a restart.
    static readonly string SavedSelectionPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PipelineExplorer", "selection.json");

    public Pipelines(IWebHostEnvironment host, IDataProtectionProvider dataProtection, ILogger<Pipelines> log)
    {
        this.host = host;
        this.log = log;
        protector = dataProtection.CreateProtector("PipelineExplorer.BearerToken");
        config = Load();
        pastedToken = ReadSavedToken();
        if (pastedToken is not null) config = config with { Token = pastedToken };
        collaborationToken = ReadSavedToken(SavedCollaborationTokenPath);
        config = config with { CollaborationToken = collaborationToken };
        if (ReadSavedSelection() is { } saved && (string.IsNullOrEmpty(config.OrganizationId) || string.IsNullOrEmpty(config.ProjectId)))
            config = config with { OrganizationId = saved.OrganizationId, ProjectId = saved.ProjectId };
    }

    string? collaborationToken;
    static readonly string SavedCollaborationTokenPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PipelineExplorer", "collab-token.dat");

    public void SetCollaborationToken(string? token)
    {
        collaborationToken = string.IsNullOrEmpty(token) ? null : token;
        SaveToken(collaborationToken, SavedCollaborationTokenPath);
        config = config with { CollaborationToken = collaborationToken };
        DropClients();
    }

    Selection? ReadSavedSelection()
    {
        try
        {
            return File.Exists(SavedSelectionPath)
                ? System.Text.Json.JsonSerializer.Deserialize<Selection>(File.ReadAllText(SavedSelectionPath)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            log.LogWarning("Couldn't read the saved org/project ({Message}).", e.Message);
            return null;
        }
    }

    void SaveSelection(Selection selection)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SavedSelectionPath)!);
            File.WriteAllText(SavedSelectionPath, System.Text.Json.JsonSerializer.Serialize(selection));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.LogWarning("Couldn't save the org/project pick ({Message}).", e.Message);
        }
    }

    string? ReadSavedToken(string? path = null)
    {
        path ??= SavedTokenPath;
        try
        {
            return File.Exists(path) ? protector.Unprotect(File.ReadAllText(path)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            log.LogWarning("Couldn't read the saved bearer token ({Message}); paste it again.", e.Message);
            return null;
        }
    }

    void SaveToken(string? token, string? path = null)
    {
        path ??= SavedTokenPath;
        try
        {
            if (token is null)
            {
                File.Delete(path);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, protector.Protect(token));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.LogWarning("Couldn't save the bearer token ({Message}); it's used until the app stops.", e.Message);
        }
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
        config = Load() with { CollaborationToken = collaborationToken };
        if (pastedToken is not null) config = config with { Token = pastedToken };
        if (ReadSavedSelection() is { } saved && (string.IsNullOrEmpty(config.OrganizationId) || string.IsNullOrEmpty(config.ProjectId)))
            config = config with { OrganizationId = saved.OrganizationId, ProjectId = saved.ProjectId };
        DropClients();
    }

    /// <summary>
    /// Use a pasted token, and keep it for the next runs (null: forget it and go back to the config
    /// file's). Org/project picks are kept.
    /// </summary>
    public void SetToken(string? token)
    {
        pastedToken = string.IsNullOrEmpty(token) ? null : token;
        SaveToken(pastedToken);
        config = config with { Token = pastedToken ?? Load().Token };   // CollaborationToken is kept
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
        SaveSelection(new Selection(organizationId, projectId));
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

    (string Org, DateTime At, byte[] Json)? membersCache;

    public async Task<byte[]> MembersAsync(CancellationToken ct)
    {
        if (membersCache is { } c && c.Org == config.OrganizationId && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(10))
            return c.Json;
        var json = await ForOrg(config.OrganizationId).GetMembersJsonAsync(ct);
        membersCache = (config.OrganizationId, DateTime.UtcNow, json);
        return json;
    }

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
                call.IsPoll, text, call.Exchange?.ToCurl(), Operation.Current.Value));
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
            token = token is null ? null : new { token.Kind, token.ExpiresAt, token.IsExpired, token.Issuer, token.Audience },
            tokenSource = pastedToken is not null ? "pasted" : token is null ? null : "file",
            collaborationToken = collaborationToken is null ? null
                : TokenInfo.Inspect(collaborationToken) is var c ? new { c.Kind, c.ExpiresAt, c.IsExpired, c.Issuer } : null,
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
