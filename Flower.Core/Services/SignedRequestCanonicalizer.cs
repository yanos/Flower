using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Flower.Services;

// Single source of truth for the exact bytes a signed peer-to-peer sync
// request signs/verifies over (see DeviceSigningKey/SignatureVerifier) -
// client and server both build this the same way so they can never drift
// apart. Deliberately covers method + path + query + body + timestamp +
// nonce: without the body hash, a captured signed POST could be replayed
// with a different (still-valid-looking) body; without the query, a
// captured signed GET could be replayed against a different id/parameter.
public static class SignedRequestCanonicalizer
{
    // The transport parameters that carry the signature itself never sign
    // themselves - excluded from the canonical query so this works
    // identically whether they travel as headers or (the LibVLC/
    // OpenSubsonicClient.BuildUrl case - see SyncHttpServer.GetIdentityValue)
    // as query-string fallbacks alongside everything else in the URL.
    private static readonly HashSet<string> TransportParams = new(StringComparer.OrdinalIgnoreCase)
    {
        "X-Flower-Signature", "X-Flower-Timestamp", "X-Flower-Nonce",
    };

    public static byte[] Build(
        string method, string absolutePath, IEnumerable<(string Key, string Value)> query, byte[] body,
        string timestamp, string nonce)
    {
        var canonicalQuery = string.Join("&",
            query.Where(p => !TransportParams.Contains(p.Key))
                 .OrderBy(p => p.Key, StringComparer.Ordinal)
                 .Select(p => $"{p.Key}={p.Value}"));
        var bodyHash = Convert.ToHexStringLower(SHA256.HashData(body));

        var toSign = string.Join("\n", method, absolutePath, canonicalQuery, bodyHash, timestamp, nonce);
        return Encoding.UTF8.GetBytes(toSign);
    }

    // Truncated to 16 bytes / 32 hex chars - deliberately the same shape/
    // length as the GUID this replaces (Guid.NewGuid("N")) so every display/
    // logging/JSON call site keeps working unchanged. The truncation is a
    // display-continuity choice, not a security requirement: what actually
    // stops spoofing is that a request must be signed by the private key
    // matching whatever public key was captured for this fingerprint at
    // pairing time (see TrustedPeerStore/SignatureVerifier), not the
    // fingerprint string's length.
    public static string ComputeFingerprint(byte[] publicKeyRaw) =>
        Convert.ToHexStringLower(SHA256.HashData(publicKeyRaw))[..32];
}
