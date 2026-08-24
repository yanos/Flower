using System;
using System.IO;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

namespace Flower.Persistence
{
    public class DeviceIdentity
    {
        public string Fingerprint { get; set; } = "";

        // What this device calls itself to its server (the X-Flower-Alias header -
        // see NetworkDiscoveryService/PlaylistSyncService/LibrarySyncService/
        // LibraryDownloadService) and what the server's own Devices list shows
        // for it. User-editable via Settings
        // (MainViewModel.DeviceAlias) since there is no reliable, permission-
        // prompt-free way to read a device's real user-assigned name on iOS
        // (UIDevice.name has returned a generic placeholder to third-party apps
        // since iOS 16) or a "your Apple ID name" equivalent on any platform.
        public string Alias { get; set; } = "";
    }

    // Persists this device's display identity (Alias, and a cached
    // Fingerprint) sent on every request to its paired server and shown
    // throughout the sidebar/pairing UI. Fingerprint
    // is not an independent value: it's always kept in sync with the device's
    // signing keypair (see DeviceKeyStore, Services.SignedRequestCanonicalizer.
    // ComputeFingerprint) - Load() takes the currently-derived fingerprint and
    // backfills/corrects a stale or missing one in place, so every other call
    // site that just reads DeviceIdentity.Fingerprint needs no changes.
    public class DeviceIdentityStore
    {
        private readonly ILogger<DeviceIdentityStore> _logger;

        public DeviceIdentityStore(ILogger<DeviceIdentityStore> logger)
        {
            _logger = logger;
        }

        public static string StorePath => Path.Combine(AppDataDirectory.Path, "device.json");

        public DeviceIdentity Load(string derivedFingerprint)
        {
            if (AtomicJsonFile.Read(StorePath, FlowerJsonContext.Default.DeviceIdentity, _logger) is { } identity)
            {
                var changed = false;

                // A mismatch means the signing key was regenerated
                // (device-key.json lost or corrupted - see DeviceKeyStore's
                // own warning when it has to do that). The key is the
                // identity, so the stored fingerprint follows it, and every
                // peer that trusted the old one needs one re-approval.
                if (identity.Fingerprint != derivedFingerprint)
                {
                    _logger.LogWarning(
                        "Device fingerprint changed {Old} -> {New} (now derived from the signing key); previously-trusted peers will need to re-approve this device",
                        identity.Fingerprint, derivedFingerprint);
                    identity.Fingerprint = derivedFingerprint;
                    changed = true;
                }

                if (changed)
                    Save(identity);
                return identity;
            }

            var fresh = new DeviceIdentity { Fingerprint = derivedFingerprint, Alias = DefaultAlias() };
            Save(fresh);
            return fresh;
        }

        // Seed value shown until the user renames it in Settings - not meant to be
        // a great name on its own (mobile in particular has no free, permission-
        // prompt-free API for the user's real device name - see DeviceIdentity.Alias).
        private static string DefaultAlias()
        {
            if (OperatingSystem.IsIOS())
                return "iPhone";
            if (OperatingSystem.IsAndroid())
                return "Android Device";
            return Environment.MachineName;
        }

        public void Save(DeviceIdentity identity) =>
            AtomicJsonFile.Write(StorePath, identity, FlowerJsonContext.Default.DeviceIdentity);

        public Task SaveAsync(DeviceIdentity identity) =>
            AtomicJsonFile.WriteAsync(StorePath, identity, FlowerJsonContext.Default.DeviceIdentity);
    }
}
