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
    // Where the platform has one (iOS - see PlatformSecureStore), the key is
    // also kept in the OS credential store, and read from there first. Not for
    // secrecy, though it does no harm: for survival. A Keychain item outlives
    // the app being deleted, so a reinstalled phone comes back with the
    // identity its server already trusts instead of as a stranger. The file
    // below stays as it was - the store on every other platform, and on iOS a
    // copy the rest of the app can go on assuming exists.
    //
    // Accepted limitation, not fixed here: outside iOS, no OS-keychain
    // integration, so the private key sits in plaintext JSON like every other
    // store in this codebase (trusted-peers.json, device.json itself) - anyone with
    // filesystem access to this device can extract it and impersonate this
    // device to its peers. That threat (local filesystem compromise) is
    // already far more severe than the LAN-spoofing threat this signing
    // scheme actually closes. What *is* done about it: this file (and its
    // .bak) is written 0600 on every non-Windows platform (see
    // AtomicJsonFile's ownerOnly), so at least a different local user on a
    // shared machine can't read it. Revocation stays peer-side - a victim
    // deletes this file to force a new keypair, then each peer unpairs the
    // old fingerprint (TrustedPeerStore.RevokeAsync / the Settings device
    // list); there is still no way to invalidate a stolen key remotely.
    public class DeviceKeyStore
    {
        // The name of the key's entry in PlatformSecureStore.
        public const string SecureStoreName = "device-key";

        private readonly ILogger<DeviceKeyStore> _logger;
        private readonly ISecureStore? _secureStore;

        public DeviceKeyStore(ILogger<DeviceKeyStore> logger)
            : this(logger, PlatformSecureStore.Current)
        {
        }

        public DeviceKeyStore(ILogger<DeviceKeyStore> logger, ISecureStore? secureStore)
        {
            _logger = logger;
            _secureStore = secureStore;
        }

        public static string StorePath => Path.Combine(AppDataDirectory.Path, "device-key.json");

        public (ECDsa Key, byte[] PublicKeyRaw) Load()
        {
            // The secure store first, because it is the copy that survives a
            // reinstall: after one, the file below is gone and this is the only
            // place the key this device was paired with still exists. The file
            // is put back from it, so the two agree again.
            if (ReadSecure() is { } kept)
            {
                try
                {
                    var restored = Import(kept);
                    if (!File.Exists(StorePath))
                        SaveFile(kept);

                    return restored;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "The device key kept in the secure store could not be imported; using the one on disk");
                }
            }

            var path = StorePath;
            try
            {
                // AtomicJsonFile, not a plain read: regenerating below means a
                // brand new keypair, and every peer that had this device trusted
                // (TrustedPeerStore keys by the fingerprint derived from the old
                // key) would stop recognizing it. That makes a torn write to this
                // file the most consequential one in the app, and makes the
                // previous-good .bak worth having far more than for any other
                // store here.
                var material = AtomicJsonFile.Read(path, FlowerCoreJsonContext.Default.DeviceKeyMaterial, _logger);
                if (material is { PrivateKeyPkcs8Base64.Length: > 0 })
                {
                    var loaded = Import(material);
                    // A key that predates the secure store - every existing
                    // install, on its first launch with it - is copied in, so
                    // it is the one a later reinstall finds.
                    WriteSecure(material);
                    return loaded;
                }
            }
            catch (Exception ex)
            {
                // Readable JSON that isn't a usable key (truncated base64, wrong
                // curve) still lands here rather than in AtomicJsonFile's own
                // recovery path, so it still deserves the same warning.
                _logger.LogWarning(ex, "Device key in {Path} could not be imported; generating a new one (previously-trusted peers will need to re-approve this device)", path);
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
        // cover-art requests handed to LibVLC/PeerMediaClient.BuildUrl,
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

        private static (ECDsa Key, byte[] PublicKeyRaw) Import(DeviceKeyMaterial material)
        {
            var ecdsa = ECDsa.Create();
            ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(material.PrivateKeyPkcs8Base64), out _);
            return (ecdsa, PublicKeyRaw(ecdsa));
        }

        private void Save(ECDsa ecdsa, byte[] publicKeyRaw)
        {
            var material = new DeviceKeyMaterial(
                "ECDSA-P256",
                Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey()),
                Convert.ToBase64String(publicKeyRaw));
            SaveFile(material);
            WriteSecure(material);
        }

        // With this store's logger, so a key file that could not be made
        // owner-only is said out loud - see AtomicJsonFile.RestrictToOwner.
        private void SaveFile(DeviceKeyMaterial material) =>
            AtomicJsonFile.Write(StorePath, material, FlowerCoreJsonContext.Default.DeviceKeyMaterial, ownerOnly: true, logger: _logger);

        // Null when there is no secure store, nothing in it, or something in it
        // that is not a usable key - in which case the file is the answer, as
        // it was before the secure store existed.
        private DeviceKeyMaterial? ReadSecure()
        {
            if (_secureStore is null)
                return null;

            try
            {
                if (_secureStore.Read(SecureStoreName) is not { Length: > 0 } json)
                    return null;

                var material = JsonSerializer.Deserialize(json, FlowerCoreJsonContext.Default.DeviceKeyMaterial);
                return material is { PrivateKeyPkcs8Base64.Length: > 0 } ? material : null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The device key kept in the secure store could not be read; using the one on disk");
                return null;
            }
        }

        // Best effort: a device whose secure store refuses a write still has
        // its key on disk, and is only as badly off as it was before this
        // existed - it loses the key on a reinstall.
        private void WriteSecure(DeviceKeyMaterial material)
        {
            if (_secureStore is null)
                return;

            try
            {
                _secureStore.Write(SecureStoreName, JsonSerializer.Serialize(material, FlowerCoreJsonContext.Default.DeviceKeyMaterial));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not keep the device key in the secure store; it will not survive the app being reinstalled");
            }
        }
    }
}
