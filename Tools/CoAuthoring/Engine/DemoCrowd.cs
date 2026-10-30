using System.Globalization;
using System.Text.Json.Nodes;
using CoAuthoring.Commands;

namespace CoAuthoring.Engine;

/// <summary>
/// Pretend people for a demo: each joins a scene, looks around it (presence, with a moving camera), and every few
/// seconds sends a command, as anyone would: places a random prefab of the project (a primitive when there are
/// none) in their own area around the scene, then moves, turns and resizes what they placed, and tidies up.
/// Their commands go through the same queue as everyone else's.
/// </summary>
public sealed class DemoCrowd
{
    static readonly string[] Names = ["Alex", "Bea", "Chen", "Dana", "Eli", "Fay", "Gus", "Hana"];
    static readonly string[] Shapes = ["Cube", "Sphere", "Cylinder", "Capsule"];
    readonly Session session;
    readonly Runner runner;
    CancellationTokenSource? running;
    IReadOnlyList<string> prefabs = [];

    public DemoCrowd(Session session, Runner runner)
    {
        this.session = session;
        this.runner = runner;
    }

    public bool IsRunning => running is { IsCancellationRequested: false };

    /// <summary>
    /// Start <paramref name="count"/> people in <paramref name="scene"/> for <paramref name="duration"/>, placing
    /// <paramref name="prefabPaths"/> (asset paths; primitives when there are none) around <paramref name="center"/>.
    /// </summary>
    public void Start(string scene, int count, TimeSpan duration, Vector center, IReadOnlyList<string>? prefabPaths = null)
    {
        Stop();
        prefabs = prefabPaths ?? [];
        var cts = running = new CancellationTokenSource(duration);
        for (var i = 0; i < Math.Clamp(count, 1, Names.Length); i++)
        {
            var name = Names[i];
            var seed = i;
            // Each person works in their own area: around the centre, apart from the others.
            var angle = 2 * Math.PI * i / Math.Max(1, count);
            var area = new Vector(center.X + Math.Cos(angle) * 16, center.Y, center.Z + Math.Sin(angle) * 16);
            _ = Task.Run(() => Actor(name, seed, scene, area, cts.Token));
        }
    }

    public void Stop()
    {
        running?.Cancel();
        running = null;
    }

    public readonly record struct Vector(double X, double Y, double Z);

    static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    async Task Actor(string name, int seed, string scene, Vector area, CancellationToken ct)
    {
        var rng = new Random(seed * 7919 + Environment.TickCount);
        var mine = new List<string>();   // hierarchy paths of what this person placed
        var made = 0;
        var orbit = rng.NextDouble() * Math.PI * 2;
        var radius = 12 + rng.NextDouble() * 8;
        var height = 4 + rng.NextDouble() * 5;
        var nextCommand = DateTime.UtcNow.AddSeconds(1 + rng.NextDouble() * 2);
        try
        {
            await Send($"open_scene --path \"{scene}\"");
            while (!ct.IsCancellationRequested)
            {
                // Look around: circle the scene, looking at its centre.
                orbit += 0.06 + seed * 0.004;
                var p = new double[] { area.X + Math.Cos(orbit) * radius, area.Y + height, area.Z + Math.Sin(orbit) * radius };
                session.Presence.Report(name, "human", new JsonObject
                {
                    ["scene"] = scene,
                    ["camera"] = new JsonObject { ["p"] = Arr(p), ["q"] = Arr(LookAt(p, [area.X, area.Y, area.Z])), ["fov"] = 60, ["aspect"] = 1.6 },
                    ["demo"] = true,
                });
                if (DateTime.UtcNow >= nextCommand)
                {
                    await Send(Next());
                    nextCommand = DateTime.UtcNow.AddSeconds(2 + rng.NextDouble() * 3);
                }
                await Task.Delay(300, ct);
            }
        }
        catch (OperationCanceledException) { }
        finally { session.Presence.Leave(name); }

        string Next()
        {
            double R(double a, double b) => a + rng.NextDouble() * (b - a);
            string Pos(double up = 0) => $"[{F(area.X + R(-8, 8))},{F(area.Y + up)},{F(area.Z + R(-8, 8))}]";
            var pick = rng.NextDouble();
            if (mine.Count < 2 || pick < 0.35)
            {
                if (prefabs.Count > 0)
                {
                    var prefab = prefabs[rng.Next(prefabs.Count)];
                    var placed = $"/{name} {Path.GetFileNameWithoutExtension(prefab)} {++made}";
                    mine.Add(placed);
                    return $"instantiate_prefab --prefab \"{prefab}\" --name \"{placed[1..]}\" ; set_transform --target \"{placed}\" --position {Pos()} --rotation [0,{rng.Next(0, 360)},0]";
                }
                var shape = Shapes[rng.Next(Shapes.Length)];
                var path = $"/{name} {shape} {++made}";
                mine.Add(path);
                return $"create_gameobject --name \"{path[1..]}\" --primitive {shape} ; set_transform --target \"{path}\" --position {Pos(0.5)}";
            }
            var target = mine[rng.Next(mine.Count)];
            if (pick < 0.55) return $"set_transform --target \"{target}\" --position {Pos()}";
            if (pick < 0.7) return $"set_transform --target \"{target}\" --rotation [0,{rng.Next(0, 360)},0]";
            if (pick < 0.82) { var s = F(R(0.7, 1.6)); return $"set_transform --target \"{target}\" --scale [{s},{s},{s}]"; }
            if (pick < 0.9 && mine.Count > 1 && prefabs.Count == 0)
            {
                // Stack one of their things on another.
                var parent = mine.First(m => m != target);
                if (target.StartsWith(parent + "/")) return $"set_transform --target \"{target}\" --position [0,1.2,0]";
                var moved = parent + "/" + target.Split('/').Last();
                mine[mine.IndexOf(target)] = moved;
                for (var k = 0; k < mine.Count; k++)
                    if (mine[k].StartsWith(target + "/")) mine[k] = moved + mine[k][target.Length..];
                return $"set_parent --target \"{target}\" --parent \"{parent}\" --world_position_stays false ; set_transform --target \"{moved}\" --position [0,1.2,0]";
            }
            if (mine.Count > 6)
            {
                var gone = mine[0];
                mine.RemoveAll(m => m == gone || m.StartsWith(gone + "/"));
                return $"delete_gameobject --target \"{gone}\"";
            }
            return $"set_active --target \"{target}\" --active true";
        }

        async Task Send(string line)
        {
            // "a ; b": one transaction (all or nothing), like a person's single gesture.
            var parts = line.Split(" ; ");
            var body = new JsonObject { ["commands"] = new JsonArray(parts.Select(x => (JsonNode)x).ToArray()), ["transaction"] = parts.Length > 1 };
            try { await runner.RunAsync(session, new Runner.Request(name, "human", scene, body, null), ct); }
            catch (Exception e) when (e is CommandException or OperationCanceledException) { }
        }
    }

    static JsonArray Arr(double[] v) => new(v.Select(x => (JsonNode)Math.Round(x, 4)).ToArray());

    // A rotation that looks from p towards t (Unity: +z forward, +y up).
    static double[] LookAt(double[] p, double[] t)
    {
        double dx = t[0] - p[0], dy = t[1] - p[1], dz = t[2] - p[2];
        var yaw = Math.Atan2(dx, dz);
        var pitch = -Math.Atan2(dy, Math.Sqrt(dx * dx + dz * dz));
        double cy = Math.Cos(yaw / 2), sy = Math.Sin(yaw / 2), cp = Math.Cos(pitch / 2), sp = Math.Sin(pitch / 2);
        // q = yaw (about y) * pitch (about x)
        return [cy * sp, sy * cp, -sy * sp, cy * cp];
    }
}
