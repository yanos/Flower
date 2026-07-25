using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace Flower.Services;

// Client-side signer for peer-to-peer sync requests - wraps this device's
// own DeviceKeyStore-persisted keypair, one instance per process (see
// App.axaml.cs). A fresh timestamp+nonce is computed on every Sign() call,
// never cached/reused: SignatureVerifier's NonceReplayGuard on the
// receiving end treats a repeated nonce as a replay attempt, so reusing one
// across calls would make the second of any two identical-looking requests
// fail.
public sealed class DeviceSigningKey : IDisposable
{
    private readonly ECDsa _ecdsa;

    public string Fingerprint { get; }
    public string PublicKeyBase64 { get; }

    public DeviceSigningKey(ECDsa ecdsa, byte[] publicKeyRaw)
    {
        _ecdsa = ecdsa;
        PublicKeyBase64 = Convert.ToBase64String(publicKeyRaw);
        Fingerprint = SignedRequestCanonicalizer.ComputeFingerprint(publicKeyRaw);
    }

    public (string Signature, string Timestamp, string Nonce) Sign(
        string method, string absolutePath, IEnumerable<(string Key, string Value)> query, byte[] body)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var toSign = SignedRequestCanonicalizer.Build(method, absolutePath, query, body, timestamp, nonce);
        var signature = _ecdsa.SignData(toSign, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return (Convert.ToBase64String(signature), timestamp, nonce);
    }

    public void Dispose() => _ecdsa.Dispose();
}
