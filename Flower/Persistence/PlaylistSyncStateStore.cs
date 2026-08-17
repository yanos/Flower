using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

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
        private readonly ILogger<PlaylistSyncStateStore> _logger;

        public PlaylistSyncStateStore(ILogger<PlaylistSyncStateStore> logger)
        {
            _logger = logger;
        }

        public static string StorePath => Path.Combine(AppDataDirectory.Path, "sync-state.json");

        // deviceFingerprint -> (playlistId -> agreed UpdatedAt). Internal (not
        // private) so FlowerJsonContext can reference it.
        internal sealed record SyncStateRecord(Dictionary<string, Dictionary<Guid, DateTimeOffset>> Devices);

        public Dictionary<Guid, DateTimeOffset> LoadBaselines(string deviceFingerprint)
        {
            var all = LoadAll();
            return all.Devices.TryGetValue(deviceFingerprint, out var forDevice)
                ? forDevice
                : new Dictionary<Guid, DateTimeOffset>();
        }

        // Whole-file read-modify-write, so two devices syncing at once would
        // otherwise interleave and drop one device's baselines entirely - the
        // load and the save have to be one critical section, not just the save.
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public async Task SaveBaselinesAsync(string deviceFingerprint, Dictionary<Guid, DateTimeOffset> baselines)
        {
            await _writeLock.WaitAsync();
            try
            {
                var all = LoadAll();
                all.Devices[deviceFingerprint] = baselines;
                await AtomicJsonFile.WriteAsync(StorePath, all, FlowerJsonContext.Default.SyncStateRecord);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private SyncStateRecord LoadAll() =>
            // A corrupt/unreadable sync-state.json just means the next sync treats
            // every playlist as a first-ever sync (no baseline to three-way-merge
            // against) rather than failing - AtomicJsonFile logs and quarantines it
            // rather than letting that difference in behavior pass unnoticed.
            AtomicJsonFile.Read(StorePath, FlowerJsonContext.Default.SyncStateRecord, _logger)
            ?? new SyncStateRecord(new Dictionary<string, Dictionary<Guid, DateTimeOffset>>());
    }
}
