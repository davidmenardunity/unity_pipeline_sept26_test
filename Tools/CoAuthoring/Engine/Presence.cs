using System.Text.Json.Nodes;

namespace CoAuthoring.Engine;

/// <summary>
/// Who is in a session and where: each participant reports a state (scene, camera, selection) a few times a second;
/// someone silent for <see cref="Timeout"/> leaves. Commands they send mark them active and say what they touched.
/// Every change goes out as a "presence" event with the whole list.
/// </summary>
public sealed class Presence : IDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    readonly object gate = new();
    readonly Action<JsonArray> publish;
    readonly Timer sweep;

    sealed class Entry
    {
        public required string Actor { get; init; }
        public string Kind { get; set; } = "human";
        public JsonObject State { get; set; } = new();
        public DateTime LastSeen { get; set; }
        public JsonObject? LastCommand { get; set; }
    }

    public Presence(Action<JsonArray> publish)
    {
        this.publish = publish;
        sweep = new Timer(_ => Sweep(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    /// <summary>A stable colour for a name.</summary>
    public static string ColorOf(string actor)
    {
        string[] palette = ["#e5484d", "#3e63dd", "#30a46c", "#f76b15", "#8e4ec6", "#12a594", "#d6409f", "#ffb224", "#0090ff", "#46a758"];
        var h = 0u;
        foreach (var c in actor) h = h * 31 + c;
        return palette[h % (uint)palette.Length];
    }

    public JsonArray List()
    {
        lock (gate)
            return new JsonArray(entries.Values.OrderBy(e => e.Actor, StringComparer.OrdinalIgnoreCase).Select(e => (JsonNode)new JsonObject
            {
                ["actor"] = e.Actor, ["kind"] = e.Kind, ["color"] = ColorOf(e.Actor),
                ["state"] = e.State.DeepClone(), ["lastSeen"] = e.LastSeen.ToString("O"), ["lastCommand"] = e.LastCommand?.DeepClone(),
            }).ToArray());
    }

    /// <summary>A participant's state: { scene, camera: {p, q, fov, aspect, near, far}, selection }.</summary>
    public void Report(string actor, string kind, JsonObject state)
    {
        lock (gate)
        {
            var e = Touch(actor, kind);
            e.State = (JsonObject)state.DeepClone();
        }
        publish(List());
    }

    /// <summary>A command someone ran: they are active, and what it touched shows on their entry.</summary>
    public void NoteCommand(string actor, string kind, string command, string status, JsonArray? touched)
    {
        lock (gate)
            Touch(actor, kind).LastCommand = new JsonObject
                { ["command"] = command, ["status"] = status, ["touched"] = touched?.DeepClone(), ["at"] = DateTime.UtcNow.ToString("O") };
        publish(List());
    }

    public void Leave(string actor)
    {
        bool removed;
        lock (gate) removed = entries.Remove(actor);
        if (removed) publish(List());
    }

    Entry Touch(string actor, string kind)
    {
        if (!entries.TryGetValue(actor, out var e)) entries[actor] = e = new Entry { Actor = actor };
        e.Kind = kind;
        e.LastSeen = DateTime.UtcNow;
        return e;
    }

    void Sweep()
    {
        bool changed;
        lock (gate)
        {
            var stale = entries.Values.Where(e => DateTime.UtcNow - e.LastSeen > Timeout).Select(e => e.Actor).ToList();
            foreach (var a in stale) entries.Remove(a);
            changed = stale.Count > 0;
        }
        if (changed) publish(List());
    }

    public void Dispose() => sweep.Dispose();
}
