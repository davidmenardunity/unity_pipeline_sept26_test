using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CoAuthoring.Commands;

/// <summary>
/// Reads commands the way <c>unity command</c> does: a line (<c>"create_gameobject --name Box --primitive cube"</c>),
/// split tokens (<c>argv</c>), or a structured <c>{command, params}</c>. The first token names the command; the rest
/// bind as <c>--name value</c>, <c>--name=value</c>, a bare <c>--flag</c> for booleans, or positionally to the
/// required parameters in order. JSON values (<c>[1,2,3]</c>, <c>{"path":"…"}</c>) are taken wherever a parameter
/// takes a structure. A leading "unity command" (or "cmd") is skipped, so a line copied from a terminal works.
/// </summary>
public static class CommandLine
{
    public static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        var inToken = false;
        var quote = '\0';
        var depth = 0;   // inside a JSON value ({…} or […]): spaces and quotes belong to the token
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote != '\0')
            {
                if (c == quote) { quote = '\0'; if (depth > 0) sb.Append(c); continue; }
                if (c == '\\' && quote == '"' && i + 1 < line.Length && line[i + 1] is '"' or '\\') { if (depth > 0) sb.Append(c); sb.Append(line[++i]); continue; }
                sb.Append(c);
                continue;
            }
            if (depth > 0)
            {
                if (c is '{' or '[') depth++;
                else if (c is '}' or ']') depth--;
                else if (c == '"') { quote = '"'; sb.Append(c); continue; }
                sb.Append(c);
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                if (inToken) { tokens.Add(sb.ToString()); sb.Clear(); inToken = false; }
                continue;
            }
            inToken = true;
            if (c is '"' or '\'') { quote = c; continue; }
            if ((c is '{' or '[') && (sb.Length == 0 || sb.ToString().EndsWith('='))) { depth = 1; sb.Append(c); continue; }
            sb.Append(c);
        }
        if (quote != '\0') throw CommandException.Bad($"unterminated {quote} quote in: {line}");
        if (depth > 0) throw CommandException.Bad($"unterminated JSON value in: {line}");
        if (inToken) tokens.Add(sb.ToString());
        if (tokens.Count >= 2 && tokens[0] == "unity" && tokens[1] is "command" or "cmd") tokens.RemoveRange(0, 2);
        return tokens;
    }

    /// <summary>The command the first token names.</summary>
    public static CommandDef Resolve(Catalog catalog, IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0) throw CommandException.Bad("empty command; GET …/commands lists what is available");
        var name = tokens[0];
        if (catalog.Find(name) is { } def) return def;
        var near = catalog.All.Where(c => c.Name.Contains(name, StringComparison.OrdinalIgnoreCase)
            || name.Split('_').Any(part => part.Length > 2 && c.Name.Contains(part, StringComparison.OrdinalIgnoreCase))).Take(8).Select(c => c.Name).ToList();
        throw CommandException.NotFound($"Command Not Found: {name}. " + (near.Count > 0 ? $"Did you mean: {string.Join(", ", near)}?" : "GET …/commands lists the commands."));
    }

    /// <summary>Binds argument tokens to a command's parameters.</summary>
    public static JsonObject Bind(CommandDef def, IReadOnlyList<string> args)
    {
        var bound = new JsonObject();
        var positional = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal) || a.Length == 2) { positional.Add(a); continue; }
            var name = a[2..];
            string? value = null;
            if (name.IndexOf('=') is var eq and > 0) { value = name[(eq + 1)..]; name = name[..eq]; }
            name = name.Replace('-', '_');
            var p = def.Param(name) ?? throw UnknownParam(def, name);
            if (value is null)
            {
                if (p.Kind == ParamKind.Bool && (i + 1 >= args.Count || !IsBoolText(args[i + 1]))) { bound[name] = true; continue; }
                if (i + 1 >= args.Count) throw CommandException.Bad($"--{name} needs a value");
                value = args[++i];
            }
            bound[name] = Parse(p, value);
        }
        var free = def.Params.Where(p => !bound.ContainsKey(p.Name)).OrderByDescending(p => p.Required).ToList();
        if (positional.Count > free.Count)
            throw CommandException.Bad($"too many arguments for '{def.Name}': {string.Join(' ', positional)}. {Usage(def)}");
        for (var i = 0; i < positional.Count; i++) bound[free[i].Name] = Parse(free[i], positional[i]);
        return Normalize(def, bound);
    }

    /// <summary>Checks structured params: known names, required ones present, defaults filled. "$0.id" references pass as they are.</summary>
    public static JsonObject Normalize(CommandDef def, JsonObject given, bool requireAll = true)
    {
        var result = new JsonObject();
        foreach (var (key, value) in given)
        {
            var name = key.Replace('-', '_');
            var p = def.Param(name) ?? throw UnknownParam(def, name);
            result[name] = value is JsonValue v && v.TryGetValue<string>(out var s) && !s.StartsWith('$') && p.Kind is not ParamKind.String and not ParamKind.Handle
                ? Parse(p, s)
                : value?.DeepClone();
        }
        foreach (var p in def.Params)
        {
            if (result.ContainsKey(p.Name)) continue;
            if (p.Required && requireAll) throw CommandException.Bad($"'{def.Name}' needs --{p.Name}: {p.Description} {Usage(def)}");
            if (p.Default is not null) result[p.Name] = p.Default.DeepClone();
        }
        return JsonNode.Parse(result.ToJsonString())!.AsObject();
    }

    public static string Usage(CommandDef def) =>
        "Usage: " + def.Name + string.Concat(def.Params.Select(p => p.Required ? $" --{p.Name} <{p.Type}>" : $" [--{p.Name} <{p.Type}>]"));

    static CommandException UnknownParam(CommandDef def, string name) => CommandException.Bad($"'{def.Name}' has no parameter --{name}. {Usage(def)}");

    static bool IsBoolText(string s) => s is "true" or "false" or "True" or "False";

    static JsonNode? Parse(ParamDef p, string text)
    {
        switch (p.Kind)
        {
            case ParamKind.Bool:
                return bool.TryParse(text, out var b) ? b : throw CommandException.Bad($"--{p.Name} must be true or false, not '{text}'");
            case ParamKind.Int:
                return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : throw CommandException.Bad($"--{p.Name} must be an integer, not '{text}'");
            case ParamKind.Number:
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : throw CommandException.Bad($"--{p.Name} must be a number, not '{text}'");
            case ParamKind.Json:
                // A JToken parameter also takes plain text (a string or number value).
                if (p.Type == "JToken" && !(text.TrimStart().StartsWith('{') || text.TrimStart().StartsWith('[') || text.TrimStart().StartsWith('"')))
                    return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : bool.TryParse(text, out var tb) ? tb : JsonValue.Create(text);
                return ParseJson(p, text);
            case ParamKind.Handle:
                return text.TrimStart().StartsWith('{') ? ParseJson(p, text) : JsonValue.Create(text);
            default:
                return text;
        }
    }

    static JsonNode? ParseJson(ParamDef p, string text)
    {
        try { return JsonNode.Parse(text); }
        catch (JsonException e) { throw CommandException.Bad($"--{p.Name} is not valid JSON ({e.Message}): {text}"); }
    }
}
