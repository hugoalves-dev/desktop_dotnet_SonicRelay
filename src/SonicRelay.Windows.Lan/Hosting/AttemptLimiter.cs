using System.Collections.Concurrent;
using System.Net;

namespace SonicRelay.Windows.Lan.Hosting;

/// <summary>
/// Per-address failure limiter: <see cref="MaxFailures"/> failed pairing/auth attempts inside
/// <see cref="Window"/> block that address for <see cref="BlockDuration"/>.
/// </summary>
public sealed class AttemptLimiter(TimeProvider time)
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan BlockDuration = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<IPAddress, Entry> entries = new();

    public bool IsBlocked(IPAddress? address)
    {
        if (address is null || !entries.TryGetValue(address, out var entry)) return false;
        lock (entry) return entry.BlockedUntil is { } until && time.GetUtcNow() < until;
    }

    public void RecordFailure(IPAddress? address)
    {
        if (address is null) return;
        var entry = entries.GetOrAdd(address, _ => new Entry());
        var now = time.GetUtcNow();
        lock (entry)
        {
            if (entry.BlockedUntil is { } until && now >= until)
            {
                entry.BlockedUntil = null;
                entry.Failures.Clear();
            }

            entry.Failures.Enqueue(now);
            while (entry.Failures.Count > 0 && now - entry.Failures.Peek() > Window) entry.Failures.Dequeue();
            if (entry.Failures.Count >= MaxFailures) entry.BlockedUntil = now.Add(BlockDuration);
        }
    }

    private sealed class Entry
    {
        public Queue<DateTimeOffset> Failures { get; } = new();
        public DateTimeOffset? BlockedUntil { get; set; }
    }
}
