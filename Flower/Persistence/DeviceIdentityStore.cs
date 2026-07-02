using System;
using System.IO;
using System.Text.Json;

namespace Flower.Persistence
{
    public class DeviceIdentity
    {
        public string Fingerprint { get; set; } = "";
    }

    // Persists a random per-install identifier used as the "fingerprint" field in
    // the /api/localsend/v2/info response (see SyncHttpServer), so a peer sees the
    // same fingerprint for this device across restarts. Generated once on first
    // run; not yet used for trust/pairing (deferred to when TLS is added).
    public class DeviceIdentityStore
    {
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
                        return identity;
                }
                catch
                {
                    // Fall through and regenerate below.
                }
            }

            var fresh = new DeviceIdentity { Fingerprint = Guid.NewGuid().ToString("N") };
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(fresh, Options));
            return fresh;
        }
    }
}
