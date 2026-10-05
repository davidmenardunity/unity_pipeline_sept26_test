using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pipeline.Client;

/// <summary>
/// Settings the app needs to talk to the pipeline. Read from the onboarding
/// scripts' <c>pipeline-onboard.config</c> (bash syntax; only plain
/// <c>KEY="value"</c> lines are used), overridden by environment variables.
/// </summary>
public sealed record PipelineConfig(
    string Token,
    string OrganizationId,
    string ProjectId,
    string Environment,
    string Branch,
    string Region,
    string Platform,
    string? RepositoryUrl,
    string? WorkbenchId,
    string? EnvironmentId,
    string? SourcePath)
{
    // KEY="value" (bash config) or KEY=value (a .env file, as VS Code's REST Client reads it).
    static readonly Regex Assignment = new(@"^([A-Z_][A-Z0-9_]*)=(?:""([^""]*)""|([^\s""#]*))", RegexOptions.Multiline);

    static readonly string[] EnvironmentOverrides =
        ["UNITY_JWT", "ORG_ID", "PROJECT_ID", "PIPELINE_ENV", "BRANCH", "PIPELINE_REGION",
         "PIPELINE_PLATFORM", "GIT_REPO_URL", "WORKBENCH_ID", "ENVIRONMENT_ID"];

    /// <summary>Where to look for the config, first match wins.</summary>
    public static IEnumerable<string> CandidatePaths(string? explicitPath = null)
    {
        if (!string.IsNullOrEmpty(explicitPath)) yield return explicitPath;
        var fromEnv = System.Environment.GetEnvironmentVariable("PIPELINE_ONBOARD_CONFIG");
        if (!string.IsNullOrEmpty(fromEnv)) yield return fromEnv;
        yield return Path.Combine(Directory.GetCurrentDirectory(), "pipeline-onboard.config");
        // Running from the repo: asset-browser/src/PipelineBrowser.App/bin/... -> repo root.
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            yield return Path.Combine(dir.FullName, "pipeline-onboard.config");
    }

    public static PipelineConfig Load(string? explicitPath = null)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string? source = null;
        foreach (var candidate in CandidatePaths(explicitPath))
        {
            if (!File.Exists(candidate)) continue;
            source = Path.GetFullPath(candidate);
            var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
            foreach (Match m in Assignment.Matches(File.ReadAllText(candidate)))
                values[m.Groups[1].Value] = (m.Groups[2].Success ? m.Groups[2] : m.Groups[3]).Value.Replace("$HOME", home);
            break;
        }
        foreach (var key in EnvironmentOverrides)
        {
            var v = System.Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrEmpty(v)) values[key] = v;
        }

        string Get(string key, string fallback = "") =>
            values.TryGetValue(key, out var v) && v.Length > 0 ? v : fallback;
        string? Optional(string key) => values.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

        return new PipelineConfig(
            Token: Get("UNITY_JWT"),
            OrganizationId: Get("ORG_ID"),
            ProjectId: Get("PROJECT_ID"),
            Environment: Get("PIPELINE_ENV", "staging") is "production" or "prod" ? "production" : "staging",
            Branch: Get("BRANCH", "main"),
            Region: Get("PIPELINE_REGION", "America"),
            Platform: Get("PIPELINE_PLATFORM", "Windows64"),
            RepositoryUrl: Optional("GIT_REPO_URL"),
            WorkbenchId: Optional("WORKBENCH_ID"),
            EnvironmentId: Optional("ENVIRONMENT_ID"),
            SourcePath: source);
    }

    /// <summary>Missing required settings, for a friendly startup error.</summary>
    public IReadOnlyList<string> Missing()
    {
        var missing = new List<string>();
        if (Token.Length == 0) missing.Add("UNITY_JWT");
        if (OrganizationId.Length == 0) missing.Add("ORG_ID");
        if (ProjectId.Length == 0) missing.Add("PROJECT_ID");
        return missing;
    }
}

/// <summary>What the app can tell from the bearer token without calling anything.</summary>
public sealed record TokenInfo(string Kind, DateTimeOffset? ExpiresAt, string? GenesisId)
{
    public TimeSpan? Remaining => ExpiresAt - DateTimeOffset.UtcNow;
    public bool IsExpired => Remaining is { } r && r <= TimeSpan.Zero;

    public static TokenInfo Inspect(string token)
    {
        if (token.StartsWith("unity_sa_bt.", StringComparison.Ordinal))
            return new TokenInfo("service account", null, null);
        if (!token.StartsWith("eyJ", StringComparison.Ordinal))
            return new TokenInfo("unknown", null, null);
        try
        {
            var payload = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            var root = doc.RootElement;
            DateTimeOffset? exp = root.TryGetProperty("exp", out var e) && e.TryGetInt64(out var s)
                ? DateTimeOffset.FromUnixTimeSeconds(s) : null;
            var genesis = root.TryGetProperty("genesisId", out var g) ? g.GetString() : null;
            return new TokenInfo("user JWT", exp, genesis);
        }
        catch (Exception)
        {
            return new TokenInfo("user JWT (unreadable)", null, null);
        }
    }
}
