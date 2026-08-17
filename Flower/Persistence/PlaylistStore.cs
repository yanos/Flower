using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Flower.Models;

namespace Flower.Persistence
{
    public class PlaylistStore
    {
        private readonly ILogger<PlaylistStore> _logger;

        // Convenience overload for the many call sites (mostly tests) that don't
        // care about log output - production code always goes through the other
        // constructor instead (see App.axaml.cs), which gets a real, properly
        // DI-configured ILogger<PlaylistStore>.
        public PlaylistStore() : this(NullLogger<PlaylistStore>.Instance) { }

        public PlaylistStore(ILogger<PlaylistStore> logger)
        {
            _logger = logger;
        }

        public static string StorePath => Path.Combine(AppDataDirectory.Path, "playlists.json");

        // One playlist entry. Track.Id is the identity, and the only thing
        // stored - playlists never duplicate track metadata.
        internal sealed record PlaylistTrackRecord(Guid Id);

        // Internal (not private) so FlowerJsonContext can reference it.
        internal sealed record PlaylistRecord(
            string Name,
            Guid Id,
            DateTimeOffset UpdatedAt,
            List<PlaylistTrackRecord>? Tracks);

        public List<Playlist> Load(IReadOnlyList<Track> libraryTracks)
        {
            try
            {
                var records = AtomicJsonFile.Read(StorePath, FlowerJsonContext.Default.PlaylistRecordList, _logger) ?? new();
                var byId = new Dictionary<Guid, Track>(libraryTracks.Count);
                foreach (var track in libraryTracks)
                    byId.TryAdd(track.Id, track);

                return records
                    .Select(r => new Playlist(r.Id, r.Name, ResolveTracks(r, byId), r.UpdatedAt))
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load playlists from {Path}; starting with no playlists", StorePath);
                return new List<Playlist>();
            }
        }

        // An entry whose id doesn't resolve is dropped - by then the track
        // really is gone from the library.
        private static List<Track> ResolveTracks(PlaylistRecord record, IReadOnlyDictionary<Guid, Track> byId)
        {
            if (record.Tracks is not { } entries)
                return new List<Track>();

            var resolved = new List<Track>(entries.Count);
            foreach (var entry in entries)
            {
                if (byId.TryGetValue(entry.Id, out var track))
                    resolved.Add(track);
            }
            return resolved;
        }

        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public async Task SaveAsync(IEnumerable<Playlist> playlists)
        {
            var records = playlists
                .Select(p => new PlaylistRecord(
                    p.Name,
                    p.Id,
                    p.UpdatedAt,
                    // Every entry, including not-yet-downloaded ones (see
                    // SYNC-PLAN.md Phase 3): an earlier version filtered on
                    // Path != null here and silently dropped any synced track
                    // the moment the playlist was saved - the user added one
                    // and it was gone on next launch, with nothing logged.
                    p.Tracks.Select(t => new PlaylistTrackRecord(t.Id)).ToList()))
                .ToList();

            await _writeLock.WaitAsync();
            try
            {
                await AtomicJsonFile.WriteAsync(StorePath, records, FlowerJsonContext.Default.PlaylistRecordList);
            }
            finally
            {
                _writeLock.Release();
            }
        }
    }
}
