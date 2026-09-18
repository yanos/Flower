using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

namespace Flower.Persistence
{
    // Remembers, per server (keyed by its fingerprint), which of its tracks it
    // and this device last agreed were starred - the baseline Library.MergeStar
    // three-way-merges a pulled star against. The same idea as
    // PlaylistSyncStateStore, for the same reason: without a baseline, "the
    // server starred this" and "this device unstarred it" are the same
    // difference, and whichever way that is settled loses somebody's change.
    //
    // On disk rather than in memory because the case it exists for outlives a
    // process: a star made on a phone with no server in reach, before an app
    // the OS then kills. The next launch's first pull has to be able to tell
    // that star from one the server removed, and memory would have forgotten.
    //
    // Only the starred ids, since that is the small side of a library and
    // anything absent is, by the same token, a track agreed to be unstarred.
    public class ServerStarBaselineStore
    {
        private readonly ILogger<ServerStarBaselineStore> _logger;

        public ServerStarBaselineStore(ILogger<ServerStarBaselineStore> logger)
        {
            _logger = logger;
        }

        public static string StorePath => Path.Combine(AppDataDirectory.Path, "server-stars.json");

        // serverFingerprint -> origin ids agreed starred. Internal (not private)
        // so FlowerJsonContext can reference it.
        internal sealed record StarBaselineRecord(Dictionary<string, List<string>> Devices);

        // Null when there has never been a pull from this server, which
        // Library.MergeStar reads as "the server's stars win".
        public IReadOnlySet<string>? Load(string serverFingerprint) =>
            LoadAll().Devices.TryGetValue(serverFingerprint, out var starred)
                ? starred.ToHashSet()
                : null;

        // Whole-file read-modify-write, and a pull's replace can race a push's
        // update, so the load and the save are one critical section.
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        // What a pull served: the whole answer, so a star the server dropped
        // leaves the baseline too.
        public Task ReplaceAsync(string serverFingerprint, IEnumerable<string> starredIds) =>
            WriteAsync(serverFingerprint, _ => starredIds.ToHashSet());

        // What a push the server accepted told it: only those tracks move.
        // Leaves a server with no baseline yet without one - a push before the
        // first pull has nothing to be relative to.
        public Task ApplyAsync(string serverFingerprint, IEnumerable<(string Id, bool Starred)> agreed) =>
            WriteAsync(serverFingerprint, current =>
            {
                if (current is null)
                    return null;

                foreach (var (id, starred) in agreed)
                {
                    if (starred)
                        current.Add(id);
                    else
                        current.Remove(id);
                }

                return current;
            });

        private async Task WriteAsync(string serverFingerprint, System.Func<HashSet<string>?, HashSet<string>?> update)
        {
            await _writeLock.WaitAsync();
            try
            {
                var all = LoadAll();
                var current = all.Devices.TryGetValue(serverFingerprint, out var list) ? list.ToHashSet() : null;
                if (update(current) is not { } next)
                    return;

                all.Devices[serverFingerprint] = next.Order().ToList();
                await AtomicJsonFile.WriteAsync(StorePath, all, FlowerJsonContext.Default.StarBaselineRecord);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        // A corrupt or unreadable file means no baseline, which is the first-
        // pull behaviour: the server's stars win once, and the baseline is
        // rebuilt from that pull.
        private StarBaselineRecord LoadAll() =>
            AtomicJsonFile.Read(StorePath, FlowerJsonContext.Default.StarBaselineRecord, _logger)
            ?? new StarBaselineRecord(new Dictionary<string, List<string>>());
    }
}
