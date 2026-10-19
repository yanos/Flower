using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Flower.Server.Services;

// Admin-issued one-time pairing codes (SYNC-PLAN.md's "Passwordless by design"
// section, path A) - proactive replacement for the reactive 60-second
// live-approval prompt the app-to-app flow used, which has no one to answer it
// on a headless box. Deliberately in-memory, not persisted to the database at all: a code
// only needs to survive its own ~10-minute expiry window, and losing all
// outstanding codes on a server restart (which also drops any in-flight
// pairing attempt) is an acceptable, easily-retried cost for not needing a
// migration for genuinely ephemeral state.
//
// One code type serves every surface. A phone, a desktop and a browser tab all
// redeem the same way; GrantsAdmin only decides what the resulting TrustedPeer
// is allowed to do afterwards, and is fixed when the code is *issued* rather
// than claimed by the redeemer - otherwise any device holding an ordinary code
// could simply ask for administrative rights.
public sealed class PairingCodeService
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O/1/I - avoids operator transcription errors
    // Five characters over the alphabet above is ~33.5 million codes, and a
    // code lives ten minutes. That keyspace is not what makes guessing
    // hopeless, and neither on its own is PairingEndpoints' per-source cap of
    // five a minute: a source is an IPv6 /64, and a /48 - free from a tunnel
    // broker - is 65,536 of them, ~3.3 million guesses inside one code's
    // lifetime, a one-in-ten shot. What does it is MaxWrongCodes below, which
    // counts wrong codes from everyone at once. What five characters buy is
    // the thing a code is actually for: being read aloud down a phone line and
    // typed by someone holding a handset, in one breath and without a grouping
    // separator.
    private const int CodeLength = 5;
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);

    // How many wrong codes, from any source, within one code lifetime, before
    // every outstanding code is cancelled. A person mistypes a code once or
    // twice; twenty in ten minutes is somebody guessing, and past it no
    // outstanding code faces more than twenty guesses - a one-in-1.7-million
    // shot however many addresses they come from. The price is that someone
    // guessing can keep codes from being redeemed for as long as they keep
    // at it, which is loud (see PairingEndpoints' warning) and costs nothing
    // but a fresh code once they stop.
    public const int MaxWrongCodes = 20;

    private readonly ConcurrentDictionary<string, Entry> _codes = new();

    private readonly object _wrongCodesLock = new();
    private readonly Queue<DateTimeOffset> _wrongCodes = new();

    private sealed record Entry(DateTimeOffset ExpiresAt, bool GrantsAdmin, bool Consumed);

    public (string Code, DateTimeOffset ExpiresAt) GenerateCode(bool grantsAdmin = false)
    {
        Prune();
        var code = GenerateRandomCode();
        var expiresAt = DateTimeOffset.UtcNow + CodeLifetime;
        _codes[code] = new Entry(expiresAt, grantsAdmin, Consumed: false);
        return (code, expiresAt);
    }

    // Single-use: a code that fails the redeem handshake for any other
    // reason (bad signature, fingerprint mismatch) is left unconsumed so a
    // legitimate device can retry within the same expiry window - only a
    // structurally successful redemption burns it.
    //
    // Returns whether the code was consumed, and (via grantsAdmin) what it was
    // issued to confer. The two travel together on purpose: a caller that
    // learned a code is valid must not have to make a second, separately
    // fallible lookup to find out what it authorizes.
    public bool TryConsume(string? code, out bool grantsAdmin) =>
        TryConsume(code, DateTimeOffset.UtcNow, out grantsAdmin, out _);

    // cancelled is set only by the wrong code that reached MaxWrongCodes: how
    // many outstanding codes that cancelled, which may be none. Null on every
    // other call.
    public bool TryConsume(string? code, DateTimeOffset now, out bool grantsAdmin, out int? cancelled)
    {
        grantsAdmin = false;
        cancelled = null;
        if (string.IsNullOrEmpty(code))
            return false;

        var normalized = Normalize(code);
        if (!_codes.TryGetValue(normalized, out var entry)
            || entry.Consumed || entry.ExpiresAt <= now
            || !_codes.TryUpdate(normalized, entry with { Consumed = true }, entry))
        {
            cancelled = CountWrongCode(now);
            return false;
        }

        grantsAdmin = entry.GrantsAdmin;
        return true;
    }

    private int? CountWrongCode(DateTimeOffset now)
    {
        lock (_wrongCodesLock)
        {
            while (_wrongCodes.Count > 0 && now - _wrongCodes.Peek() >= CodeLifetime)
                _wrongCodes.Dequeue();

            _wrongCodes.Enqueue(now);
            if (_wrongCodes.Count < MaxWrongCodes)
                return null;

            // Counted afresh from here: the codes that were guessed at are
            // gone, and a code issued after this faces a budget of its own.
            _wrongCodes.Clear();
            var cancelled = 0;
            foreach (var (outstanding, outstandingEntry) in _codes)
            {
                if (!outstandingEntry.Consumed && outstandingEntry.ExpiresAt > now && _codes.TryRemove(outstanding, out _))
                    cancelled++;
            }
            return cancelled;
        }
    }

    // Codes are dictated over the phone and typed by hand as often as they are
    // scanned, so accept the shapes that produces: any casing, surrounding
    // whitespace, and the grouping separators a user copies from the dash-
    // formatted rendering on the admin screen.
    private static string Normalize(string code) =>
        code.Trim().Replace("-", "").Replace(" ", "").ToUpperInvariant();

    private void Prune()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (code, entry) in _codes)
        {
            if (entry.ExpiresAt <= now)
                _codes.TryRemove(code, out _);
        }
    }

    private static string GenerateRandomCode() =>
        RandomNumberGenerator.GetString(Alphabet, CodeLength);
}
