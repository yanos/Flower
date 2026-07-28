using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.Extensions.Logging;

namespace Flower.Persistence
{
    public sealed record DeviceKeyMaterial(string Algorithm, string PrivateKeyPkcs8Base64, string PublicKeyBase64);

    // Persists this device's signing keypair - the actual cryptographic
    // identity backing DeviceIdentity.Fingerprint, which is derived from the
    // public key here (see Services.SignedRequestCanonicalizer.
    // ComputeFingerprint) rather than being its own independent random
    // value. Kept in its own file, separate from device.json
    // (DeviceIdentityStore), since that file is purely cosmetic display
    // state (Alias/derived Fingerprint) while this one holds actual private
    // key material - splitting them leaves room to harden just this file
    // later (OS keychain) without touching the identity file's shape.
    //
    // Accepted limitation, not fixed here: no OS-keychain integration, so
    // the private key sits in plaintext JSON like every other store in this
    // codebase (trusted-peers.json, device.json itself) - anyone with
    // filesystem access to this device can extract it and impersonate this
    // device to its peers. That threat (local filesystem compromise) is
    // already far more severe than the LAN-spoofing threat this signing
    // scheme actually closes.
    public class DeviceKeyStore
    {
        private readonly ILogger<DeviceKeyStore> _logger;

        public DeviceKeyStore(ILogger<DeviceKeyStore> logger)
        {
            _logger = logger;
        }

        public static string StorePath => Path.Combine(AppDataDirectory.Path, "device-key.json");

        public (ECDsa Key, byte[] PublicKeyRaw) Load()
        {
            var path = StorePath;
            if (File.Exists(path))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    var material = JsonSerializer.Deserialize(json, FlowerJsonContext.Default.DeviceKeyMaterial);
                    if (material is { PrivateKeyPkcs8Base64.Length: > 0 })
                    {
                        var ecdsa = ECDsa.Create();
                        ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(material.PrivateKeyPkcs8Base64), out _);
                        return (ecdsa, PublicKeyRaw(ecdsa));
                    }
                }
                catch (Exception ex)
                {
                    // Falling through to regenerate below means a brand new
                    // keypair - every peer that had this device trusted
                    // (TrustedPeerStore keys by the fingerprint derived from
                    // the old key) would stop recognizing it, a real enough
                    // consequence to warrant a warning rather than silently
                    // regenerating.
                    _logger.LogWarning(ex, "Failed to load device key from {Path}; generating a new one (previously-trusted peers will need to re-approve this device)", path);
                }
            }

            var fresh = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var publicKeyRaw = PublicKeyRaw(fresh);
            Save(fresh, publicKeyRaw);
            return (fresh, publicKeyRaw);
        }

        // Raw uncompressed SEC1 point (0x04 || X(32) || Y(32)) - smaller than
        // ExportSubjectPublicKeyInfo()'s DER wrapper (65 bytes vs. ~91) and
        // trivially reconstructible (see SignatureVerifier.TryParsePublicKey),
        // which matters since this value has to travel in URLs (stream/
        // cover-art requests handed to LibVLC/OpenSubsonicClient.BuildUrl,
        // which can't carry custom headers) as well as request headers.
        private static byte[] PublicKeyRaw(ECDsa ecdsa)
        {
            var q = ecdsa.ExportParameters(false).Q;
            var raw = new byte[65];
            raw[0] = 0x04;
            Buffer.BlockCopy(q.X!, 0, raw, 1, 32);
            Buffer.BlockCopy(q.Y!, 0, raw, 33, 32);
            return raw;
        }

        private void Save(ECDsa ecdsa, byte[] publicKeyRaw)
        {
            var path = StorePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var material = new DeviceKeyMaterial(
                "ECDSA-P256",
                Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey()),
                Convert.ToBase64String(publicKeyRaw));
            File.WriteAllText(path, JsonSerializer.Serialize(material, FlowerJsonContext.Default.DeviceKeyMaterial));
        }
    }
}
