using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Flower.Server.Services;

// Admin-issued one-time pairing codes (SYNC-PLAN.md's "Passwordless by design"
// section, path A) - proactive replacement for SyncHttpServer's reactive
// 60-second live-approval prompt, which has no one to answer it on a headless
// box. Deliberately in-memory, not persisted to the database at all: a code
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
    private const int CodeLength = 8;
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, Entry> _codes = new();

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
    public bool TryConsume(string? code, out bool grantsAdmin)
    {
        grantsAdmin = false;
        if (string.IsNullOrEmpty(code))
            return false;

        var normalized = Normalize(code);
        if (!_codes.TryGetValue(normalized, out var entry))
            return false;
        if (entry.Consumed || entry.ExpiresAt <= DateTimeOffset.UtcNow)
            return false;

        if (!_codes.TryUpdate(normalized, entry with { Consumed = true }, entry))
            return false;

        grantsAdmin = entry.GrantsAdmin;
        return true;
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
