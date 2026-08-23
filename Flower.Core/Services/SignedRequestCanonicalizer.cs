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
    // Every X-Flower-* transport parameter - the signature itself, and the
    // identity it is signed under - is excluded from the canonical query, so
    // the canonical form is identical whether those values travel as headers
    // (OpenSubsonicClient.SendAsync, SyncHttpServer's own API calls) or as
    // query-string fallbacks alongside everything else in the URL (the
    // LibVLC/OpenSubsonicClient.BuildUrl case - see SignedRequest.Identity).
    //
    // Only the three signature params used to be excluded, which made the two
    // transports sign *different* strings: a caller signs its identity params
    // (see PeerOpenSubsonicClientFactory) either way, but a server only sees
    // them in the query when they were sent in the query, so every
    // header-authenticated peer call verified against a canonical query the
    // caller had never signed and was rejected. Nothing is weakened by
    // dropping them: an unsigned X-Flower-Fingerprint only selects which
    // trusted key the signature is checked against (VerifyTrustedPeer), and
    // an unsigned X-Flower-PublicKey is checked against the fingerprint it
    // hashes to (VerifySelfSigned) - swapping either just fails the check.
    private static bool IsTransportParam(string key) =>
        key.StartsWith("X-Flower-", StringComparison.OrdinalIgnoreCase);

    public static byte[] Build(
        string method, string absolutePath, IEnumerable<(string Key, string Value)> query, byte[] body,
        string timestamp, string nonce)
    {
        // Percent-encoded before joining, so a value can never imitate the
        // separators. Unencoded, `?a=1&b=2` and `?a=1%26b%3D2` both canonicalize
        // to "a=1&b=2" - two different requests, one signature, which is a
        // signature that does not say what it appears to say. Reaching it needs
        // an attacker positioned to rewrite a request before it is delivered
        // (the nonce guard covers one already delivered), so plain HTTP behind a
        // TLS-terminating proxy rather than WireGuard - narrow, and cheaper to
        // close than to keep reasoning about. See docs/OPEN-INTERNET-REVIEW.md.
        //
        // Sorted on the *encoded* form, and by value as well as key: two values
        // under one key otherwise rely on both ends flattening them in the same
        // arrival order, which nothing actually guarantees.
        var canonicalQuery = string.Join("&",
            query.Where(p => !IsTransportParam(p.Key))
                 .Select(p => (Key: Uri.EscapeDataString(p.Key), Value: Uri.EscapeDataString(p.Value)))
                 .OrderBy(p => p.Key, StringComparer.Ordinal)
                 .ThenBy(p => p.Value, StringComparer.Ordinal)
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
