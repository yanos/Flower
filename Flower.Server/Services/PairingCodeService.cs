using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Flower.Server.Services;

// Admin-issued one-time pairing codes (SYNC-PLAN.md's "Pairing redesign"
// section) - proactive replacement for SyncHttpServer's reactive 60-second
// live-approval prompt, which has no one to answer it on a headless box.
// Deliberately in-memory, not persisted to the database at all: a code only
// needs to survive its own ~10-minute expiry window, and losing all
// outstanding codes on a server restart (which also drops any in-flight
// pairing attempt) is an acceptable, easily-retried cost for not needing a
// migration for genuinely ephemeral state.
public sealed class PairingCodeService
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O/1/I - avoids operator transcription errors
    private const int CodeLength = 8;
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, (DateTimeOffset ExpiresAt, bool Consumed)> _codes = new();

    public (string Code, DateTimeOffset ExpiresAt) GenerateCode()
    {
        Prune();
        var code = GenerateRandomCode();
        var expiresAt = DateTimeOffset.UtcNow + CodeLifetime;
        _codes[code] = (expiresAt, false);
        return (code, expiresAt);
    }

    // Single-use: a code that fails the redeem handshake for any other
    // reason (bad signature, fingerprint mismatch) is left unconsumed so a
    // legitimate device can retry within the same expiry window - only a
    // structurally successful redemption burns it.
    public bool TryConsume(string? code)
    {
        if (string.IsNullOrEmpty(code))
            return false;

        var normalized = code.Trim().ToUpperInvariant();
        if (!_codes.TryGetValue(normalized, out var entry))
            return false;
        if (entry.Consumed || entry.ExpiresAt <= DateTimeOffset.UtcNow)
            return false;

        return _codes.TryUpdate(normalized, (entry.ExpiresAt, true), entry);
    }

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
        string.Create(CodeLength, RandomNumberGenerator.GetBytes(CodeLength), (span, bytes) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = Alphabet[bytes[i] % Alphabet.Length];
        });
}
