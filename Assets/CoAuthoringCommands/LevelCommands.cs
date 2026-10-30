using System;
using System.Collections.Generic;
using Unity.DataModel;
using Unity.DataModel.Commands;

namespace Unity.Pipeline.CoAuthoringCommands
{
    // Custom commands for co-authoring, declared the way the pipeline package declares commands ([CliCommand],
    // [CliArg]). This assembly has no engine references and uses Unity.DataModel.dll, so the co-authoring service
    // (Pipeline Explorer) finds it in Library/ScriptAssemblies and runs it on the shared draft, with everyone else's
    // commands, in order.
    public static class LevelCommands
    {
        [CliCommand("scatter_prefab", "Place copies of a prefab in a ring around a point of the open scene.", Tags = new[] { "co-authoring" })]
        public static object ScatterPrefab(
            ICommandContext context,
            [CliArg("prefab", "The prefab's asset path (Assets/…/X.prefab).", Required = true)] string prefab,
            [CliArg("count", "How many copies.")] int count = 6,
            [CliArg("radius", "The ring's radius, in metres.")] float radius = 5f,
            [CliArg("center", "The ring's centre as [x,y,z].")] float[] center = null,
            [CliArg("name", "The copies' name (numbered).")] string name = null)
        {
            if (count < 1 || count > 200) throw new ArgumentException("count must be between 1 and 200");
            var guid = context.GuidOf(prefab) ?? throw new ArgumentException($"No asset at {prefab}.");
            var root = context.Prefab(guid) ?? throw new ArgumentException($"{prefab} isn't a prefab with a plain root.");
            var c = center is { Length: 3 } ? new Vector3(center[0], center[1], center[2]) : Vector3.Zero;
            var scene = context.Scene;
            var created = new List<string>();
            for (var i = 0; i < count; i++)
            {
                var angle = 2 * Math.PI * i / count;
                var position = new Vector3(c.X + radius * Math.Cos(angle), c.Y, c.Z + radius * Math.Sin(angle));
                // Each copy faces the centre.
                var facing = Quaternion.Euler(0, 270 - angle * 180 / Math.PI, 0);
                var id = scene.InstantiatePrefab(guid, root, $"{name ?? root.Name ?? "Prefab"} {i + 1}", new Pose(position, facing, root.Pose.Scale));
                created.Add(id.ToString());
            }
            return new { scene = context.ScenePath, prefab, created };
        }
    }
}
