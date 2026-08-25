using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using Flower.Persistence;
using Flower.Services;

namespace Flower.Server.Services;

// Kestrel/Minimal-API adapter onto the shared PeerSignatureAuth - the check
// itself (proof-of-possession, and the header-else-query rule for where the
// identity may be read from) lives in Flower.Core, extracted when the app had a
// listener of its own for this to drift from. All that is left here is turning
// an HttpRequest into a SignedRequest.
//
// Two callers, one mechanism: PairingEndpoints' pair-redeem uses the
// self-signed form (there is nothing to look up yet for a device that has not
// paired), and everything under /api/admin uses the trusted-peer form. There
// is no third admin-only authentication scheme - see SYNC-PLAN.md's
// "Passwordless by design": the browser holds a non-extractable WebCrypto
// keypair and signs like any other device, so the admin API is gated by the
// same signature check as the rest, plus a capability flag on the peer.
public static class DeviceSignatureAuth
{
    // Returns the verified fingerprint, or null on any failure.
    //
    // The optional logger is how a refusal stops being invisible. Every caller
    // of these three methods answers a failure with a bare 401/403, so without
    // a line written here there is no record anywhere that anything was ever
    // turned away - which makes "my phone stopped syncing" and "something is
    // knocking on this port" the same observation: silence. See LogRefusal for
    // the level policy.
    public static string? VerifySelfSigned(
        HttpRequest request, byte[] body, NonceReplayGuard replayGuard, ILogger? logger = null)
    {
        var fingerprint = PeerSignatureAuth.VerifySelfSigned(
            ToSignedRequest(request, body), replayGuard, DateTimeOffset.UtcNow);

        if (fingerprint == null && logger != null
            && SelfSignedThrottle.ShouldLog(RemoteAddress(request), DateTimeOffset.UtcNow, out _))
        {
            // Always Debug, never Warning: this is the pairing path, so every
            // caller is by definition someone this server has no relationship
            // with yet. A failure here is an unpaired stranger - a mistyped
            // code, or a scanner - and not evidence of anything wrong.
            logger.LogDebug(
                "Refused a self-signed request to {Path} from {RemoteAddress} claiming {Fingerprint}: "
                + "the signature did not verify against the public key it offered.",
                request.Path.Value, RemoteAddress(request), Claimed(request));
        }

        return fingerprint;
    }

    // The gated form: verified against the public key captured when this
    // fingerprint was approved, never against a key offered on the request.
    public static string? VerifyTrustedPeer(
        HttpRequest request, byte[] body, TrustedPeerStore trustedPeers, NonceReplayGuard replayGuard,
        ILogger? logger = null) =>
        AuthenticateTrustedPeer(request, body, trustedPeers, replayGuard, logger).Fingerprint;

    // The same check, keeping "I don't know you" and "that signature didn't
    // check out" apart so the two can be answered differently - see
    // PeerSignatureAuth.AuthenticateTrustedPeer.
    public static PeerAuthResult AuthenticateTrustedPeer(
        HttpRequest request, byte[] body, TrustedPeerStore trustedPeers, NonceReplayGuard replayGuard,
        ILogger? logger = null)
    {
        var result = PeerSignatureAuth.AuthenticateTrustedPeer(
            ToSignedRequest(request, body), trustedPeers.GetPublicKey, replayGuard, DateTimeOffset.UtcNow);

        if (result.Failure != PeerAuthFailure.None)
            LogRefusal(request, result.Failure, logger);

        return result;
    }

    // The level split, and the reason for it: BadSignature means this server
    // *does* hold a key for the caller, so a device that paired successfully is
    // now failing - clock skew, a replayed nonce, a half-finished revocation.
    // Somebody legitimate is being turned away, which is a Warning. NotTrusted
    // means no key on file at all, which on a port reachable from anywhere is
    // ordinary background noise, so it is Debug for the same reason the
    // LanGuard drop in Program.cs is: an operator chasing "it won't connect"
    // can find it, without a port scan filling the Logs tab.
    //
    // The fingerprint logged is the one *claimed*, never a verified one -
    // there isn't a verified one here, that is the point - so it is attacker-
    // controlled and must be read as such.
    private static void LogRefusal(HttpRequest request, PeerAuthFailure failure, ILogger? logger)
    {
        if (logger == null)
            return;

        // Throttled per source and per failure kind - a refused caller is
        // usually a repeating one, and without this the repeats bury everything
        // else in the log. See RefusalLogThrottle.
        var address = RemoteAddress(request);
        var now = DateTimeOffset.UtcNow;
        if (!Throttle.ShouldLog($"{failure}|{address}", now, out var suppressed))
            return;
        Throttle.Prune(now);

        var alsoSuppressed = suppressed == 0 ? "" : $" ({suppressed} more since the last such refusal.)";

        if (failure == PeerAuthFailure.BadSignature)
        {
            logger.LogWarning(
                "Refused {Method} {Path} from {RemoteAddress}: {Fingerprint} is a trusted peer, but the request's "
                + "signature did not verify - a stale timestamp, a replayed nonce, or a key that no longer matches."
                + "{AlsoSuppressed}",
                request.Method, request.Path.Value, address, Claimed(request), alsoSuppressed);
        }
        else
        {
            logger.LogDebug(
                "Refused {Method} {Path} from {RemoteAddress}: no public key on file for the claimed "
                + "fingerprint {Fingerprint} (never paired, or revoked since).{AlsoSuppressed}",
                request.Method, request.Path.Value, address, Claimed(request), alsoSuppressed);
        }
    }

    private static readonly RefusalLogThrottle Throttle = new();

    // The self-signed path has its own bucket: it is the pairing route, where
    // a burst of failures means something different than it does on a gated one.
    private static readonly RefusalLogThrottle SelfSignedThrottle = new();

    // Whatever the caller says it is, for the log line only. Null becomes a
    // placeholder rather than an empty field so "claimed nothing at all" and
    // "claimed something unknown" read differently.
    private static string Claimed(HttpRequest request) =>
        ToSignedRequest(request, []).Identity("X-Flower-Fingerprint") is { Length: > 0 } claimed
            ? claimed
            : "(no fingerprint)";

    private static string RemoteAddress(HttpRequest request) =>
        request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "(unknown)";

    // For the values a handler wants *after* the request is verified (pairing
    // code, alias). Same header-else-query rule, because it is the same rule.
    public static string? GetIdentityValue(HttpRequest request, string name) =>
        ToSignedRequest(request, []).Identity(name);

    private static SignedRequest ToSignedRequest(HttpRequest request, byte[] body)
    {
        var query = new List<(string Key, string Value)>();
        foreach (var (key, values) in request.Query)
        {
            foreach (var value in values)
            {
                if (value != null)
                    query.Add((key, value));
            }
        }

        return new SignedRequest(
            request.Method, request.Path.Value ?? "/", query, body,
            name => request.Headers.TryGetValue(name, out var header) && header.Count > 0 ? header[0] : null);
    }
}
