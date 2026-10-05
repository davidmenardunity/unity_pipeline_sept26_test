using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pipeline.Client;

/// <summary>
/// The full request and response of one call, for the activity panel's details
/// pane. The bearer token is redacted and bodies are trimmed, so it's safe to
/// paste into a bug report.
/// </summary>
public sealed record ApiExchange(
    string Method,
    string Url,
    IReadOnlyList<KeyValuePair<string, string>> RequestHeaders,
    string? RequestBody,
    int Status = 0,
    string? Reason = null,
    IReadOnlyList<KeyValuePair<string, string>>? ResponseHeaders = null,
    string? ResponseBody = null,
    string? TransportError = null,
    bool RequestBodyIsBinary = false)
{
    public const int MaxBodyChars = 64 * 1024;

    public bool HasResponse => ResponseHeaders is not null || TransportError is not null;

    /// <summary>The exchange as text: request line, headers and bodies.</summary>
    public string Format(DateTimeOffset? at = null, TimeSpan? duration = null) =>
        string.Concat(Parts(at, duration).Select(p => p.Text));

    /// <summary>
    /// The same text as <see cref="Format"/>, split into typed pieces so a view
    /// can colour it (methods, status, header names, JSON tokens…).
    /// </summary>
    public IEnumerable<ExchangePart> Parts(DateTimeOffset? at = null, TimeSpan? duration = null)
    {
        yield return new(Method, PartKind.Method);
        yield return new(" " + Url + "\n", PartKind.Url);
        if (TransportError is not null)
            yield return new("→ no response: " + TransportError + "\n", PartKind.StatusError);
        else if (ResponseHeaders is null)
            yield return new("→ waiting for the response…\n", PartKind.Pending);
        else
        {
            yield return new($"→ {Status}{(Reason is null ? "" : " " + Reason)}", Status switch
            {
                >= 200 and < 300 => PartKind.StatusOk,
                >= 500 => PartKind.StatusError,
                _ => PartKind.StatusWarn,
            });
            yield return new((duration is { } d ? $" · {d.TotalSeconds:0.00}s" : "")
                             + (at is { } t ? $" · {t:HH:mm:ss}" : "") + "\n", PartKind.Dim);
        }

        foreach (var p in Section("Request headers", HeaderParts(RequestHeaders))) yield return p;
        foreach (var p in Section("Request body", BodyParts(RequestBody, "(none)"))) yield return p;
        if (ResponseHeaders is null) yield break;
        foreach (var p in Section("Response headers", HeaderParts(ResponseHeaders))) yield return p;
        foreach (var p in Section("Response body", BodyParts(ResponseBody, "(empty)"))) yield return p;
    }

    static IEnumerable<ExchangePart> Section(string title, IEnumerable<ExchangePart> body)
    {
        yield return new($"\n## {title}\n", PartKind.Heading);
        foreach (var p in body) yield return p;
        yield return new("\n", PartKind.Plain);
    }

    static IEnumerable<ExchangePart> HeaderParts(IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        if (headers.Count == 0) { yield return new("(none)", PartKind.Dim); yield break; }
        for (var i = 0; i < headers.Count; i++)
        {
            var (name, value) = headers[i];
            yield return new(name + ": ", PartKind.HeaderName);
            yield return new(value, name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ? PartKind.Dim
                : name.Contains("request-id", StringComparison.OrdinalIgnoreCase) ? PartKind.Highlight
                : PartKind.HeaderValue);
            if (i < headers.Count - 1) yield return new("\n", PartKind.Plain);
        }
    }

    // Strings (a key when followed by ':'), numbers, and true/false/null.
    static readonly Regex JsonToken = new(
        @"(""(?:[^""\\]|\\.)*"")(\s*:)?|(-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)|\b(true|false|null)\b", RegexOptions.Compiled);

    static IEnumerable<ExchangePart> BodyParts(string? body, string none)
    {
        if (body is null) { yield return new(none, PartKind.Dim); yield break; }
        body = body.TrimEnd();
        if (body.StartsWith('(')) { yield return new(body, PartKind.Dim); yield break; }   // "(123 bytes, image/png)"
        if (body is not ['{' or '[', ..]) { yield return new(body, PartKind.Plain); yield break; }

        var last = 0;
        foreach (Match m in JsonToken.Matches(body))
        {
            if (m.Index > last) yield return new(body[last..m.Index], PartKind.Plain);
            if (m.Groups[1].Success)
            {
                yield return new(m.Groups[1].Value, m.Groups[2].Success ? PartKind.JsonKey : PartKind.JsonString);
                if (m.Groups[2].Success) yield return new(m.Groups[2].Value, PartKind.Plain);
            }
            else yield return new(m.Value, PartKind.JsonLiteral);
            last = m.Index + m.Length;
        }
        if (last < body.Length) yield return new(body[last..], PartKind.Plain);
    }

    /// <summary>
    /// A curl command that repeats the call. The token is left as
    /// <c>$UNITY_JWT</c>: <c>source pipeline-onboard.config</c> first.
    /// </summary>
    public string ToCurl()
    {
        var sb = new StringBuilder("curl -sS -i");
        if (Method != "GET") sb.Append(" -X ").Append(Method);
        sb.Append(' ').Append(Quote(Url));
        foreach (var (name, value) in RequestHeaders)
        {
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            sb.Append(" \\\n  -H ").Append(name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                ? "\"Authorization: Bearer $UNITY_JWT\"" : Quote($"{name}: {value}"));
        }
        if (RequestBodyIsBinary) sb.Append(" \\\n  --data-binary @FILE   # binary body, not captured");
        else if (RequestBody is not null) sb.Append(" \\\n  --data-raw ").Append(Quote(RequestBody));
        return sb.ToString();
    }

    static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    // ── capture ─────────────────────────────────────────────────────────────

    internal static List<KeyValuePair<string, string>> Collect(params HttpHeaders?[] sources)
    {
        var list = new List<KeyValuePair<string, string>>();
        foreach (var headers in sources)
            if (headers is not null)
                foreach (var (name, values) in headers)
                    list.Add(new(name, name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                        ? Redact(string.Join(", ", values)) : string.Join(", ", values)));
        return list;
    }

    /// <summary>"Bearer eyJhbGci…(redacted, 1234 chars)": enough to tell tokens apart, not to use one.</summary>
    internal static string Redact(string value)
    {
        var space = value.IndexOf(' ');
        var (scheme, secret) = space < 0 ? ("", value) : (value[..(space + 1)], value[(space + 1)..]);
        return $"{scheme}{secret[..Math.Min(8, secret.Length)]}…(redacted, {secret.Length} chars)";
    }

    /// <summary>A body as text: pretty JSON, trimmed text, or a size note for binary.</summary>
    public static string? DescribeBody(byte[]? bytes, string? contentType)
    {
        if (bytes is null || bytes.Length == 0) return null;
        if (!LooksLikeText(bytes, contentType))
            return $"({bytes.Length:N0} bytes{(contentType is null ? "" : ", " + contentType)})";

        var text = Encoding.UTF8.GetString(bytes);
        if (text.Length <= MaxBodyChars && text.TrimStart() is ['{' or '[', ..])
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                text = JsonSerializer.Serialize(doc.RootElement, Indented);
            }
            catch (JsonException) { /* not JSON after all: show as is */ }
        }
        return text.Length <= MaxBodyChars ? text
            : text[..MaxBodyChars] + $"\n… (trimmed; {bytes.Length:N0} bytes in all)";
    }

    static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static bool LooksLikeText(byte[] bytes, string? contentType)
    {
        if (contentType is not null && (contentType.StartsWith("text/") || contentType.Contains("json")
            || contentType.Contains("xml") || contentType.Contains("yaml") || contentType.Contains("html")))
            return true;
        if (contentType is not null && (contentType.StartsWith("image/") || contentType.Contains("octet-stream")))
            return false;
        // Unknown type: text if the start has no NULs and is valid UTF-8.
        var head = bytes.AsSpan(0, Math.Min(bytes.Length, 4096));
        if (head.IndexOf((byte)0) >= 0) return false;
        // The window may cut a multi-byte character in two: allow up to 3 dangling bytes.
        var maxCut = bytes.Length > head.Length ? 3 : 0;
        for (var cut = 0; cut <= maxCut && cut < head.Length; cut++)
        {
            try { StrictUtf8.GetString(head[..^cut]); return true; }
            catch (DecoderFallbackException) { }
        }
        return false;
    }

    static readonly UTF8Encoding StrictUtf8 = new(false, true);
}

public enum PartKind
{
    Plain, Dim, Method, Url, StatusOk, StatusWarn, StatusError, Pending,
    Heading, HeaderName, HeaderValue, Highlight, JsonKey, JsonString, JsonLiteral,
}

/// <summary>A run of text in <see cref="ApiExchange.Parts"/>.</summary>
public readonly record struct ExchangePart(string Text, PartKind Kind);
