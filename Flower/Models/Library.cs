using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.Extensions.Logging;

using Flower.Logging;

namespace Flower.Models
{
    public class Library
    {
        private static readonly ILogger Logger = AppLogging.CreateLogger<Library>();

        // Guards every read-modify-write of Tracks. EndReached fires on a LibVLC
        // callback thread (see CLAUDE.md's Binding Notes) while the startup/rescan
        // Task.Run (App.axaml.cs) runs on a threadpool thread - both touch this
        // field, and without a lock a play-count increment applied between a
        // concurrent UpdateTracks' previousByPath snapshot and its Tracks swap
        // is silently discarded: the snapshot predates the increment, and the
        // swapped-in list is built from that stale snapshot. See IncrementPlayCount.
        private readonly object _lock = new();

        public List<Track> Tracks { get; private set; }
        public List<Playlist> Playlists { get; private set; } = new List<Playlist>();

        public event EventHandler? TracksUpdated;

        // Fired when PlaylistSyncService/SyncHttpServer replace the playlist set as
        // a result of syncing with another device - see ReplacePlaylists. Local UI
        // actions (create/rename/add-track) manage sidebar state inline instead of
        // relying on this event, so this only needs to cover the sync path.
        public event EventHandler? PlaylistsUpdated;

        public Library(List<Track> tracks)
        {
            Tracks = new List<Track>(tracks);
        }

        // A rescan (see Importer) produces brand-new Track instances read straight
        // from file tags, each defaulting DateAdded to "now" and PlayCount/
        // ImportedPlayCount to 0 - so without this, every track would look
        // freshly added, and all play counts would silently reset, on every
        // launch/rescan. Carry these forward for any track already known by Path.
        public void UpdateTracks(List<Track> tracks)
        {
            lock (_lock)
            {
                var previousByPath = Tracks
                    .Where(t => t.Path != null)
                    .GroupBy(t => t.Path!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                foreach (var track in tracks)
                {
                    if (track.Path != null && previousByPath.TryGetValue(track.Path, out var previous))
                    {
                        track.DateAdded         = previous.DateAdded;
                        track.PlayCount         = previous.PlayCount;
                        track.ImportedPlayCount = previous.ImportedPlayCount;
                    }
                }

                // Tracks known via sync (OriginDeviceFingerprint set - see
                // LibrarySyncService/MergeSyncedTracks), placeholder or already
                // downloaded, aren't necessarily rediscoverable by a disk/MediaStore
                // scan at all - a downloaded file can live in platform-private
                // storage a scan never looks at (see LibraryDownloadService, Android
                // in particular) - so a scan finding nothing there is not evidence
                // the track should be forgotten. Excluded here if the fresh scan
                // *did* also find the same path (e.g. iOS's Documents-folder scan
                // legitimately re-discovering a file this device downloaded earlier)
                // to avoid a duplicate row - that fresh-scanned instance already
                // carried its DateAdded/PlayCount forward above.
                var freshPaths = new HashSet<string>(
                    tracks.Where(t => t.Path != null).Select(t => t.Path!),
                    StringComparer.OrdinalIgnoreCase);
                var carriedForwardSyncTracks = Tracks.Where(t =>
                    t.OriginDeviceFingerprint != null && (t.Path == null || !freshPaths.Contains(t.Path)));

                Tracks = tracks.Concat(carriedForwardSyncTracks).ToList();
            }

            TracksUpdated?.Invoke(this, EventArgs.Empty);
        }

        // Applies a peer's known-songs catalog (see LibrarySyncService,
        // SYNC-PLAN.md Phase 3): each incoming track becomes a new Path == null
        // placeholder if this device has nothing matching it by SyncKey, or - if
        // it already has a placeholder for the same track - just updates which
        // peer currently holds the real file (OriginDeviceFingerprint) and its
        // latest known album art (OriginAlbumArtHash). Every other device's play
        // count (RemotePlayCounts) is merged in either way, real file or
        // placeholder - see MergeRemotePlayCounts. Never replaces a track this
        // device already has a real, Path-backed copy of with the incoming one,
        // and never removes anything just because a peer doesn't mention it -
        // purely additive/updating, unlike UpdateTracks' full replace.
        public void MergeSyncedTracks(IReadOnlyList<Track> incoming)
        {
            lock (_lock)
            {
                var byKey = Tracks
                    .GroupBy(t => t.SyncKey)
                    .ToDictionary(g => g.Key, g => g.First());

                var merged = new List<Track>(Tracks);
                foreach (var remote in incoming)
                {
                    if (byKey.TryGetValue(remote.SyncKey, out var existing))
                    {
                        if (existing.Path == null)
                        {
                            existing.OriginDeviceFingerprint = remote.OriginDeviceFingerprint;
                            existing.OriginFileExtension = remote.OriginFileExtension;
                            existing.OriginAlbumArtHash = remote.OriginAlbumArtHash;
                        }
                        MergeRemotePlayCounts(existing, remote.RemotePlayCounts);
                        continue; // Already known locally, real file or placeholder - only
                                  // the bookkeeping above needed updating, not a whole new Track.
                    }

                    merged.Add(remote);
                    byKey[remote.SyncKey] = remote; // Guards against duplicate SyncKeys within `incoming` itself.
                }

                Tracks = merged;
            }

            TracksUpdated?.Invoke(this, EventArgs.Empty);
        }

        // Per-key max, not overwrite - see Track.RemotePlayCounts' own doc
        // comment: a device's own reported count only ever grows, so this is
        // safe to apply repeatedly, in any order, including a report relayed
        // through a third device rather than learned directly from its origin.
        private static void MergeRemotePlayCounts(Track existing, Dictionary<string, int> incoming)
        {
            foreach (var (fingerprint, count) in incoming)
            {
                existing.RemotePlayCounts[fingerprint] =
                    Math.Max(existing.RemotePlayCounts.GetValueOrDefault(fingerprint), count);
            }
        }

        // Atomically resolves whichever Track object currently represents
        // playedTrack.Path in the library and increments its PlayCount, under the
        // same lock UpdateTracks uses - so this can never race a concurrent rescan
        // the way a plain "find in Tracks, then increment" from the caller could
        // (see the comment on _lock above). playedTrack itself is the fallback for
        // tracks with no Path. Returns the object that was actually incremented,
        // since it may not be playedTrack.
        public Track IncrementPlayCount(Track playedTrack)
        {
            lock (_lock)
            {
                var current = playedTrack.Path != null
                    ? Tracks.FirstOrDefault(t => string.Equals(t.Path, playedTrack.Path, StringComparison.OrdinalIgnoreCase))
                      ?? playedTrack
                    : playedTrack;
                current.PlayCount++;
                Logger.LogDebug("PlayCount incremented to {NewCount} for {Title} ({Path})", current.PlayCount, current.Title, current.Path);
                return current;
            }
        }

        // Notifies listeners that a Track already in Tracks was mutated in place -
        // e.g. a placeholder's Path being set after a successful download (see
        // LibraryDownloadService) - without a list replacement, since the same
        // Track reference is still current and nothing was added or removed.
        public void NotifyTrackChanged() => TracksUpdated?.Invoke(this, EventArgs.Empty);

        public void AddPlaylist(Playlist playlist)
        {
            Playlists.Add(playlist);
        }

        public void RemovePlaylist(Playlist playlist)
        {
            Playlists.Remove(playlist);
        }

        // Atomically swaps in a merged playlist set from a sync session and notifies
        // listeners - see PlaylistsUpdated.
        public void ReplacePlaylists(List<Playlist> playlists)
        {
            Playlists = new List<Playlist>(playlists);
            PlaylistsUpdated?.Invoke(this, EventArgs.Empty);
        }
    }
}
