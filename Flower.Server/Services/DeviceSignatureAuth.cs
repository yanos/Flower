using Microsoft.AspNetCore.Http;

using Flower.Persistence;
using Flower.Services;

namespace Flower.Server.Services;

// Kestrel/Minimal-API adapter onto the shared PeerSignatureAuth - the check
// itself (proof-of-possession, and the header-else-query rule for where the
// identity may be read from) lives in Flower.Core so it cannot drift from
// SyncHttpServer's copy, which is what it used to be. All that is left here
// is turning an HttpRequest into a SignedRequest.
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
    public static string? VerifySelfSigned(HttpRequest request, byte[] body, NonceReplayGuard replayGuard) =>
        PeerSignatureAuth.VerifySelfSigned(ToSignedRequest(request, body), replayGuard, DateTimeOffset.UtcNow);

    // The gated form: verified against the public key captured when this
    // fingerprint was approved, never against a key offered on the request.
    public static string? VerifyTrustedPeer(HttpRequest request, byte[] body, TrustedPeerStore trustedPeers, NonceReplayGuard replayGuard) =>
        PeerSignatureAuth.VerifyTrustedPeer(
            ToSignedRequest(request, body), trustedPeers.GetPublicKey, replayGuard, DateTimeOffset.UtcNow);

    // The same check, keeping "I don't know you" and "that signature didn't
    // check out" apart so the two can be answered differently - see
    // PeerSignatureAuth.AuthenticateTrustedPeer.
    public static PeerAuthResult AuthenticateTrustedPeer(HttpRequest request, byte[] body, TrustedPeerStore trustedPeers, NonceReplayGuard replayGuard) =>
        PeerSignatureAuth.AuthenticateTrustedPeer(
            ToSignedRequest(request, body), trustedPeers.GetPublicKey, replayGuard, DateTimeOffset.UtcNow);

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
