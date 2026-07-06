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

                // Sync placeholders (Path == null, OriginDeviceFingerprint set - see
                // LibrarySyncService/MergeSyncedTracks) are never produced by a disk
                // scan at all, so they'd otherwise vanish every time this runs. A
                // rescan only concerns local files; it has no opinion on tracks known
                // only via sync, so carry them forward untouched. Keyed on
                // OriginDeviceFingerprint rather than just Path == null so an
                // otherwise-incomplete Track (no path set for some other reason) isn't
                // mistaken for one.
                var placeholders = Tracks.Where(t => t.Path == null && t.OriginDeviceFingerprint != null);

                Tracks = tracks.Concat(placeholders).ToList();
            }

            TracksUpdated?.Invoke(this, EventArgs.Empty);
        }

        // Applies a peer's known-songs catalog (see LibrarySyncService,
        // SYNC-PLAN.md Phase 3): each incoming track becomes a new Path == null
        // placeholder if this device has nothing matching it by SyncKey, or - if
        // it already has a placeholder for the same track - just updates which
        // peer currently holds the real file (OriginDeviceFingerprint). Never
        // touches a track this device already has a real, Path-backed copy of,
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
                            existing.OriginDeviceFingerprint = remote.OriginDeviceFingerprint;
                        continue; // Already a real local file - the peer having it too isn't news.
                    }

                    merged.Add(remote);
                    byKey[remote.SyncKey] = remote; // Guards against duplicate SyncKeys within `incoming` itself.
                }

                Tracks = merged;
            }

            TracksUpdated?.Invoke(this, EventArgs.Empty);
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
