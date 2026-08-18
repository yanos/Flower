using Microsoft.AspNetCore.Http;

using Flower.Services;

namespace Flower.Server.Services;

// Kestrel/Minimal-API adapter onto the shared PeerSignatureAuth - the check
// itself (proof-of-possession, and the header-else-query rule for where the
// identity may be read from) lives in Flower.Core so it cannot drift from
// SyncHttpServer's copy, which is what it used to be. All that is left here
// is turning an HttpRequest into a SignedRequest.
//
// Used only by PairingEndpoints' pair-redeem route - every other
// Flower.Server route either needs no device identity (getCoverArt, ping) or
// uses the classic Subsonic admin token (SubsonicAuth).
public static class DeviceSignatureAuth
{
    // Returns the verified fingerprint, or null on any failure.
    public static string? VerifySelfSigned(HttpRequest request, byte[] body, NonceReplayGuard replayGuard) =>
        PeerSignatureAuth.VerifySelfSigned(ToSignedRequest(request, body), replayGuard, DateTimeOffset.UtcNow);

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
