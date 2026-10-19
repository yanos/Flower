using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Flower.Services;

// Bounds replay of a captured signed request (see SignatureVerifier) to a
// narrow window - a valid signature alone proves possession of the signing
// key, not that the request is fresh, so without this a sniffed-and-
// replayed request would keep verifying successfully forever. Entries are
// retained a bit past SignatureVerifier's own timestamp validity window,
// purely to bound memory - a pruned nonce isn't "safe to reuse again," its
// timestamp will already be rejected as stale by then regardless.
//
// The nonce is recorded before the signature is checked (see
// SignatureVerifier.Check), so this is a dictionary a caller who cannot sign
// writes to, under keys it chooses - the hazard RateLimiter next door is
// written around, and this is written around it the same way. Each key is
// bounded (MaxFingerprintLength, MaxNonceLength, checked by the verifier
// before anything is recorded), and the expiry sweep runs at most once per
// PruneEvery rather than on every insert, which used to make each insert cost
// a walk of everything already held. How many entries there can be is then the
// request budgets' business (RequestGate): at most what they let through in one
// retention window, plus one sweep interval. There is no ceiling
// past which new nonces are refused, deliberately - a full guard would refuse
// every honest device along with whoever filled it.
public sealed class NonceReplayGuard
{
    private static readonly TimeSpan RetainFor = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan PruneEvery = TimeSpan.FromSeconds(15);

    // Both are 32 hex characters on the honest path (SignedRequestCanonicalizer.
    // ComputeFingerprint, and the 16 random bytes DeviceSigningKey.Sign and the
    // browser's signer draw), so twice that is room, not a constraint.
    public const int MaxFingerprintLength = 64;
    public const int MaxNonceLength = 64;

    // Keyed by fingerprint+nonce, not nonce alone - two honest peers could
    // coincidentally generate the same random nonce, and that must not
    // collide with each other's replay tracking.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new();
    private DateTimeOffset _nextPrune = DateTimeOffset.MinValue;
    private int _pruning;

    // How many nonces are held, so the sweep is observable to a test.
    public int Count => _seen.Count;

    public bool TryRecord(string fingerprint, string nonce, DateTimeOffset now)
    {
        // The verifier refuses these first and says why; this is the backstop
        // for any other caller, and an over-long key is never stored.
        if (fingerprint.Length > MaxFingerprintLength || nonce.Length > MaxNonceLength)
            return false;

        Prune(now);
        return _seen.TryAdd($"{fingerprint}:{nonce}", now);
    }

    // Best-effort and single-threaded, as RateLimiter.Prune is: a missed pass
    // only means an expired entry lives until the next one.
    private void Prune(DateTimeOffset now)
    {
        if (now < _nextPrune || Interlocked.Exchange(ref _pruning, 1) == 1)
            return;

        try
        {
            _nextPrune = now + PruneEvery;
            foreach (var (key, seenAt) in _seen)
            {
                if (now - seenAt > RetainFor)
                    _seen.TryRemove(key, out _);
            }
        }
        finally
        {
            Volatile.Write(ref _pruning, 0);
        }
    }
}
