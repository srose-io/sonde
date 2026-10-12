using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Sonde.Probe;

/// <summary>One captured <see cref="DiagnosticListener"/> event: when, its name, and a depth-2 summary of its payload.</summary>
/// <param name="Timestamp">When the event was observed.</param>
/// <param name="EventName">The event's name as the listener wrote it.</param>
/// <param name="Payload">The event's payload, serialised two levels deep.</param>
public sealed record TapEntry(DateTimeOffset Timestamp, string EventName, JsonNode? Payload);

/// <summary>
/// Ring-buffered taps on named <see cref="DiagnosticListener"/>s (EF Core SQL, HttpClient,
/// ASP.NET). A tap persists across evals like <c>g.Vars</c>; reading it never clears it.
/// </summary>
internal static class Taps
{
    private const int PayloadDepth = 2;
    private const int DefaultCapacity = 1000;

    private sealed class Tap
    {
        public required string ListenerName { get; init; }
        public int Capacity { get; init; }
        public ConcurrentQueue<TapEntry> Buffer { get; } = new();
        public List<IDisposable> Subscriptions { get; } = [];
    }

    private static readonly ConcurrentDictionary<string, Tap> Active = new(StringComparer.Ordinal);

    public static void Start(string listenerName, int capacity)
    {
        ArgumentException.ThrowIfNullOrEmpty(listenerName);
        if (Active.ContainsKey(listenerName))
            return;

        var tap = new Tap { ListenerName = listenerName, Capacity = capacity <= 0 ? DefaultCapacity : capacity };
        if (!Active.TryAdd(listenerName, tap))
            return; // a concurrent Start won

        // AllListeners replays every existing listener to a new subscriber, so this catches a
        // listener already created (EF Core's, say) as well as one created later.
        tap.Subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(new AllListenersObserver(listenerName)));
    }

    public static IReadOnlyList<TapEntry> Read(string listenerName, DateTimeOffset? since)
    {
        if (!Active.TryGetValue(listenerName, out var tap))
            return [];
        var all = tap.Buffer.ToArray();
        return since is { } cutoff ? all.Where(e => e.Timestamp >= cutoff).ToArray() : all;
    }

    public static void Stop(string listenerName)
    {
        if (!Active.TryRemove(listenerName, out var tap))
            return;
        foreach (var subscription in tap.Subscriptions)
        {
            try
            {
                subscription.Dispose();
            }
            catch (Exception)
            {
                // A listener that is already gone; nothing to release.
            }
        }
    }

    public static IReadOnlyList<string> Names() => Active.Keys.ToArray();

    private static void Record(string listenerName, KeyValuePair<string, object?> evt)
    {
        if (!Active.TryGetValue(listenerName, out var tap))
            return;
        JsonNode? payload;
        try
        {
            payload = ResultJson.Serialize(evt.Value, PayloadDepth);
        }
        catch (Exception)
        {
            payload = JsonValue.Create("<payload unserialisable>");
        }
        tap.Buffer.Enqueue(new TapEntry(DateTimeOffset.UtcNow, evt.Key, payload));
        while (tap.Buffer.Count > tap.Capacity && tap.Buffer.TryDequeue(out _))
        {
        }
    }

    private sealed class AllListenersObserver(string listenerName) : IObserver<DiagnosticListener>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }

        public void OnNext(DiagnosticListener value)
        {
            if (value.Name == listenerName && Active.TryGetValue(listenerName, out var tap))
                tap.Subscriptions.Add(value.Subscribe(new EventObserver(listenerName)));
        }
    }

    private sealed class EventObserver(string listenerName) : IObserver<KeyValuePair<string, object?>>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(KeyValuePair<string, object?> value) => Record(listenerName, value);
    }
}
