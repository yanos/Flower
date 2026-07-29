using System;
using System.Collections.Concurrent;

namespace Flower.Services;

// Fixed-window request counter - deliberately simple (this codebase has zero
// rate limiting today, so a blunt fixed window is already a large
// improvement) rather than a token bucket, which isn't worth the extra
// state for LAN-scale traffic (a handful of peers, sync sessions triggered
// by discovery events). See SyncHttpServer for the four category instances
// and their limits/keying.
public sealed class RateLimiter
{
    private readonly int _max;
    private readonly TimeSpan _window;
    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset WindowStart)> _windows = new();

    public RateLimiter(int max, TimeSpan window)
    {
        _max = max;
        _window = window;
    }

    public bool TryAcquire(string key, DateTimeOffset now)
    {
        while (true)
        {
            if (!_windows.TryGetValue(key, out var existing) || now - existing.WindowStart >= _window)
            {
                var fresh = (Count: 1, WindowStart: now);
                if (existing == default && _windows.TryAdd(key, fresh))
                    return true;
                if (existing != default && _windows.TryUpdate(key, fresh, existing))
                    return true;
                continue; // Another thread raced this key - retry against its result.
            }

            var updated = (existing.Count + 1, existing.WindowStart);
            if (!_windows.TryUpdate(key, updated, existing))
                continue; // Lost a race with a concurrent request for the same key - retry.
            return updated.Item1 <= _max;
        }
    }
}
