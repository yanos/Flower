using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Flower.Models
{
    public class Library
    {
        private readonly ILogger<Library> _logger;

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

        // Convenience overload for the many call sites (mostly tests) that don't
        // care about log output - production code always goes through the other
        // constructor instead (see App.axaml.cs), which gets a real, properly
        // DI-configured ILogger<Library>.
        public Library(List<Track> tracks) : this(tracks, NullLogger<Library>.Instance) { }

        public Library(List<Track> tracks, ILogger<Library> logger)
        {
            Tracks = new List<Track>(tracks);
            _logger = logger;
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

                // Fallback for a synced track whose exact Path string no longer
                // matches anything in this fresh scan - keyed by SyncKey
                // (Title/Artist/Album/Duration), which stays stable even when
                // Path doesn't. Confirmed on a real device: iOS can reassign
                // the sandboxed app's Data container UUID across a reinstall,
                // which shifts every absolute path under it (including
                // Documents, where downloaded files and library.json both
                // live) - the exact-Path match above then fails for a
                // downloaded file whose content and filename are otherwise
                // completely unchanged, and without this fallback the stale
                // old-container Track survives untouched below (see
                // carriedForwardSyncTracks) alongside the freshly-rescanned
                // one for the same physical file, showing up as a duplicate.
                // Restricted to OriginDeviceFingerprint-carrying tracks - a
                // plain local track's Path has no comparable reason to drift
                // out from under it between scans.
                var previousSyncedByKey = Tracks
                    .Where(t => t.OriginDeviceFingerprint != null)
                    .GroupBy(t => t.SyncKey)
                    .ToDictionary(g => g.Key, g => g.First());

                foreach (var track in tracks)
                {
                    if (track.Path != null && previousByPath.TryGetValue(track.Path, out var previous))
                    {
                        track.DateAdded         = previous.DateAdded;
                        track.PlayCount         = previous.PlayCount;
                        track.ImportedPlayCount = previous.ImportedPlayCount;
                        CarryForwardOrigin(track, previous);
                    }
                    else if (previousSyncedByKey.TryGetValue(track.SyncKey, out var previousSynced))
                    {
                        track.DateAdded         = previousSynced.DateAdded;
                        track.PlayCount         = previousSynced.PlayCount;
                        track.ImportedPlayCount = previousSynced.ImportedPlayCount;
                        CarryForwardOrigin(track, previousSynced);
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
                // OR the same SyncKey (the container-UUID-drift case above) - either
                // way, that fresh-scanned instance already carried its
                // DateAdded/PlayCount/origin metadata forward above.
                var freshPaths = new HashSet<string>(
                    tracks.Where(t => t.Path != null).Select(t => t.Path!),
                    StringComparer.OrdinalIgnoreCase);
                var freshSyncKeys = new HashSet<string>(tracks.Select(t => t.SyncKey));
                var carriedForwardSyncTracks = Tracks.Where(t =>
                    t.OriginDeviceFingerprint != null
                    && (t.Path == null || !freshPaths.Contains(t.Path))
                    && !freshSyncKeys.Contains(t.SyncKey));

                Tracks = tracks.Concat(carriedForwardSyncTracks).ToList();
            }

            TracksUpdated?.Invoke(this, EventArgs.Empty);
        }

        // Shared by UpdateTracks' two match branches (exact Path, SyncKey
        // fallback) - a freshly-rescanned Track starts with none of this
        // (Importer only reads file tags), so without it a rescan would
        // silently strip sync origin/redownload info from any track that
        // also happens to be locally rediscoverable (the common case for a
        // downloaded file, which lives in the same folder Importer scans).
        private static void CarryForwardOrigin(Track track, Track previous)
        {
            track.OriginDeviceFingerprint = previous.OriginDeviceFingerprint;
            track.OriginFileExtension = previous.OriginFileExtension;
            track.OriginAlbumArtHash = previous.OriginAlbumArtHash;
            MergeRemotePlayCounts(track, previous.RemotePlayCounts);
        }

        // Applies a peer's known-songs catalog (see LibrarySyncService,
        // SYNC-PLAN.md Phase 3): each incoming track becomes a new Path == null
        // placeholder if this device has nothing matching it by SyncKey, or - if
        // it already has a placeholder OR a real, Path-backed copy for the same
        // track - just updates which peer currently holds a copy
        // (OriginDeviceFingerprint) and its latest known album art
        // (OriginAlbumArtHash). Every other device's play count
        // (RemotePlayCounts) is merged in either way, real file or placeholder -
        // see MergeRemotePlayCounts. Never replaces a track this device already
        // has a real, Path-backed copy of with the incoming one, and never
        // removes anything just because a peer doesn't mention it - purely
        // additive/updating, unlike UpdateTracks' full replace.
        //
        // OriginDeviceFingerprint/OriginFileExtension/OriginAlbumArtHash and
        // DateAdded are the exceptions to "never touches an already-known
        // track": this method's only caller (LibrarySyncService, per
        // SyncRolePolicy) is always a Client pulling from its one paired
        // Server over Flower's own private /api/flower/v1/library endpoint -
        // never a third-party OpenSubsonic server, which only ever answers
        // the generic /rest/* browse API - so the peer here is always another
        // Flower instance, and OriginDeviceFingerprint is always that Server's
        // own fingerprint (see LibrarySyncMapper.ToPlaceholderTrack). Recording
        // it even when this device already has its own real file for the same
        // track (matched by SyncKey, e.g. a song the user separately imported
        // on both devices) is what lets MobileMainViewModel's delete-downloaded-
        // file warning correctly tell "the paired Server also has this, safe to
        // delete and re-download later" apart from "no known peer has this,
        // deleting it is permanent" - without this, that distinction would only
        // ever be known for a track that started life as a placeholder here,
        // not one this device already had a file for before pairing. Pairing's
        // whole premise is the Client's library *view* mirroring the Server's
        // (see ServerPickerView's confirmation dialog), so the Server's
        // DateAdded should win for Recently Added parity too, real file or
        // placeholder alike.
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
                        existing.OriginDeviceFingerprint = remote.OriginDeviceFingerprint;
                        existing.OriginFileExtension = remote.OriginFileExtension;
                        existing.OriginAlbumArtHash = remote.OriginAlbumArtHash;
                        existing.DateAdded = remote.DateAdded;
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
                _logger.LogDebug("PlayCount incremented to {NewCount} for {Title} ({Path})", current.PlayCount, current.Title, current.Path);
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
