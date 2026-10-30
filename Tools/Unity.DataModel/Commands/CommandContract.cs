using System;

namespace Unity.DataModel.Commands
{
    /// <summary>
    /// Marks a static method as a command, as <c>Unity.Pipeline.Commands.CliCommandAttribute</c> does in the
    /// pipeline package: same name, same shape. The co-authoring service finds commands by this attribute's name,
    /// so methods marked with either one are found; a method that runs on the data model takes an
    /// <see cref="ICommandContext"/> parameter.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class CliCommandAttribute : Attribute
    {
        public CliCommandAttribute(string name, string description)
        {
            Name = name;
            Description = description;
        }

        public string Name { get; }
        public string Description { get; }
        public string[] Tags { get; set; } = Array.Empty<string>();
    }

    /// <summary>A command's parameter, as <c>Unity.Pipeline.Commands.CliArgAttribute</c>: its CLI name and description.</summary>
    [AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false)]
    public sealed class CliArgAttribute : Attribute
    {
        public CliArgAttribute(string name, string description)
        {
            Name = name;
            Description = description;
        }

        public string Name { get; }
        public string Description { get; }
        public bool Required { get; set; }
        public object? DefaultValue { get; set; }
    }

    /// <summary>
    /// Where a command runs on the data model: the scene the caller is working in, other files of the project,
    /// and the prefabs and assets they refer to. Changes made to files opened here are kept when the command
    /// succeeds, and dropped when it throws.
    /// </summary>
    public interface ICommandContext
    {
        /// <summary>Who sent the command.</summary>
        string Actor { get; }

        /// <summary>The scene the caller is working in (its path, and the file).</summary>
        string ScenePath { get; }
        UnityFile Scene { get; }

        /// <summary>Another file of the project (a prefab, a scene, a material…), by path.</summary>
        UnityFile Open(string path);

        /// <summary>An asset's GUID by path ("Assets/…"), or null.</summary>
        string? GuidOf(string path);

        /// <summary>An asset's path by GUID, or null.</summary>
        string? PathOf(string guid);

        /// <summary>A prefab's root by GUID (for placing instances), or null when it has no plain root.</summary>
        PrefabRoot? Prefab(string guid);
    }
}
