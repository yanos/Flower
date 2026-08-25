using System.Collections.Concurrent;

namespace Flower.Server.Services;

// Keeps a refused caller from filling the log with its own refusals.
//
// Every rejection this server writes - a bad signature, an unknown fingerprint,
// a rate-limited pairing attempt - is triggered by someone else's request, and
// the callers most worth logging are exactly the ones that repeat: a scanner
// working through a port, a client stuck in a retry loop, somebody guessing
// pairing codes. Logging each one produces hundreds of identical lines, which
// pushes everything else out of the 2000-entry in-memory buffer the Logs tab
// and the client log push both read from (see InMemoryLogStore). The flood then
// costs the operator the very information it was supposed to give them.
//
// So the first refusal of a kind from a source is written immediately - a
// refusal nobody sees is the problem this whole pass exists to fix - and the
// repeats within the window collapse into a count carried by the next line.
// Same argument as ProxyHeaderAudit.ShouldWarn, which throttles the one other
// request-triggered warning in this server; this one is per source and per
// kind, because "device A's clock is wrong" and "someone is scanning" are
// different findings and must not silence each other.
public sealed class RefusalLogThrottle(TimeSpan? repeatInterval = null)
{
    // Long enough that a determined caller cannot flood, short enough that a
    // person retrying by hand while watching the log still sees each attempt.
    public static readonly TimeSpan DefaultRepeatInterval = TimeSpan.FromMinutes(1);

    private readonly TimeSpan _interval = repeatInterval ?? DefaultRepeatInterval;
    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    private sealed class Entry
    {
        public DateTimeOffset NextAllowed;
        public int Suppressed;
    }

    // Whether to write this refusal, and how many were swallowed since the last
    // one that was written. suppressed is 0 on a first occurrence, so a caller
    // can append "(and N more)" only when there is something to append.
    public bool ShouldLog(string key, DateTimeOffset now, out int suppressed)
    {
        var entry = _entries.GetOrAdd(key, _ => new Entry());
        lock (entry)
        {
            if (now < entry.NextAllowed)
            {
                entry.Suppressed++;
                suppressed = 0;
                return false;
            }

            suppressed = entry.Suppressed;
            entry.Suppressed = 0;
            entry.NextAllowed = now + _interval;
            return true;
        }
    }

    // Bounded cleanup, called from the same paths that log: without it a
    // long-running server accumulates one entry per address that ever knocked,
    // which is unbounded and attacker-driven. Anything past its window is
    // already going to log again on sight, so dropping it changes nothing.
    public void Prune(DateTimeOffset now)
    {
        foreach (var (key, entry) in _entries)
        {
            lock (entry)
            {
                if (now < entry.NextAllowed || entry.Suppressed != 0)
                    continue;
            }

            _entries.TryRemove(key, out _);
        }
    }

    public int TrackedSources => _entries.Count;
}
