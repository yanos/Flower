using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Flower.Persistence
{
    // Remembers, per remote device (keyed by its DeviceIdentityStore fingerprint) and
    // per playlist Id, the UpdatedAt both sides had already agreed on the last time
    // they synced. PlaylistSyncPlanner uses this as the three-way-merge baseline: if
    // only one side moved past it since, that side wins outright; if both did, it's a
    // real conflict. Without this, every differing playlist on a first-ever sync would
    // look identical to one where only one side genuinely changed.
    public class PlaylistSyncStateStore
    {
        public static string StorePath => Path.Combine(AppDataDirectory.Path, "sync-state.json");

        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

        // deviceFingerprint -> (playlistId -> agreed UpdatedAt)
        private sealed record SyncStateRecord(Dictionary<string, Dictionary<Guid, DateTimeOffset>> Devices);

        public Dictionary<Guid, DateTimeOffset> LoadBaselines(string deviceFingerprint)
        {
            var all = LoadAll();
            return all.Devices.TryGetValue(deviceFingerprint, out var forDevice)
                ? forDevice
                : new Dictionary<Guid, DateTimeOffset>();
        }

        public async Task SaveBaselinesAsync(string deviceFingerprint, Dictionary<Guid, DateTimeOffset> baselines)
        {
            var all = LoadAll();
            all.Devices[deviceFingerprint] = baselines;

            var path = StorePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(all, Options));
        }

        private SyncStateRecord LoadAll()
        {
            var path = StorePath;
            if (!File.Exists(path))
                return new SyncStateRecord(new Dictionary<string, Dictionary<Guid, DateTimeOffset>>());

            try
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<SyncStateRecord>(json, Options)
                       ?? new SyncStateRecord(new Dictionary<string, Dictionary<Guid, DateTimeOffset>>());
            }
            catch
            {
                return new SyncStateRecord(new Dictionary<string, Dictionary<Guid, DateTimeOffset>>());
            }
        }
    }
}
