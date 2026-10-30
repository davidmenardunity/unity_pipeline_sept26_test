using System.Text.Json.Nodes;

namespace CoAuthoring.Commands;

/// <summary>A parameter, as the pipeline package declares it (<c>[CliArg]</c>): its CLI name and .NET type name ("ObjectRef", "Single[]"…).</summary>
public sealed record ParamDef(string Name, string Type, string Description, bool Required = false, JsonNode? Default = null)
{
    /// <summary>How the parameter's text is read on a command line.</summary>
    public ParamKind Kind => Type switch
    {
        "Boolean" => ParamKind.Bool,
        "Int32" or "Int64" => ParamKind.Int,
        "Single" or "Double" => ParamKind.Number,
        "ObjectRef" => ParamKind.Handle,
        "String" => ParamKind.String,
        _ => ParamKind.Json,   // arrays, JObject, JToken, lists
    };
}

public enum ParamKind { String, Bool, Int, Number, Json, Handle }

/// <summary>
/// One command: the name, description and parameters the pipeline package declares, and how this service runs it
/// on the Unity data model. <see cref="Writes"/> commands change files; the others only read.
/// </summary>
public sealed class CommandDef
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public IReadOnlyList<ParamDef> Params { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Where the command is declared: "Unity.Pipeline.Editor" for the package's, else the custom command's assembly.</summary>
    public string Package { get; init; } = "";
    public JsonNode? Schema { get; init; }
    public bool Custom { get; init; }
    public bool Writes { get; init; }
    public required Func<Engine.CommandScope, JsonObject, Task<JsonNode?>> Run { get; init; }

    public ParamDef? Param(string name) => Params.FirstOrDefault(p => p.Name == name);

    public JsonObject Describe(bool full) => full
        ? new JsonObject
        {
            ["name"] = Name, ["description"] = Description, ["tags"] = new JsonArray(Tags.Select(t => (JsonNode)t).ToArray()),
            ["package"] = Package, ["custom"] = Custom, ["writes"] = Writes,
            ["parameters"] = new JsonArray(Params.Select(p => (JsonNode)new JsonObject
            {
                ["name"] = p.Name, ["description"] = p.Description, ["type"] = p.Type, ["required"] = p.Required, ["defaultValue"] = p.Default?.DeepClone(),
            }).ToArray()),
            ["schema"] = Schema?.DeepClone(),
        }
        : new JsonObject { ["name"] = Name, ["description"] = Description, ["custom"] = Custom };
}

/// <summary>A command that failed: a code the caller can act on, a message, and optional details (current values on a conflict).</summary>
public sealed class CommandException(string code, string message, JsonNode? details = null) : Exception(message)
{
    public string Code { get; } = code;
    public JsonNode? Details { get; } = details;

    public static CommandException Bad(string message) => new(ErrorCodes.BadRequest, message);
    public static CommandException NotFound(string message) => new(ErrorCodes.NotFound, message);
}

public static class ErrorCodes
{
    public const string BadRequest = "bad_request";
    public const string NotFound = "not_found";
    public const string Conflict = "conflict";
    public const string Invalid = "invalid_operation";
    public const string Unsupported = "unsupported";
    public const string Internal = "internal";
}
