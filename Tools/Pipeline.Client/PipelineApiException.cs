using System.Text.Json;

namespace Pipeline.Client;

/// <summary>
/// A failed pipeline call, with the server's problem+json fields kept verbatim
/// (the audience is project-service developers: show them what the server said).
/// </summary>
public sealed class PipelineApiException : Exception
{
    public int Status { get; }
    public string? Code { get; }
    public string? Detail { get; }
    public string? RequestId { get; }
    public string Method { get; }
    public string Path { get; }

    /// <summary>An HTML 403 from the network edge: the caller isn't on the VPN.</summary>
    public bool IsVpnBlock { get; }

    public PipelineApiException(string method, string path, int status, string? code, string? detail,
        string? requestId, bool isVpnBlock, Exception? inner = null)
        : base(Describe(method, path, status, code, detail, requestId, isVpnBlock), inner)
    {
        (Method, Path, Status, Code, Detail, RequestId, IsVpnBlock) =
            (method, path, status, code, detail, requestId, isVpnBlock);
    }

    /// <summary>
    /// What reads answer when the project service isn't running. Also seen from
    /// asset-tree while the service is up (F42), so confirm with the status route.
    /// </summary>
    public bool SuggestsServiceDown => Status == 503 && Code == "status_authority_unavailable";

    /// <summary>Text a developer can paste into a bug report.</summary>
    public string BugReport =>
        $"{Method} {Path}\nHTTP {Status}{(Code is null ? "" : $" {Code}")}\n{Detail}\nrequestId: {RequestId ?? "-"}\nat: {DateTimeOffset.UtcNow:u}";

    static string Describe(string method, string path, int status, string? code, string? detail,
        string? requestId, bool vpn)
    {
        if (vpn) return $"{method} {path}: HTTP 403 from the network edge. Are you on the VPN?";
        if (status == 0) return $"{method} {path}: no response ({detail})";
        var s = $"{method} {path}: HTTP {status}";
        if (code is not null) s += $" {code}";
        if (!string.IsNullOrEmpty(detail)) s += $": {detail}";
        if (requestId is not null) s += $" [requestId {requestId}]";
        return s + Hint(status, code);
    }

    static string Hint(int status, string? code) => (status, code) switch
    {
        (401, _) => " (token expired or for the other environment)",
        (404, "project_not_configured") => " (the org has no UVCS server location)",
        (503, "status_authority_unavailable") => " (project service not running?)",
        (501, "ps_capability_absent") => " (the backend refused this; the real upstream status is hidden, DVOPSWEB-7841)",
        _ => "",
    };

    /// <summary>Build from a response body (problem+json, HTML or anything else).</summary>
    public static PipelineApiException FromResponse(string method, string path, int status, byte[] body)
    {
        var text = body.Length == 0 ? "" : System.Text.Encoding.UTF8.GetString(body);
        if (status == 403 && text.TrimStart().StartsWith('<'))
            return new PipelineApiException(method, path, status, null, null, null, isVpnBlock: true);
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            string? Str(JsonElement e, string name) =>
                e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString() : null;
            var error = root.TryGetProperty("error", out var err) ? err : default;
            var code = Str(error, "code") ?? Str(root, "code")
                       ?? (root.TryGetProperty("code", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetRawText() : null);
            var detail = Str(error, "message") ?? Str(root, "detail") ?? Str(root, "title");
            return new PipelineApiException(method, path, status, code, detail, Str(root, "requestId"), false);
        }
        catch (JsonException)
        {
            return new PipelineApiException(method, path, status, null, text.Length > 300 ? text[..300] : text, null, false);
        }
    }
}
