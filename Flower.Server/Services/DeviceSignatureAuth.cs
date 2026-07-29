using Microsoft.AspNetCore.Http;

using Flower.Services;

namespace Flower.Server.Services;

// Kestrel/Minimal-API counterpart to SyncHttpServer.VerifySelfSigned - same
// proof-of-possession check (the caller must hold the private key matching
// the public key it's offering; the fingerprint it claims must actually be
// that key's hash), verified against the offered key itself since, for a
// not-yet-paired device, there's nothing in TrustedPeerStore to look up yet.
// Used only by PairingEndpoints' pair-redeem route - every other
// Flower.Server route either needs no device identity (getCoverArt, ping) or
// uses the classic Subsonic admin token (SubsonicAuth).
public static class DeviceSignatureAuth
{
    // Returns the verified fingerprint, or null on any failure.
    public static string? VerifySelfSigned(HttpRequest request, byte[] body, NonceReplayGuard replayGuard)
    {
        var fingerprint = GetIdentityValue(request, "X-Flower-Fingerprint");
        var publicKeyBase64 = GetIdentityValue(request, "X-Flower-PublicKey");
        if (string.IsNullOrEmpty(fingerprint) || string.IsNullOrEmpty(publicKeyBase64))
            return null;

        byte[] publicKeyRaw;
        try
        {
            publicKeyRaw = Convert.FromBase64String(publicKeyBase64);
        }
        catch (FormatException)
        {
            return null;
        }
        if (publicKeyRaw.Length != 65 || publicKeyRaw[0] != 0x04)
            return null;
        if (SignedRequestCanonicalizer.ComputeFingerprint(publicKeyRaw) != fingerprint)
            return null;

        var verified = SignatureVerifier.Verify(
            request.Method, request.Path.Value ?? "/", GetQueryParams(request), body,
            GetIdentityValue(request, "X-Flower-Timestamp"), GetIdentityValue(request, "X-Flower-Nonce"),
            GetIdentityValue(request, "X-Flower-Signature"), publicKeyBase64,
            DateTimeOffset.UtcNow, replayGuard, fingerprint);

        return verified ? fingerprint : null;
    }

    // Header if present, else the same name as a query param - mirrors
    // SyncHttpServer.GetIdentityValue's fallback for a URL-only caller.
    public static string? GetIdentityValue(HttpRequest request, string name) =>
        request.Headers.TryGetValue(name, out var header) && header.Count > 0 && !string.IsNullOrEmpty(header[0])
            ? header[0]
            : request.Query.TryGetValue(name, out var query) && query.Count > 0
                ? query[0]
                : null;

    private static IEnumerable<(string Key, string Value)> GetQueryParams(HttpRequest request)
    {
        foreach (var (key, values) in request.Query)
        {
            foreach (var value in values)
            {
                if (value != null)
                    yield return (key, value);
            }
        }
    }
}
