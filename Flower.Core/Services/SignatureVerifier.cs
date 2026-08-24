using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace Flower.Services;

// Server-side counterpart to DeviceSigningKey - verifies a signed sync
// request against a specific caller's public key (see
// SignedRequestCanonicalizer for the shared canonical form both sides
// build). Pure/stateless except for the caller-owned NonceReplayGuard
// passed in (Flower.Server holds one, in DI).
public static class SignatureVerifier
{
    public static readonly TimeSpan ClockSkewWindow = TimeSpan.FromSeconds(60);

    public static bool Verify(
        string method, string absolutePath, IEnumerable<(string Key, string Value)> query, byte[] body,
        string? timestamp, string? nonce, string? signatureBase64, string? publicKeyBase64,
        DateTimeOffset now, NonceReplayGuard replayGuard, string fingerprint)
    {
        if (string.IsNullOrEmpty(timestamp) || string.IsNullOrEmpty(nonce) ||
            string.IsNullOrEmpty(signatureBase64) || string.IsNullOrEmpty(publicKeyBase64))
            return false;

        if (!long.TryParse(timestamp, out var unixSeconds))
            return false;
        var requestTime = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        if ((now - requestTime).Duration() > ClockSkewWindow)
            return false;

        // Recorded before the signature itself is checked - a wrong/forged
        // signature still burns the nonce, which is fine: a legitimate
        // caller always generates a fresh nonce per attempt (see
        // DeviceSigningKey.Sign), so this never blocks a genuine retry.
        if (!replayGuard.TryRecord(fingerprint, nonce, now))
            return false;

        if (!TryParsePublicKey(publicKeyBase64, out var point))
            return false;

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(signatureBase64);
        }
        catch (FormatException)
        {
            return false;
        }

        using var ecdsa = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = point });
        var toVerify = SignedRequestCanonicalizer.Build(method, absolutePath, query, body, timestamp, nonce);
        return ecdsa.VerifyData(toVerify, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    // Also used by the pair-redeem route to check
    // "fingerprint == hash(offered public key)" before trusting a self-signed
    // proof-of-possession request.
    public static bool TryParsePublicKey(string publicKeyBase64, out ECPoint point)
    {
        point = default;
        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(publicKeyBase64);
        }
        catch (FormatException)
        {
            return false;
        }

        if (raw.Length != 65 || raw[0] != 0x04)
            return false;

        point = new ECPoint { X = raw[1..33], Y = raw[33..65] };
        return true;
    }
}
