using System;
using System.Collections.Concurrent;

namespace Flower.Services;

// Bounds replay of a captured signed request (see SignatureVerifier) to a
// narrow window - a valid signature alone proves possession of the signing
// key, not that the request is fresh, so without this a sniffed-and-
// replayed request would keep verifying successfully forever. Entries are
// retained a bit past SignatureVerifier's own timestamp validity window,
// purely to bound memory - a pruned nonce isn't "safe to reuse again," its
// timestamp will already be rejected as stale by then regardless.
public sealed class NonceReplayGuard
{
    private static readonly TimeSpan RetainFor = TimeSpan.FromSeconds(120);

    // Keyed by fingerprint+nonce, not nonce alone - two honest peers could
    // coincidentally generate the same random nonce, and that must not
    // collide with each other's replay tracking.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new();

    public bool TryRecord(string fingerprint, string nonce, DateTimeOffset now)
    {
        Prune(now);
        return _seen.TryAdd($"{fingerprint}:{nonce}", now);
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var (key, seenAt) in _seen)
        {
            if (now - seenAt > RetainFor)
                _seen.TryRemove(key, out _);
        }
    }
}
