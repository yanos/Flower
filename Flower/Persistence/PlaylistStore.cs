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

        // One playlist entry. Track.Id is the identity; Path is carried alongside
        // it purely as a fallback for the one launch where a library.json written
        // before Track.Id existed gets fresh ids minted on load (see Track.Id),
        // so the ids in an older playlists.json can't match yet.
        internal sealed record PlaylistTrackRecord(Guid Id, string? Path);

        // Playlists never duplicate track metadata - entries are references
        // resolved against the library on load.
        //
        // Playlist Id/UpdatedAt default to Guid.Empty/default(DateTimeOffset) when
        // absent so records written before sync support (no Id field at all) still
        // deserialize - see Load's migration below rather than failing the whole
        // file. TrackPaths is the pre-Track.Id shape, still read for exactly that
        // reason and no longer written; Tracks replaces it. Internal (not private)
        // so FlowerJsonContext can reference it.
        internal sealed record PlaylistRecord(
            string Name,
            Guid Id = default,
            DateTimeOffset UpdatedAt = default,
            List<PlaylistTrackRecord>? Tracks = null,
            List<string>? TrackPaths = null);

        public List<Playlist> Load(IReadOnlyList<Track> libraryTracks)
        {
            try
            {
                var records = AtomicJsonFile.Read(StorePath, FlowerJsonContext.Default.PlaylistRecordList, _logger) ?? new();
                var byPath  = libraryTracks
                    .Where(t => t.Path != null)
                    .GroupBy(t => t.Path!)
                    .ToDictionary(g => g.Key, g => g.First());
                var byId = new Dictionary<Guid, Track>(libraryTracks.Count);
                foreach (var track in libraryTracks)
                    byId.TryAdd(track.Id, track);

                return records
                    .Select(r => new Playlist(
                        r.Id == Guid.Empty ? Guid.NewGuid() : r.Id,
                        r.Name,
                        ResolveTracks(r, byId, byPath),
                        r.UpdatedAt == default ? DateTimeOffset.UtcNow : r.UpdatedAt))
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load playlists from {Path}; starting with no playlists", StorePath);
                return new List<Playlist>();
            }
        }

        // Id first, then the legacy path list. An entry whose id resolves is
        // used as-is; only one that doesn't falls back to matching by path,
        // which is what carries a playlist written before Track.Id existed
        // through its first launch. An entry matching neither is dropped - by
        // then the track really is gone from the library.
        private static List<Track> ResolveTracks(
            PlaylistRecord record,
            IReadOnlyDictionary<Guid, Track> byId,
            IReadOnlyDictionary<string, Track> byPath)
        {
            if (record.Tracks is { } entries)
            {
                var resolved = new List<Track>(entries.Count);
                foreach (var entry in entries)
                {
                    if (byId.TryGetValue(entry.Id, out var track) ||
                        (entry.Path != null && byPath.TryGetValue(entry.Path, out track)))
                        resolved.Add(track);
                }
                return resolved;
            }

            return (record.TrackPaths ?? new List<string>())
                .Where(byPath.ContainsKey)
                .Select(p => byPath[p])
                .ToList();
        }

        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public async Task SaveAsync(IEnumerable<Playlist> playlists)
        {
            var records = playlists
                .Select(p => new PlaylistRecord(
                    p.Name,
                    p.Id,
                    p.UpdatedAt,
                    // Every entry, not just the ones with a Path. Filtering on
                    // Path != null here silently dropped any not-yet-downloaded
                    // track (see SYNC-PLAN.md Phase 3) the moment the playlist
                    // was saved - the user added a synced song to a playlist and
                    // it was gone on next launch, with nothing logged.
                    p.Tracks.Select(t => new PlaylistTrackRecord(t.Id, t.Path)).ToList()))
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
