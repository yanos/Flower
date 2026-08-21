using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Flower.Server.Services;

// Short-lived admin sessions for the browser settings page, minted by a device
// that is already an admin.
//
// Every other caller of /api/admin signs its requests with a device keypair
// (DeviceSignatureAuth). The browser cannot: .NET-for-WebAssembly has no
// asymmetric crypto at all - ECDsa.Create() throws PlatformNotSupportedException
// for every curve there, which is why App.axaml.cs skips registering
// DeviceSigningKey (and everything downstream of it) when OperatingSystem.
// IsBrowser(). SYNC-PLAN.md's "the browser is a device" design answers that with
// a non-extractable WebCrypto keypair in IndexedDB, which is a real initiative of
// its own and not built yet.
//
// So the browser's authority is *derived* rather than independent: an already-
// trusted admin device (the desktop client's "Server Settings..." button, or the
// server's own console at first run) mints one of these and hands it to the
// browser in the URL fragment. That keeps one trust root - a browser tab can only
// ever administer what some admin device could already administer - and does not
// close off giving the browser its own key later.
//
// Deliberately shaped like StreamTicketService, for the same reason and with the
// same three narrowings: it is a bearer token by necessity, so it expires in
// minutes, records who minted it, and dies with them (see RevokeFor, called from
// the device-revoke route). LanGuard keeps it unusable from off the LAN even in
// the window where it is live.
public sealed class AdminSessionService
{
    // Long enough to actually change some settings without being logged out
    // mid-edit, short enough that a URL left in a chat window or a shell history
    // is worthless by the time anyone finds it. Re-minting is one click on the
    // client, so there is no reason to stretch this.
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(60);

    // The identity recorded for a session the server minted for its own console
    // at first run, when by definition no admin device exists yet to mint one.
    // Not a fingerprint of anything - it can never match a TrustedPeer, which is
    // what stops RevokeFor from ever touching it and what makes IsAdmin below
    // answer for it directly rather than through the trust store.
    public const string ConsoleFingerprint = "console";

    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    private sealed record Session(string Fingerprint, DateTimeOffset ExpiresAt);

    public (string Token, DateTimeOffset ExpiresAt) Issue(string fingerprint)
    {
        Prune();
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var expiresAt = DateTimeOffset.UtcNow + SessionLifetime;
        _sessions[token] = new Session(fingerprint, expiresAt);
        return (token, expiresAt);
    }

    // Returns the fingerprint the session was minted for, or null. The caller
    // still has to decide whether that fingerprint may do what it is asking -
    // this only says who is asking (see AdminEndpoints' filter, which re-checks
    // TrustedPeerStore.IsAdmin on every request so revoking a device's admin
    // flag takes effect immediately rather than at session expiry).
    public string? Resolve(string? token, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(token))
            return null;
        if (!_sessions.TryGetValue(token, out var session))
            return null;
        if (session.ExpiresAt <= now)
            return null;

        return session.Fingerprint;
    }

    public int RevokeFor(string fingerprint)
    {
        var revoked = 0;
        foreach (var (token, session) in _sessions)
        {
            if (session.Fingerprint == fingerprint && _sessions.TryRemove(token, out _))
                revoked++;
        }
        return revoked;
    }

    private void Prune()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (token, session) in _sessions)
        {
            if (session.ExpiresAt <= now)
                _sessions.TryRemove(token, out _);
        }
    }
}
