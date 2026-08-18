using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Flower.Models
{
    // See Library.TrackStatsChanged. Carries the Track object that was
    // actually mutated, which is not necessarily the one the caller passed in -
    // a rescan can have replaced it since (see Library.ResolveCurrent).
    public sealed class TrackStatsChangedEventArgs(Track track) : EventArgs
    {
        public Track Track { get; } = track;
    }

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

        // Path -> Track lookup for IncrementPlayCount/RecordPlayed, which run
        // twice per song change and used to do an O(n) FirstOrDefault over the
        // whole library (16k tracks at the scale SYNC-PLAN.md targets) while
        // holding _lock.
        //
        // Built lazily and thrown away rather than maintained incrementally,
        // because a Track's Path is not immutable: LibraryDownloadService sets
        // it on a placeholder when a download lands, in place, without going
        // through this class at all - so an index kept up to date only at the
        // points where Tracks is *replaced* would silently miss that. Every
        // path that can change either the list or a Path calls Invalidate,
        // including NotifyTrackChanged, which is exactly the "a Track you
        // already hold was mutated in place" signal.
        private Dictionary<string, Track>? _byPath;

        // Opaque "has the track catalog changed" token, handed to peers as the
        // ETag on GET /api/flower/v1/library and advertised on /info so a
        // client can tell a changed server-side catalog from an unchanged one
        // without pulling 6-8 MB of manifest to find out (see
        // ARCHITECTURE-REVIEW Tier 1.4, SyncHttpServer.HandleGetLibraryAsync,
        // LibrarySyncService).
        //
        // Session id + counter rather than a bare counter, and deliberately
        // not a content hash. A bare counter would collide across a restart -
        // a peer holding "7" from before could see a *different* catalog that
        // has also reached "7" and wrongly conclude nothing changed, which is
        // the one failure mode that actually loses data. The session id makes
        // that impossible: a restarted device's tokens never match anything a
        // peer saw before it, so the worst case is one redundant pull. A
        // content hash would avoid even that, but computing it means building
        // the whole manifest (~1,400 TagLib opens for the album-art hashes),
        // which is exactly the work this exists to skip - and /info is polled
        // every ~5s per peer.
        private readonly string _sessionId = Guid.NewGuid().ToString("N")[..8];
        private long _changeCount;

        public string ChangeToken => $"{_sessionId}-{Interlocked.Read(ref _changeCount)}";

        // Every mutation that can change what BuildAllSongs would produce -
        // the list itself, a Path, or a play count / LastPlayedAt, all of
        // which ride along in the manifest.
        private void BumpChangeToken() => Interlocked.Increment(ref _changeCount);

        public event EventHandler? TracksUpdated;

        // A play count / LastPlayedAt bump on a single track, as opposed to
        // TracksUpdated's "the track list itself changed".
        //
        // These used to be the same event, so playing a song rebuilt the whole
        // UI (a 16k-element copy, a full album regroup, and 16k
        // TrackRowViewModel allocations - see MainViewModel.PopulateTracks) and
        // triggered a full library sync with the paired peer, twice per track
        // change. Subscribers should refresh just the affected track's stats
        // columns. See docs/ARCHITECTURE-REVIEW.md Tier 1.1.
        public event EventHandler<TrackStatsChangedEventArgs>? TrackStatsChanged;

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
            int beforeCount, afterCount, carriedForwardCount;
            lock (_lock)
            {
                beforeCount = Tracks.Count;
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
                        CarryForwardMutableState(previous, track);
                    else if (previousSyncedByKey.TryGetValue(track.SyncKey, out var previousSynced))
                        CarryForwardMutableState(previousSynced, track);
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
                    && !freshSyncKeys.Contains(t.SyncKey))
                    .ToList();

                Tracks = tracks.Concat(carriedForwardSyncTracks).ToList();
                InvalidatePathIndex();
                afterCount = Tracks.Count;
                carriedForwardCount = carriedForwardSyncTracks.Count;

                RebindPlaylistTracks();
            }

            _logger.LogInformation("Library updated: {FreshCount} track(s) from scan, {CarriedForwardCount} synced-only track(s) carried forward, {TotalBefore} -> {TotalAfter}",
                tracks.Count, carriedForwardCount, beforeCount, afterCount);

            TracksUpdated?.Invoke(this, EventArgs.Empty);
        }

        // Points every playlist at the Track instances now in Tracks.
        //
        // PlaylistStore resolves playlist membership to Track object references
        // exactly once, at startup. Every launch then kicks off a background
        // rescan whose UpdateTracks replaces Tracks wholesale with brand-new
        // instances - and nothing used to re-point the playlists, so for the
        // rest of the session a playlist held a *different object graph* than
        // the library did for the same songs. A play count incremented on the
        // library's instance never showed up when viewing the playlist, and
        // vice versa; the two only agreed again after a restart, via
        // playlists.json's path round-trip.
        //
        // Deliberately not ReplaceAll: membership and order haven't changed,
        // only which object represents each entry, so bumping Playlist.
        // UpdatedAt here would make every single rescan look like a local edit
        // to PlaylistSyncPlanner's three-way merge and manufacture conflicts
        // out of nothing.
        //
        // An entry with no match left in Tracks keeps its existing instance
        // rather than being dropped: a scan not finding a file is not proof
        // it's gone (see carriedForwardSyncTracks above for the same
        // reasoning), and silently deleting a user's playlist entry is a much
        // worse failure than briefly showing a stale one.
        private void RebindPlaylistTracks()
        {
            var byId = new Dictionary<Guid, Track>(Tracks.Count);
            foreach (var track in Tracks)
                byId.TryAdd(track.Id, track);

            foreach (var playlist in Playlists)
                playlist.RebindTracks(byId);
        }

        // THE list of everything about a Track that a rescan must not reset.
        //
        // A rescan (see Importer) produces brand-new Track instances read
        // straight from file tags - fresh Id, DateAdded defaulting to "now",
        // play counts at 0, no sync origin - so every field here would be
        // silently lost on every single launch without this. It exists as one
        // method, called from both of UpdateTracks' match branches (exact
        // Path, SyncKey fallback), specifically so adding a new
        // persisted-but-not-rescannable field (Starred, Rating, a provider
        // Source tag) is one edit rather than two identical ones that are
        // easy to update only half of. LibraryTests pins that a rescan
        // preserves all of it.
        private static void CarryForwardMutableState(Track previous, Track track)
        {
            // First, and separately worth calling out: Id is this track's
            // identity everywhere else in the app (playlist membership, the
            // play queue, Track Info navigation). Letting a rescan mint a new
            // one would silently orphan the track from every list holding it.
            track.Id                      = previous.Id;

            track.DateAdded               = previous.DateAdded;
            track.PlayCount               = previous.PlayCount;
            track.ImportedPlayCount       = previous.ImportedPlayCount;
            track.LastPlayedAt            = previous.LastPlayedAt;
            track.OriginDeviceFingerprint = previous.OriginDeviceFingerprint;
            track.OriginTrackId           = previous.OriginTrackId;
            track.OriginFileExtension     = previous.OriginFileExtension;
            track.OriginAlbumArtHash      = previous.OriginAlbumArtHash;
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
        // see MergeRemotePlayCounts. Never replaces (or removes) a track this
        // device already has a real, Path-backed copy of - a peer's manifest
        // omitting something is never evidence to touch the user's own file.
        //
        // A never-downloaded placeholder (Path == null) IS removed if it was
        // last known to come from sourceDeviceFingerprint specifically but that
        // peer's current manifest no longer mentions it - the server is this
        // placeholder's only reason to exist, so once the server stops
        // vouching for it there's nothing left backing it locally. Confirmed
        // necessary against a real duplicate: a duration-rounding fix changed
        // what SyncKey a track computes to, and without this the old,
        // now-unreachable placeholder just sat there forever as an orphan
        // alongside the new, correctly-keyed one. Scoped to
        // sourceDeviceFingerprint (not "any placeholder no longer mentioned")
        // so a placeholder left over from a previous pairing to a *different*
        // server - this method's caller only ever syncs one peer at a time,
        // see SyncRolePolicy - is never swept up by an unrelated sync.
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
        //
        // Returns how many tracks were pruned, purely for the caller's own
        // logging (see LibrarySyncService.SyncWithAsync) - visibility that
        // would have made the bug this was built to fix obvious immediately
        // instead of needing a manual device-log investigation.
        public int MergeSyncedTracks(string sourceDeviceFingerprint, IReadOnlyList<Track> incoming)
        {
            int removedCount;
            lock (_lock)
            {
                var byKey = Tracks
                    .GroupBy(t => t.SyncKey)
                    .ToDictionary(g => g.Key, g => g.First());
                var incomingKeys = new HashSet<string>();

                var merged = new List<Track>(Tracks);
                foreach (var remote in incoming)
                {
                    incomingKeys.Add(remote.SyncKey);
                    if (byKey.TryGetValue(remote.SyncKey, out var existing))
                    {
                        existing.OriginDeviceFingerprint = remote.OriginDeviceFingerprint;
                        existing.OriginTrackId = remote.OriginTrackId;
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

                var stale = new HashSet<Track>(merged.Where(t =>
                    t.Path == null &&
                    t.OriginDeviceFingerprint == sourceDeviceFingerprint &&
                    !incomingKeys.Contains(t.SyncKey)));
                merged.RemoveAll(stale.Contains);

                Tracks = merged;
                InvalidatePathIndex();
                removedCount = stale.Count;
            }

            TracksUpdated?.Invoke(this, EventArgs.Empty);
            return removedCount;
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
            Track current;
            lock (_lock)
            {
                current = ResolveCurrent(playedTrack);
                current.PlayCount++;
                BumpChangeToken();
                _logger.LogDebug("PlayCount incremented to {NewCount} for {Title} ({Path})", current.PlayCount, current.Title, current.Path);
            }

            TrackStatsChanged?.Invoke(this, new TrackStatsChangedEventArgs(current));
            return current;
        }

        // Whichever Track object currently represents playedTrack's file.
        // Callers must already hold _lock - the returned reference is only
        // meaningful for as long as Tracks isn't swapped underneath it, which
        // is the whole reason IncrementPlayCount/RecordPlayed mutate inside the
        // lock rather than resolving and returning first.
        private Track ResolveCurrent(Track playedTrack)
        {
            if (playedTrack.Path == null)
                return playedTrack;

            _byPath ??= BuildPathIndex();
            return _byPath.TryGetValue(playedTrack.Path, out var current) ? current : playedTrack;
        }

        // First match wins, matching the FirstOrDefault this replaced: two
        // library entries for the same path is not supposed to happen, but if
        // it does, incrementing a consistent one of them beats throwing.
        private Dictionary<string, Track> BuildPathIndex()
        {
            var index = new Dictionary<string, Track>(Tracks.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var track in Tracks)
            {
                if (track.Path != null)
                    index.TryAdd(track.Path, track);
            }

            return index;
        }

        // Callers must hold _lock, except NotifyTrackChanged - see its comment.
        private void InvalidatePathIndex()
        {
            _byPath = null;
            BumpChangeToken();
        }

        // Atomically resolves whichever Track object currently represents
        // playedTrack.Path in the library and stamps LastPlayedAt to now - same
        // resolve-under-lock pattern as IncrementPlayCount above, for the same
        // reason (a concurrent rescan replacing Tracks mid-flight). Called from
        // PlaylistControlViewModel.Play at the moment a track starts playing, not
        // from the EndReached/IncrementPlayCount "finished naturally" path - see
        // Track.LastPlayedAt's own doc comment for why those two are deliberately
        // different triggers.
        public Track RecordPlayed(Track playedTrack)
        {
            Track current;
            lock (_lock)
            {
                current = ResolveCurrent(playedTrack);
                current.LastPlayedAt = DateTimeOffset.UtcNow;
                BumpChangeToken();
            }

            TrackStatsChanged?.Invoke(this, new TrackStatsChangedEventArgs(current));
            return current;
        }

        // Notifies listeners that a Track already in Tracks was mutated in place -
        // e.g. a placeholder's Path being set after a successful download (see
        // LibraryDownloadService) - without a list replacement, since the same
        // Track reference is still current and nothing was added or removed.
        // The in-place mutation this announces may well be a Path being set on
        // a placeholder that just finished downloading, so the path index has
        // to go with it. Taking _lock here only to null a field would be
        // pointless (a concurrent reader either sees the stale index and is
        // about to be told to re-read anyway, or rebuilds it fresh); what
        // matters is that the next resolve rebuilds rather than trusting an
        // index that predates the new Path.
        public void NotifyTrackChanged()
        {
            InvalidatePathIndex();
            TracksUpdated?.Invoke(this, EventArgs.Empty);
        }

        public void AddPlaylist(Playlist playlist)
        {
            Playlists.Add(playlist);
            _logger.LogInformation("Playlist created: {Name} ({TrackCount} track(s))", playlist.Name, playlist.Tracks.Count);
        }

        public void RemovePlaylist(Playlist playlist)
        {
            Playlists.Remove(playlist);
            _logger.LogInformation("Playlist deleted: {Name}", playlist.Name);
        }

        // Atomically swaps in a merged playlist set from a sync session and notifies
        // listeners - see PlaylistsUpdated. Skipped entirely when the merge came out
        // identical to what's already here (the common case - most syncs find
        // nothing to reconcile): PlaylistsUpdated drives MainViewModel to rebuild the
        // sidebar's whole Playlists section, which - since this runs on whatever
        // debounce/poll cadence PlaylistSyncService uses, independent of the user -
        // would otherwise tear down and recreate every row, mid-rename or not, on
        // every single poll even when nothing actually changed.
        public void ReplacePlaylists(List<Playlist> playlists)
        {
            if (PlaylistsUnchanged(Playlists, playlists))
                return;

            Playlists = new List<Playlist>(playlists);
            PlaylistsUpdated?.Invoke(this, EventArgs.Empty);
        }

        // Id+UpdatedAt (bumped by Playlist on every rename/track add/remove/reorder -
        // see Playlist.UpdatedAt) is enough to tell "identical" apart from "changed"
        // without a deep track-by-track comparison. Order matters too, since the
        // sidebar renders playlists in list order.
        private static bool PlaylistsUnchanged(List<Playlist> a, List<Playlist> b)
        {
            if (a.Count != b.Count)
                return false;

            for (var i = 0; i < a.Count; i++)
            {
                if (a[i].Id != b[i].Id || a[i].UpdatedAt != b[i].UpdatedAt)
                    return false;
            }

            return true;
        }
    }
}
