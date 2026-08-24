using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Flower.Services;

// Sliding-window request counter - deliberately simple (a token bucket isn't
// worth the extra state for LAN-scale traffic: a handful of peers, sync
// sessions triggered by discovery events) but no longer a plain *fixed*
// window, which let a burst timed across the window boundary through at
// roughly 2x the configured ceiling: max requests in the last instant of one
// window plus max more in the first instant of the next, all inside a span
// shorter than the window itself. On the login and pairing-code-redeem
// limiters that doubling is the whole budget an attacker cares about.
//
// The standard weighted approximation: keep the previous window's count
// alongside the current one and charge a request against
// previous * (fraction of the window still overlapped) + current. Costs one
// extra int per key, needs no per-request timestamp list, and never
// over-admits the way a fixed window does. See SyncHttpServer for the five
// category instances and their limits/keying.
public sealed class RateLimiter
{
    // Idle keys are dropped after this many windows with no traffic. Keys are
    // attacker-chosen (source IP on every pre-trust endpoint), so without
    // this the dictionary is an unbounded memory sink that a spoofed-source
    // flood grows for free.
    private const int IdleWindowsBeforeEviction = 4;

    // The key every per-source limiter should use, rather than the address
    // string itself. An IPv6 caller is not one caller: an ordinary residential
    // or hosting allocation is a /64, so a full-address key hands an attacker
    // 2^64 free buckets and the per-IP ceilings bound nothing they are willing
    // to rotate addresses for. It also feeds the memory sink
    // IdleWindowsBeforeEviction exists to bound, since keys are attacker-chosen
    // and eviction runs four windows behind. Collapsing to the /64 makes the
    // budgets mean what they read as meaning. See docs/OPEN-INTERNET-REVIEW.md.
    //
    // Link-local is deliberately left at full precision. Nothing off-link can
    // source an fe80:: address, so there is no rotation to bound - while
    // collapsing it would put every device on the same link into one bucket,
    // which is exactly the LAN case these limiters have to keep working for.
    public static string KeyFor(IPAddress? address)
    {
        if (address == null)
            return "unknown";

        // Kestrel hands out ::ffff:a.b.c.d for an IPv4 client on a dual-stack
        // socket; keying that as IPv6 would put every IPv4 caller in one /64.
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (address.AddressFamily != AddressFamily.InterNetworkV6 || address.IsIPv6LinkLocal)
            return address.ToString();

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    private readonly int _max;
    private readonly TimeSpan _window;
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();
    private DateTimeOffset _nextPrune = DateTimeOffset.MinValue;
    private int _pruning;

    public RateLimiter(int max, TimeSpan window)
    {
        _max = max;
        _window = window;
    }

    // Previous/current counts for the two windows straddled by WindowStart,
    // which always marks the current window's opening edge.
    private readonly record struct Bucket(int Previous, int Current, DateTimeOffset WindowStart);

    public bool TryAcquire(string key, DateTimeOffset now)
    {
        Prune(now);

        while (true)
        {
            _buckets.TryGetValue(key, out var existing);
            var rolled = Roll(existing, now);

            var elapsed = (now - rolled.WindowStart).TotalMilliseconds;
            var overlap = 1.0 - Math.Clamp(elapsed / _window.TotalMilliseconds, 0.0, 1.0);
            var estimated = rolled.Previous * overlap + rolled.Current + 1;

            var updated = rolled with { Current = rolled.Current + 1 };
            if (existing == default)
            {
                if (!_buckets.TryAdd(key, updated))
                    continue; // Another thread created this key first - retry against its result.
            }
            else if (!_buckets.TryUpdate(key, updated, existing))
            {
                continue; // Lost a race with a concurrent request for the same key - retry.
            }

            return estimated <= _max;
        }
    }

    // How many keys are currently held. Exists so the eviction sweep below is
    // observable at all - nothing in the app reads it.
    public int TrackedKeyCount => _buckets.Count;

    // Read-only peek: is this key currently under its ceiling? For budgets
    // that are only *spent* on some outcomes but must gate every request -
    // SubsonicEndpoints charges its failed-auth limiter only when auth
    // actually fails, then locks the source out entirely while it's over
    // budget.
    public bool WouldAllow(string key, DateTimeOffset now)
    {
        if (!_buckets.TryGetValue(key, out var existing))
            return true;

        var rolled = Roll(existing, now);
        var overlap = 1.0 - Math.Clamp((now - rolled.WindowStart).TotalMilliseconds / _window.TotalMilliseconds, 0.0, 1.0);
        return rolled.Previous * overlap + rolled.Current + 1 <= _max;
    }

    // Advances a bucket to the window containing `now`: one window elapsed
    // demotes current to previous, two or more means nothing recent overlaps
    // at all and both counts reset.
    private Bucket Roll(Bucket bucket, DateTimeOffset now)
    {
        if (bucket == default)
            return new Bucket(0, 0, now);

        var elapsed = now - bucket.WindowStart;
        if (elapsed < _window)
            return bucket;
        if (elapsed < _window * 2)
            return new Bucket(bucket.Current, 0, bucket.WindowStart + _window);
        return new Bucket(0, 0, now);
    }

    // Best-effort sweep, at most once per window and never on more than one
    // thread at a time - this runs on the request path, so a missed pass just
    // means the entry survives until the next one.
    private void Prune(DateTimeOffset now)
    {
        if (now < _nextPrune || Interlocked.Exchange(ref _pruning, 1) == 1)
            return;

        try
        {
            _nextPrune = now + _window;
            var cutoff = now - _window * IdleWindowsBeforeEviction;
            foreach (var entry in _buckets)
            {
                if (entry.Value.WindowStart <= cutoff)
                    _buckets.TryRemove(new KeyValuePair<string, Bucket>(entry.Key, entry.Value));
            }
        }
        finally
        {
            Volatile.Write(ref _pruning, 0);
        }
    }
}
