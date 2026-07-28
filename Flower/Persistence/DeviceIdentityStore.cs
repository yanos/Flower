using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

namespace Flower.Persistence
{
    public class DeviceIdentity
    {
        public string Fingerprint { get; set; } = "";

        // What this device calls itself to peers (the /info "alias" field and
        // X-Flower-Alias header - see SyncHttpServer/PlaylistSyncService/
        // LibrarySyncService/LibraryDownloadService) and what the desktop
        // sidebar shows for a discovered device. User-editable via Settings
        // (MainViewModel.DeviceAlias) since there is no reliable, permission-
        // prompt-free way to read a device's real user-assigned name on iOS
        // (UIDevice.name has returned a generic placeholder to third-party apps
        // since iOS 16) or a "your Apple ID name" equivalent on any platform.
        public string Alias { get; set; } = "";
    }

    // Persists this device's display identity (Alias, and a cached
    // Fingerprint) used in the "/api/localsend/v2/info" response (see
    // SyncHttpServer) and shown throughout the sidebar/pairing UI. Fingerprint
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
            var path = StorePath;
            if (File.Exists(path))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    if (JsonSerializer.Deserialize(json, FlowerJsonContext.Default.DeviceIdentity) is { } identity)
                    {
                        var changed = false;

                        // Alias didn't exist before this field was added - backfill
                        // an existing device.json rather than showing a blank name.
                        if (string.IsNullOrEmpty(identity.Alias))
                        {
                            identity.Alias = DefaultAlias();
                            changed = true;
                        }

                        // A mismatch means either first run after the signing
                        // scheme shipped (a stale GUID-shaped fingerprint
                        // predating it) or a regenerated key (device-key.json
                        // lost/corrupted) - either way, every peer that trusted
                        // the old fingerprint needs one re-approval, same as any
                        // other fingerprint change (see DeviceKeyStore's own
                        // warning when it has to regenerate a key).
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
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load device identity from {Path}; generating a new one", path);
                }
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

        public void Save(DeviceIdentity identity)
        {
            var path = StorePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(identity, FlowerJsonContext.Default.DeviceIdentity));
        }

        public async Task SaveAsync(DeviceIdentity identity)
        {
            var path = StorePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(identity, FlowerJsonContext.Default.DeviceIdentity));
        }
    }
}
