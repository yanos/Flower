using System.Collections.Concurrent;
using System.Security.Cryptography;

using Microsoft.Extensions.Options;

using Flower.Server.Configuration;

namespace Flower.Server.Services;

// v1 admin/browser session auth (SYNC-PLAN.md's "Security hardening for a
// server that's no longer LAN-only" - the browser UI can't hold a device
// signing key the way a paired peer can, so it needs its own login flow).
// Deliberately a bearer token, not a cookie: a token that only ever lives in
// the SPA's memory has no ambient-credential CSRF surface to defend against,
// so there's nothing here to layer CSRF mitigations onto.
public sealed class AdminAuthService(IOptions<FlowerServerOptions> options)
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _tokens = new();

    public bool ValidateCredentials(string? username, string? password)
    {
        var opts = options.Value;
        return !string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password)
            && FixedTimeEquals(username, opts.AdminUsername)
            && FixedTimeEquals(password, opts.AdminPassword);
    }

    public string IssueToken()
    {
        Prune();
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        _tokens[token] = DateTimeOffset.UtcNow + TokenLifetime;
        return token;
    }

    public bool ValidateToken(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return false;
        return _tokens.TryGetValue(token, out var expiresAt) && expiresAt > DateTimeOffset.UtcNow;
    }

    private void Prune()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (token, expiresAt) in _tokens)
        {
            if (expiresAt <= now)
                _tokens.TryRemove(token, out _);
        }
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));
}
