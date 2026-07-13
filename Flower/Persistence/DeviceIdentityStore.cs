using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Flower.Logging;

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

    // Persists a random per-install identifier used as the "fingerprint" field in
    // the /api/localsend/v2/info response (see SyncHttpServer), so a peer sees the
    // same fingerprint for this device across restarts. Generated once on first
    // run; not yet used for trust/pairing (deferred to when TLS is added).
    public class DeviceIdentityStore
    {
        // Ad-hoc constructed at many call sites - see TrustedPeerStore's
        // identical field for why AppLogging.CreateLogger<T>() rather than a
        // constructor parameter.
        private static readonly ILogger Logger = AppLogging.CreateLogger<DeviceIdentityStore>();

        public static string StorePath => Path.Combine(AppDataDirectory.Path, "device.json");

        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

        public DeviceIdentity Load()
        {
            var path = StorePath;
            if (File.Exists(path))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    if (JsonSerializer.Deserialize<DeviceIdentity>(json, Options) is { Fingerprint.Length: > 0 } identity)
                    {
                        // Alias didn't exist before this field was added - backfill
                        // an existing device.json rather than showing a blank name.
                        if (string.IsNullOrEmpty(identity.Alias))
                        {
                            identity.Alias = DefaultAlias();
                            Save(identity);
                        }
                        return identity;
                    }
                }
                catch (Exception ex)
                {
                    // Falling through to regenerate below means a brand new
                    // random Fingerprint - every peer that had this device
                    // trusted (TrustedPeerStore keys by fingerprint) would stop
                    // recognizing it, a real enough consequence to warrant a
                    // warning rather than silently regenerating.
                    Logger.LogWarning(ex, "Failed to load device identity from {Path}; generating a new one (previously-trusted peers will need to re-approve this device)", path);
                }
            }

            var fresh = new DeviceIdentity { Fingerprint = Guid.NewGuid().ToString("N"), Alias = DefaultAlias() };
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
            File.WriteAllText(path, JsonSerializer.Serialize(identity, Options));
        }

        public async Task SaveAsync(DeviceIdentity identity)
        {
            var path = StorePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(identity, Options));
        }
    }
}
