using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoAuthoring.Engine;
using Unity.DataModel.Commands;

namespace CoAuthoring.Commands;

/// <summary>
/// The commands this service takes. The pipeline package's are described by catalog.json (its own descriptions,
/// parameters and schemas, from the Editor's GET /api/commands) and run by <see cref="Builtins"/>; custom commands
/// are static methods marked <c>[CliCommand]</c> (the package's attribute or Unity.DataModel's: they are found by
/// the attribute's name), with <c>[CliArg]</c> parameters and an <see cref="ICommandContext"/> parameter.
/// </summary>
public sealed class Catalog
{
    readonly Dictionary<string, CommandDef> byName = new(StringComparer.Ordinal);

    public IReadOnlyCollection<CommandDef> All => byName.Values;
    public CommandDef? Find(string name) => byName.TryGetValue(name, out var d) ? d : null;

    /// <summary>Package commands the catalog lists but this service can't run on the data model (Editor-only work).</summary>
    public IReadOnlyList<string> NotImplemented { get; private set; } = [];

    /// <summary>Errors met loading custom commands (an assembly that couldn't load, a method with an unsupported parameter).</summary>
    public List<string> LoadErrors { get; } = [];

    public static Catalog Builtin()
    {
        var c = new Catalog();
        using var stream = typeof(Catalog).Assembly.GetManifestResourceStream("CoAuthoring.catalog.json")!;
        var doc = JsonNode.Parse(stream)!;
        var missing = new List<string>();
        foreach (var cmd in doc["commands"]!.AsArray().OfType<JsonObject>())
        {
            var name = cmd["name"]!.GetValue<string>();
            if (!Builtins.All.TryGetValue(name, out var handler)) { missing.Add(name); continue; }
            c.Add(new CommandDef
            {
                Name = name,
                Description = cmd["description"]?.GetValue<string>() ?? "",
                Tags = cmd["tags"]?.AsArray().Select(t => t!.GetValue<string>()).ToList() ?? [],
                Package = cmd["package"]?.GetValue<string>() ?? "Unity.Pipeline.Editor",
                Schema = cmd["schema"]?.DeepClone(),
                Params = cmd["parameters"]!.AsArray().OfType<JsonObject>().Select(p => new ParamDef(
                    p["name"]!.GetValue<string>(), p["type"]?.GetValue<string>() ?? "String", p["description"]?.GetValue<string>() ?? "",
                    p["required"]?.GetValue<bool>() ?? false, p["defaultValue"]?.DeepClone())).ToList(),
                Writes = handler.Writes,
                Run = handler.Run,
            });
        }
        c.NotImplemented = missing;
        return c;
    }

    public void Add(CommandDef def)
    {
        if (byName.ContainsKey(def.Name)) throw new InvalidOperationException($"Two commands are named {def.Name}.");
        byName[def.Name] = def;
    }

    // ── custom commands ───────────────────────────────────────────────────

    /// <summary>
    /// Add the custom commands of every assembly in <paramref name="folder"/> that refers to Unity.DataModel (in a
    /// Unity project: Library/ScriptAssemblies, where the Editor compiles an assembly definition that references
    /// Unity.DataModel.dll). Returns how many were added.
    /// </summary>
    public int LoadCustom(string folder)
    {
        if (!Directory.Exists(folder)) return 0;
        var added = 0;
        foreach (var file in Directory.EnumerateFiles(folder, "*.dll"))
        {
            try
            {
                var name = AssemblyName.GetAssemblyName(file);
                using var probe = new System.Reflection.PortableExecutable.PEReader(File.OpenRead(file));
                if (!probe.HasMetadata || !ReferencesDataModel(probe)) continue;
                var context = new CommandLoadContext(file);
                added += AddCustom(context.LoadFromAssemblyPath(Path.GetFullPath(file)));
            }
            catch (Exception e) when (e is BadImageFormatException or FileLoadException or IOException or ReflectionTypeLoadException)
            {
                LoadErrors.Add($"{Path.GetFileName(file)}: {e.Message}");
            }
        }
        return added;
    }

    static bool ReferencesDataModel(System.Reflection.PortableExecutable.PEReader pe)
    {
        var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        return md.AssemblyReferences.Any(h => md.GetString(md.GetAssemblyReference(h).Name) == "Unity.DataModel");
    }

    // Unity.DataModel comes from this service, so the commands' ICommandContext is the one it passes; other
    // references load from the commands' folder.
    sealed class CommandLoadContext(string path) : AssemblyLoadContext(isCollectible: false)
    {
        readonly AssemblyDependencyResolver resolver = new(path);
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == "Unity.DataModel") return typeof(ICommandContext).Assembly;
            if (resolver.ResolveAssemblyToPath(name) is { } p) return LoadFromAssemblyPath(p);
            var local = Path.Combine(Path.GetDirectoryName(path)!, name.Name + ".dll");
            return File.Exists(local) ? LoadFromAssemblyPath(local) : null;
        }
    }

    /// <summary>Add the custom commands an assembly declares. Returns how many.</summary>
    public int AddCustom(Assembly assembly)
    {
        var added = 0;
        Type[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray()!; }
        foreach (var method in types.SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)))
        {
            var attr = method.GetCustomAttributesData().FirstOrDefault(a => a.AttributeType.Name == "CliCommandAttribute");
            if (attr == null) continue;
            try
            {
                Add(FromMethod(method, attr));
                added++;
            }
            catch (Exception e) when (e is InvalidOperationException or NotSupportedException)
            {
                LoadErrors.Add($"{method.DeclaringType?.FullName}.{method.Name}: {e.Message}");
            }
        }
        return added;
    }

    static CommandDef FromMethod(MethodInfo method, CustomAttributeData attr)
    {
        var name = (string)attr.ConstructorArguments[0].Value!;
        var description = attr.ConstructorArguments.Count > 1 ? (string?)attr.ConstructorArguments[1].Value ?? "" : "";
        var tags = attr.NamedArguments.FirstOrDefault(n => n.MemberName == "Tags").TypedValue.Value is IEnumerable<CustomAttributeTypedArgument> t
            ? t.Select(x => (string)x.Value!).ToList() : [];
        var parameters = method.GetParameters();
        if (!parameters.Any(p => p.ParameterType == typeof(ICommandContext)))
            throw new NotSupportedException("a custom command for co-authoring takes an ICommandContext parameter (it runs on the data model, not in the Editor)");
        var defs = new List<(ParameterInfo Info, ParamDef? Def)>();
        foreach (var p in parameters)
        {
            if (p.ParameterType == typeof(ICommandContext)) { defs.Add((p, null)); continue; }
            var arg = p.GetCustomAttributesData().FirstOrDefault(a => a.AttributeType.Name == "CliArgAttribute");
            var argName = arg?.ConstructorArguments[0].Value as string ?? p.Name!;
            var argDescription = arg?.ConstructorArguments.Count > 1 ? arg.ConstructorArguments[1].Value as string ?? "" : "";
            var required = arg?.NamedArguments.FirstOrDefault(n => n.MemberName == "Required").TypedValue.Value as bool? ?? !p.HasDefaultValue;
            JsonNode? def = p.HasDefaultValue && p.DefaultValue != null ? JsonSerializer.SerializeToNode(p.DefaultValue) : null;
            defs.Add((p, new ParamDef(argName, TypeName(p.ParameterType), argDescription, required, def)));
        }
        return new CommandDef
        {
            Name = name, Description = description, Tags = tags, Custom = true, Writes = true,
            Package = method.DeclaringType?.Assembly.GetName().Name ?? "",
            Params = defs.Where(d => d.Def != null).Select(d => d.Def!).ToList(),
            Run = (scope, args) =>
            {
                var values = defs.Select(d => d.Def == null ? scope : Convert(args[d.Def.Name], d.Info)).ToArray();
                object? result;
                try { result = method.Invoke(null, values); }
                catch (TargetInvocationException e) when (e.InnerException != null) { throw e.InnerException; }
                return Task.FromResult(result is null ? null : JsonSerializer.SerializeToNode(result, result.GetType()));
            },
        };
    }

    static string TypeName(Type t) => t == typeof(string) ? "String" : t == typeof(bool) ? "Boolean" : t == typeof(int) ? "Int32" : t == typeof(long) ? "Int64"
        : t == typeof(float) ? "Single" : t == typeof(double) ? "Double" : t == typeof(float[]) ? "Single[]" : t.Name;

    static object? Convert(JsonNode? value, ParameterInfo p)
    {
        if (value is null) return p.HasDefaultValue ? p.DefaultValue : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null;
        try { return value.Deserialize(p.ParameterType); }
        catch (JsonException e) { throw CommandException.Bad($"--{p.Name}: {e.Message}"); }
    }
}
