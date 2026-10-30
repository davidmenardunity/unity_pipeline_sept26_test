using System.Text.Json;
using System.Text.Json.Nodes;
using CoAuthoring.Engine;

namespace CoAuthoring.Commands;

/// <summary>
/// Runs a request on a session's queue. Each command runs on its own (its own sequence number) and reports its own
/// result, so one failure doesn't stop the rest; with <c>transaction: true</c> they run as one change instead (all
/// applied, or none). A string <c>"$&lt;index-or-id&gt;.&lt;path&gt;"</c> in a later command's parameters is replaced
/// by part of an earlier command's result (<c>"$0.globalId"</c>); <c>"$$"</c> escapes a dollar.
/// </summary>
public sealed class Runner(Catalog catalog)
{
    sealed record Parsed(int Index, string? Id, CommandDef? Def, JsonObject? Args, Exception? Error, string Text);

    public sealed record Request(string Actor, string Kind, string? Scene, JsonObject Body, string? IdempotencyKey);

    public async Task<JsonObject> RunAsync(Session session, Request request, CancellationToken ct)
    {
        var commands = request.Body["commands"] as JsonArray
            ?? throw CommandException.Bad("the body must be {\"commands\": [\"create_gameobject --name Box\", …]}");
        if (commands.Count == 0) throw CommandException.Bad("'commands' is empty");
        if (commands.Count > 500) throw CommandException.Bad("at most 500 commands per request");
        if (session.TryReplay(request.IdempotencyKey, out var replay)) return replay!;
        var transaction = request.Body["transaction"]?.GetValue<bool>() ?? false;
        long? baseSeq = request.Body["baseSeq"] is JsonValue bs && bs.TryGetValue<long>(out var b) ? b : null;
        var parsed = commands.Select((c, i) => Parse(i, c)).ToList();
        var answer = await session.Enqueue(async () =>
        {
            var ctx = new Ctx(session, request, baseSeq, ct);
            var results = transaction ? await RunTransaction(ctx, parsed) : await RunEach(ctx, parsed);
            return new JsonObject { ["seq"] = session.Seq, ["results"] = new JsonArray(results.ToArray()) };
        });
        session.Remember(request.IdempotencyKey, answer);
        return answer;
    }

    sealed record Ctx(Session Session, Request Request, long? BaseSeq, CancellationToken Ct);

    Parsed Parse(int index, JsonNode? item)
    {
        var text = item?.ToJsonString() ?? "null";
        string? id = null;
        try
        {
            switch (item)
            {
                case JsonValue v when v.TryGetValue<string>(out var line):
                {
                    text = line;
                    var tokens = CommandLine.Tokenize(line);
                    var def = CommandLine.Resolve(catalog, tokens);
                    return new Parsed(index, null, def, CommandLine.Bind(def, tokens.Skip(1).ToList()), null, line);
                }
                case JsonObject o:
                {
                    id = o["id"]?.GetValue<string>();
                    if (o["argv"] is JsonArray argv)
                    {
                        var tokens = argv.Select(t => t?.ToString() ?? "").ToList();
                        text = string.Join(' ', tokens);
                        var def = CommandLine.Resolve(catalog, tokens);
                        return new Parsed(index, id, def, CommandLine.Bind(def, tokens.Skip(1).ToList()), null, text);
                    }
                    if (o["command"] is JsonValue name)
                    {
                        text = name.GetValue<string>();
                        var def = CommandLine.Resolve(catalog, CommandLine.Tokenize(text));
                        var given = (o["params"] ?? o["parameters"]) as JsonObject ?? new JsonObject();
                        // "$0.id" references are substituted when the command runs; required ones may still be references here.
                        return new Parsed(index, id, def, CommandLine.Normalize(def, (JsonObject)given.DeepClone()), null, text);
                    }
                    throw CommandException.Bad("a command is a string, {argv: […]} or {command, params}");
                }
                default:
                    throw CommandException.Bad("a command is a string, {argv: […]} or {command, params}");
            }
        }
        catch (Exception e) { return new Parsed(index, id, null, null, e, text); }
    }

    // ── one command at a time ─────────────────────────────────────────────

    async Task<List<JsonNode>> RunEach(Ctx ctx, List<Parsed> parsed, bool stopOnError = false)
    {
        var results = new List<JsonNode>();
        var done = new List<(Parsed P, JsonNode? Result, bool Ok)>();
        foreach (var p in parsed)
        {
            if (stopOnError && done.Any(d => !d.Ok)) { results.Add(Status(p, "skipped")); continue; }
            if (p.Error != null) { results.Add(Error(ctx, p, p.Error)); done.Add((p, null, false)); continue; }
            try
            {
                JsonNode? value;
                JsonObject? changes = null;
                if (p.Def!.Name == "batch") value = await RunBatch(ctx, SubstituteExcept(p.Args!, done, "operations"));
                else
                {
                    var scope = ctx.Session.NewScope(ctx.Request.Actor, ctx.Request.Kind, ctx.Request.Scene, ctx.Ct);
                    value = await p.Def.Run(scope, Substitute(p.Args!, done));
                    changes = await ctx.Session.CommitScopeAsync(scope, p.Text, ctx.BaseSeq);
                }
                results.Add(Ok(ctx, p, value, changes));
                done.Add((p, value, true));
            }
            catch (Exception e)
            {
                results.Add(Error(ctx, p, e));
                done.Add((p, null, false));
            }
        }
        return results;
    }

    // ── all or nothing ────────────────────────────────────────────────────

    async Task<List<JsonNode>> RunTransaction(Ctx ctx, List<Parsed> parsed)
    {
        if (parsed.FirstOrDefault(p => p.Error != null) is { } bad)
            return parsed.Select(p => p == bad ? Error(ctx, p, p.Error!) : Status(p, "skipped")).Cast<JsonNode>().ToList();
        if (parsed.FirstOrDefault(p => p.Def!.Name == "batch") is { } nested)
            return parsed.Select(p => p == nested ? Error(ctx, p, CommandException.Bad("batch can't run inside a transaction")) : Status(p, "skipped")).Cast<JsonNode>().ToList();
        var scope = ctx.Session.NewScope(ctx.Request.Actor, ctx.Request.Kind, ctx.Request.Scene, ctx.Ct);
        var values = new JsonNode?[parsed.Count];
        var done = new List<(Parsed P, JsonNode? Result, bool Ok)>();
        foreach (var p in parsed)
        {
            try { values[p.Index] = await p.Def!.Run(scope, Substitute(p.Args!, done)); }
            catch (Exception e)
            {
                return parsed.Select(q => q.Index < p.Index ? Status(q, "reverted") : q.Index == p.Index ? Error(ctx, q, e) : Status(q, "skipped")).Cast<JsonNode>().ToList();
            }
            done.Add((p, values[p.Index], true));
        }
        JsonObject? changes;
        try { changes = await ctx.Session.CommitScopeAsync(scope, string.Join(" ; ", parsed.Select(p => p.Text)), ctx.BaseSeq); }
        catch (Exception e) { return parsed.Select(p => p.Index == parsed.Count - 1 ? Error(ctx, p, e) : Status(p, "reverted")).Cast<JsonNode>().ToList(); }
        return parsed.Select(p => Ok(ctx, p, values[p.Index], p.Index == parsed.Count - 1 ? changes : null, note: false)).Cast<JsonNode>().ToList();
    }

    /// <summary>The CLI's <c>batch</c>: operations [{id?, command, params}], transactional (default) or not.</summary>
    async Task<JsonNode?> RunBatch(Ctx ctx, JsonObject args)
    {
        var ops = args["operations"] as JsonArray ?? throw CommandException.Bad("--operations must be a list of {id?, command, params}");
        if (ops.Count > 200) throw CommandException.Bad("at most 200 operations per batch");
        var onError = args["on_error"]?.GetValue<string>() ?? "abort";
        var transactional = (args["transactional"]?.GetValue<bool>() ?? true) && onError != "continue";
        var parsed = ops.Select((o, i) => o is JsonObject op && op["command"]?.GetValue<string>() == "batch"
            ? new Parsed(i, op["id"]?.GetValue<string>(), null, null, CommandException.Bad("batch can't be nested"), "batch")
            : Parse(i, o)).ToList();
        if (args["dry_run"]?.GetValue<bool>() == true)
            return new JsonObject
            {
                ["dryRun"] = true, ["valid"] = parsed.All(p => p.Error == null),
                ["results"] = new JsonArray(parsed.Select(p => p.Error == null ? Status(p, "valid") : Error(ctx, p, p.Error)).Cast<JsonNode>().ToArray()),
            };
        var results = transactional ? await RunTransaction(ctx, parsed) : await RunEach(ctx, parsed, stopOnError: onError == "abort");
        var failed = results.Any(r => r!["status"]!.GetValue<string>() == "error");
        return new JsonObject
        {
            ["results"] = new JsonArray(results.ToArray()), ["applied"] = results.Count(r => r!["status"]!.GetValue<string>() == "ok"),
            ["reverted"] = transactional && failed, ["transactional"] = transactional, ["onError"] = onError,
        };
    }

    // ── references ────────────────────────────────────────────────────────

    static JsonObject Substitute(JsonObject args, List<(Parsed P, JsonNode? Result, bool Ok)> done) => (JsonObject)Replace(args.DeepClone(), done)!;

    static JsonObject SubstituteExcept(JsonObject args, List<(Parsed P, JsonNode? Result, bool Ok)> done, string key)
    {
        var copy = (JsonObject)args.DeepClone();
        var kept = copy[key];
        copy.Remove(key);
        var result = Substitute(copy, done);
        result[key] = kept?.DeepClone();
        return result;
    }

    static JsonNode? Replace(JsonNode? node, List<(Parsed P, JsonNode? Result, bool Ok)> done)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(kv => kv.Key).ToList()) o[key] = Replace(o[key]?.DeepClone(), done);
                return o;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++) a[i] = Replace(a[i]?.DeepClone(), done);
                return a;
            case JsonValue v when v.TryGetValue<string>(out var s) && s.StartsWith('$'):
                if (s.StartsWith("$$", StringComparison.Ordinal)) return JsonValue.Create(s[1..]);
                var dot = s.IndexOf('.');
                var head = dot < 0 ? s[1..] : s[1..dot];
                var path = dot < 0 ? "" : s[(dot + 1)..];
                var target = done.LastOrDefault(d => d.P.Id == head);
                if (target.P is null && int.TryParse(head, out var index)) target = done.FirstOrDefault(d => d.P.Index == index);
                if (target.P is null) throw CommandException.Bad($"'{s}' refers to no earlier command (references point backwards, by index or id)");
                if (!target.Ok) throw CommandException.Bad($"'{s}' refers to command {target.P.Index}, which failed");
                return Select(target.Result, path) ?? throw CommandException.Bad($"'{s}': command {target.P.Index}'s result has no '{path}'");
            default:
                return node;
        }
    }

    /// <summary>A small SelectToken: "globalId", "created[0].globalId".</summary>
    static JsonNode? Select(JsonNode? node, string path)
    {
        if (path.Length == 0) return node?.DeepClone();
        var current = node;
        foreach (var part in path.Split('.'))
        {
            var name = part;
            var indexes = new List<int>();
            while (name.EndsWith(']') && name.LastIndexOf('[') is var open and >= 0)
            {
                indexes.Insert(0, int.Parse(name[(open + 1)..^1]));
                name = name[..open];
            }
            if (name.Length > 0) current = (current as JsonObject)?[name];
            foreach (var i in indexes) current = current is JsonArray arr && i < arr.Count ? arr[i] : null;
            if (current is null) return null;
        }
        return current?.DeepClone();
    }

    // ── results ───────────────────────────────────────────────────────────

    static JsonObject Ok(Ctx ctx, Parsed p, JsonNode? result, JsonObject? changes, bool note = true)
    {
        var o = Status(p, "ok");
        o["result"] = result;
        if (changes != null) { o["seq"] = ctx.Session.Seq; o["changes"] = changes; }
        if (note) Note(ctx, p, "ok", changes);
        return o;
    }

    static JsonObject Status(Parsed p, string status)
    {
        var o = new JsonObject { ["status"] = status, ["command"] = p.Def?.Name ?? p.Text };
        if (p.Id != null) o["id"] = p.Id;
        return o;
    }

    static JsonObject Error(Ctx ctx, Parsed p, Exception e)
    {
        var o = Status(p, "error");
        switch (e)
        {
            case CommandException c:
                o["code"] = c.Code;
                o["message"] = c.Message;
                if (c.Details != null) o["details"] = c.Details.DeepClone();
                break;
            case KeyNotFoundException:
                o["code"] = ErrorCodes.NotFound;
                o["message"] = e.Message;
                break;
            case JsonException or InvalidOperationException or FormatException or ArgumentException:
                o["code"] = ErrorCodes.Invalid;
                o["message"] = e.Message;
                break;
            default:
                o["code"] = ErrorCodes.Internal;
                o["message"] = e.Message;
                break;
        }
        o["retry"] = "Nothing was changed; fix the command and send it again.";
        Note(ctx, p, "error", null);
        return o;
    }

    // What someone touched shows on their presence entry.
    static void Note(Ctx ctx, Parsed p, string status, JsonObject? changes)
    {
        var touched = new JsonArray();
        foreach (var (_, file) in changes ?? [])
        {
            foreach (var c in file?["created"]?.AsArray() ?? []) touched.Add(c?["fileId"]?.DeepClone());
            foreach (var u in file?["updated"]?.AsArray() ?? []) touched.Add((u?["gameObject"] ?? u?["fileId"])?.DeepClone());
        }
        ctx.Session.Presence.NoteCommand(ctx.Request.Actor, ctx.Request.Kind, p.Text, status, touched);
    }
}
