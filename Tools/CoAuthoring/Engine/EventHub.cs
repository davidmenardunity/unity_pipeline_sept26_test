using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace CoAuthoring.Engine;

/// <summary>Something that happened in a session: a write (with what it changed), a save, a discard, or presence.</summary>
public sealed record SessionEvent(long Seq, string Type, string? Actor, JsonObject? Data, DateTimeOffset At)
{
    public JsonObject ToJson() => new()
    {
        ["seq"] = Seq, ["type"] = Type, ["actor"] = Actor, ["at"] = At.ToString("O"), ["data"] = Data?.DeepClone(),
    };
}

/// <summary>
/// Fan-out of session events with a replay buffer: a subscriber passing <c>since</c> gets every buffered event after
/// it, then live ones. When <c>since</c> is older than the buffer it gets one "resync" event and should read again.
/// Presence goes to current subscribers only (<see cref="PublishTransient"/>), never replayed.
/// </summary>
public sealed class EventHub(int capacity)
{
    readonly LinkedList<SessionEvent> buffer = new();
    readonly List<Channel<SessionEvent>> subscribers = new();
    readonly object gate = new();

    public void Publish(SessionEvent e) => Publish(e, buffered: true);
    public void PublishTransient(SessionEvent e) => Publish(e, buffered: false);

    void Publish(SessionEvent e, bool buffered)
    {
        List<Channel<SessionEvent>> subs;
        lock (gate)
        {
            if (buffered)
            {
                buffer.AddLast(e);
                while (buffer.Count > capacity) buffer.RemoveFirst();
            }
            subs = subscribers.ToList();
        }
        foreach (var s in subs) s.Writer.TryWrite(e);
    }

    public async IAsyncEnumerable<SessionEvent> Subscribe(long? since, long currentSeq, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateBounded<SessionEvent>(new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest });
        List<SessionEvent> backlog;
        lock (gate)
        {
            backlog = since is null ? [] : buffer.Where(e => e.Seq > since).ToList();
            var oldest = buffer.First?.Value.Seq;
            if (since != null && since < currentSeq && (oldest is null || oldest > since + 1))
                backlog = [new SessionEvent(currentSeq, "resync", null, null, DateTimeOffset.UtcNow)];
            subscribers.Add(channel);
        }
        try
        {
            var last = since ?? currentSeq;
            foreach (var e in backlog)
            {
                last = Math.Max(last, e.Seq);
                yield return e;
            }
            await foreach (var e in channel.Reader.ReadAllAsync(ct))
            {
                if (e.Seq <= last && e.Type == "write") continue;   // already sent from the backlog
                last = Math.Max(last, e.Seq);
                yield return e;
            }
        }
        finally
        {
            lock (gate) subscribers.Remove(channel);
        }
    }
}
