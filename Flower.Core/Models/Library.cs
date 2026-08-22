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

        // Where a mutation made here is persisted - see ITrackStore. Both
        // hosts hand in the same TrackRepository; it is optional only for the
        // tests and the handful of call sites that build a throwaway Library
        // with no database behind it at all.
        private readonly ITrackStore? _store;

        // The playlist counterpart of _store - see IPlaylistStore. Same
        // arrangement, same reason: every path that changes a playlist already
        // funnels through PlaylistsChanged, so persisting there covers renames
        // and drag-reorders too, which no call site can be relied on to
        // remember.
        private readonly IPlaylistStore? _playlistStore;

        // Guards every read-modify-write of Tracks. EndReached fires on a LibVLC
        // callback thread (see CLAUDE.md's Binding Notes) while the startup/rescan
        // Task.Run (App.axaml.cs) runs on a threadpool thread - both touch this
        // field, and without a lock a play-count increment applied between a
        // concurrent UpdateTracks' previousByPath snapshot and its Tracks swap
        // is silently discarded: the snapshot predates the increment, and the
        // swapped-in list is built from that stale snapshot. See IncrementPlayCount.
        private readonly object _lock = new();

        public List<Track> Tracks { get; private set; }
        // Same copy-on-write discipline as Tracks, and for the same reason:
        // ReplacePlaylists runs on the sync path (PlaylistSyncService's poll
        // loop, and SyncHttpServer on an HttpListener thread) concurrently with
        // UI-thread create/delete, and every mutation below is a
        // read-modify-write. Guarded by the same _lock, and - critically -
        // exposed as IReadOnlyList over a list that is never mutated in place,
        // so a reader enumerating Playlists (MainViewModel rebuilding the
        // sidebar, PlaylistSyncMapper.ToManifest) can never have the collection
        // change underneath it. Locking the mutators alone would not have been
        // enough for that; it would only have moved the tear from lost updates
        // to InvalidOperationException in the reader.
        public IReadOnlyList<Playlist> Playlists { get; private set; } = [];

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

        // Grouped-album / by-id / by-artist indexes for the OpenSubsonic
        // surface - see LibrarySnapshot. Built lazily and thrown away rather
        // than maintained incrementally, exactly like _byPath above and
        // invalidated at the same points, because the same "a Track was
        // mutated in place without going through this class" case applies:
        // a tag edit changes which album a track groups into.
        private LibrarySnapshot? _snapshot;

        // Lock-free for readers: the field is only ever assigned a fully-built
        // snapshot, so a caller either sees the previous one or the next one,
        // never a half-populated dictionary. Two threads racing to rebuild
        // will each build one and the loser's is discarded - cheaper than
        // holding _lock across the grouping while requests are being served.
        public LibrarySnapshot Snapshot
        {
            get
            {
                var current = Volatile.Read(ref _snapshot);
                if (current is not null)
                    return current;

                var built = LibrarySnapshot.Build(Tracks);
                Volatile.Write(ref _snapshot, built);
                return built;
            }
        }

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

        // Fired after *any* change to the playlist set or to a playlist in it -
        // create, delete, sync replace, rename, track add/remove/reorder. This
        // exists to make persistence structural: saving used to be each call
        // site's own responsibility (six separate
        // "_playlistStore.SaveAsync(Library.Playlists)" calls scattered across
        // MainViewModel and MainView's code-behind), so nothing stopped a new
        // mutation path from simply forgetting and losing the user's edit at
        // exit. App.axaml.cs now subscribes once, here, and every path is
        // covered by construction. Deliberately separate from PlaylistsUpdated:
        // that one means "the sidebar must be rebuilt" and must NOT fire for
        // local edits (it would tear down the row the user is mid-rename in -
        // see ReplacePlaylists), whereas this one means "the on-disk copy is
        // stale" and must fire for exactly those.
        public event EventHandler? PlaylistsChanged;

        // Convenience overload for the many call sites (mostly tests) that don't
        // care about log output - production code always goes through the other
        // constructor instead (see App.axaml.cs), which gets a real, properly
        // DI-configured ILogger<Library>.
        public Library(List<Track> tracks) : this(tracks, NullLogger<Library>.Instance) { }

        // "Could a placeholder stamped with this fingerprint still be played?"
        // Answered by the head that actually has a pairing to answer with - the
        // app hands in SyncRolePolicy.MayRequestFrom against the currently
        // paired Server (see App.axaml.cs). Left null on Flower.Server and in
        // tests, where the question does not arise: a server holds no
        // placeholders of its own, and every one it does hold came from an
        // import it is itself the origin of.
        //
        // A delegate rather than an AppSettings dependency because Library is
        // Flower.Core and the pairing model is the app's - and because the
        // answer changes at runtime (an Unpair, a switch to a different Server)
        // rather than being fixed when the library is built.
        public Func<string, bool>? IsOriginPaired { get; set; }

        public Library(
            List<Track> tracks,
            ILogger<Library> logger,
            ITrackStore? store = null,
            IPlaylistStore? playlistStore = null)
        {
            Tracks = new List<Track>(tracks);
            _logger = logger;
            _store = store;
            _playlistStore = playlistStore;
        }

        // A rescan (see Importer) produces brand-new Track instances read straight
        // from file tags, each defaulting DateAdded to "now" and PlayCount/
        // ImportedPlayCount to 0 - so without this, every track would look
        // freshly added, and all play counts would silently reset, on every
        // launch/rescan. Carry these forward for any track already known by Path.
        // Replaces the contents wholesale, treating the incoming list as
        // authoritative - the post-construction form of the constructor, for a
        // load straight out of the database.
        //
        // Deliberately not UpdateTracks: that reconciles a fresh *filesystem*
        // scan against what is resident and carries in-memory mutable state
        // forward over it, which is right for a rescan and exactly wrong for a
        // reload, where the stored rows are the newer truth and carrying
        // memory over them would mask a value that never reached disk.
        public void Reset(List<Track> tracks)
        {
            lock (_lock)
            {
                Tracks = tracks;
                InvalidateIndexes();
            }

            TracksUpdated?.Invoke(this, EventArgs.Empty);
        }

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

                // For everything the scan is responsible for, the scan's result
                // IS the library: a track it did not produce is a track that is
                // no longer there. The folder list (Settings > Library) is the
                // whole of what the user has asked Flower to scan, so emptying it
                // empties the library, and deleting a file removes its track -
                // without either needing a separate delete path to remember.
                //
                // Two kinds of track the scan is NOT responsible for survive it,
                // because their absence from a scan says nothing about them:
                //
                //   - A placeholder (Path == null): a peer's catalog entry with
                //     no local file at all (see MergeSyncedTracks). There is
                //     nothing on disk for a scan to find.
                //   - A file this device downloaded itself
                //     (IsLocallyDownloaded - see LibraryDownloadService): it can
                //     live in platform-private storage a scan never looks at,
                //     Android's app-private Downloads folder in particular.
                //
                // Both are excluded again if the fresh scan *did* turn up the
                // same path (iOS's Documents-folder scan legitimately
                // re-discovering a file this device downloaded earlier) or the
                // same SyncKey (the container-UUID-drift case above) - either
                // way that fresh-scanned instance already carried this one's
                // DateAdded/PlayCount/origin metadata forward above, and keeping
                // both would duplicate the track.
                //
                // This test used to be OriginDeviceFingerprint != null, which
                // reads as the same thing only while that field means what its
                // own doc comment promises ("never set on a track this device
                // actually imported itself"). MergeSyncedTracks stopped honouring
                // that on purpose, stamping the origin onto local files a paired
                // server also has - so on any paired device the predicate
                // silently became "every track", and no rescan could ever remove
                // anything again. Observed as a client sitting on 16k tracks with
                // no library folders configured at all, logging "0 track(s) from
                // scan, 16115 synced-only track(s) carried forward" on every
                // launch.
                // The placeholder half of that carries one further condition,
                // and it is the one this device's own library was wrong about
                // for a whole release: a placeholder is only worth keeping
                // while somebody is still there to serve it. Unpairing used to
                // clear three fields in AppSettings and leave the catalog
                // alone, so a client that unpaired sat on a library made
                // entirely of rows that could not be played, relaunch after
                // relaunch - every click on one logging "no currently paired,
                // reachable origin device" and doing nothing visible at all.
                // UnpairServer now prunes them at the moment of unpairing (see
                // RemoveTracksFromOrigin); this is what heals a library that
                // was already in that state before it did.
                var freshPaths = new HashSet<string>(
                    tracks.Where(t => t.Path != null).Select(t => t.Path!),
                    StringComparer.OrdinalIgnoreCase);
                var freshSyncKeys = new HashSet<string>(tracks.Select(t => t.SyncKey));
                var carriedForwardSyncTracks = Tracks.Where(t =>
                    (t.Path == null || t.IsLocallyDownloaded)
                    && (t.Path == null || !freshPaths.Contains(t.Path))
                    && !freshSyncKeys.Contains(t.SyncKey)
                    && (t.Path != null || HasSomewhereToComeFrom(t)))
                    .ToList();

                Tracks = tracks.Concat(carriedForwardSyncTracks).ToList();
                InvalidateIndexes();
                afterCount = Tracks.Count;
                carriedForwardCount = carriedForwardSyncTracks.Count;

                RebindPlaylistTracks();

                // Inside the lock, deliberately. Rescans, sync merges and
                // imports are triggered from independent sites with no
                // ordering between them, so two can overlap; writing here
                // means the rows go out in the same order the swaps happened
                // and an earlier reconciliation cannot land on top of a later
                // one. This is the ordering LibraryStore's own write lock used
                // to provide, back when remembering to call it was each
                // caller's job. Readers are unaffected - Tracks is
                // copy-on-write and read without the lock.
                Persist(() => _store!.ReplaceAll(Tracks));
            }

            _logger.LogInformation("Library updated: {FreshCount} track(s) from scan, {CarriedForwardCount} placeholder/downloaded track(s) carried forward, {TotalBefore} -> {TotalAfter}",
                tracks.Count, carriedForwardCount, beforeCount, afterCount);

            TracksUpdated?.Invoke(this, EventArgs.Empty);
        }

        // Whether a track with no local file still has an origin that could
        // produce one. Three ways to say yes, and only one to say no:
        //
        //   - No IsOriginPaired hook at all: the host does not have a pairing
        //     model (Flower.Server), so it is in no position to call anything
        //     orphaned and the answer stays what it was before this existed.
        //   - No OriginDeviceFingerprint: not a sync placeholder in the first
        //     place. DeleteDownloadedFileAsync produces one of these by
        //     deleting the only copy of a purely local import - it is the
        //     user's own track, nobody else's, and nothing here may drop it.
        //   - The fingerprint is the currently paired Server: business as
        //     usual.
        private bool HasSomewhereToComeFrom(Track track) =>
            IsOriginPaired == null
            || track.OriginDeviceFingerprint == null
            || IsOriginPaired(track.OriginDeviceFingerprint);

        // Everything a given origin device was the only source of, dropped -
        // called when this device stops being paired with it (see
        // PeerSyncCoordinator.UnpairServer).
        //
        // Two different things carry that fingerprint and only one of them goes
        // away. A placeholder (Path == null) was never anything but a promise
        // that this peer would serve the file on request; with the pairing gone
        // the promise is void and the row is unplayable, so it is removed. A
        // real file (Path != null) is this device's own, whatever the sync once
        // said about who else has a copy - it stays, and only loses the origin
        // metadata, which is now stale and would otherwise have the mobile
        // delete-a-download warning still calling it re-downloadable.
        //
        // Returns how many tracks were removed, for the caller to log.
        public int RemoveTracksFromOrigin(string originFingerprint)
        {
            int removedCount;
            lock (_lock)
            {
                var kept = new List<Track>(Tracks.Count);
                foreach (var track in Tracks)
                {
                    if (track.OriginDeviceFingerprint != originFingerprint)
                    {
                        kept.Add(track);
                        continue;
                    }

                    if (track.Path == null)
                        continue;

                    track.OriginDeviceFingerprint = null;
                    track.OriginTrackId = null;
                    track.OriginFileExtension = null;
                    track.OriginAlbumArtHash = null;
                    kept.Add(track);
                }

                removedCount = Tracks.Count - kept.Count;
                Tracks = kept;
                InvalidateIndexes();
                RebindPlaylistTracks();

                // Inside the lock, for the ordering reason UpdateTracks'
                // own Persist call spells out. Unconditional, because the
                // origin metadata cleared above is a change worth writing even
                // when nothing was removed.
                Persist(() => _store!.ReplaceAll(Tracks));
            }

            _logger.LogInformation("Dropped {RemovedCount} placeholder(s) from origin {Origin} and cleared its metadata from the rest",
                removedCount, originFingerprint);

            TracksUpdated?.Invoke(this, EventArgs.Empty);
            return removedCount;
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
            track.Starred                 = previous.Starred;
            track.StarredAt               = previous.StarredAt;
            track.OriginDeviceFingerprint = previous.OriginDeviceFingerprint;
            track.OriginTrackId           = previous.OriginTrackId;
            track.OriginFileExtension     = previous.OriginFileExtension;
            track.OriginAlbumArtHash      = previous.OriginAlbumArtHash;
            // Carried forward even though a rescan finding the file means it was
            // under a scanned folder after all: the flag also answers "is
            // deleting this file reversible" for the mobile download UI, and that
            // stays true of a downloaded file the scan happens to see.
            track.IsLocallyDownloaded     = previous.IsLocallyDownloaded;
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
                InvalidateIndexes();
                removedCount = stale.Count;

                // See UpdateTracks - same write, same reason for being under
                // the lock. Without it a merge only lived in memory, and a
                // relaunched app (mobile has no always-on background process)
                // lost every not-yet-downloaded placeholder learned this way.
                Persist(() => _store!.ReplaceAll(Tracks));
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
            var current = BumpPlayCount(playedTrack);
            Persist(() => _store!.UpdateStats(current));
            TrackStatsChanged?.Invoke(this, new TrackStatsChangedEventArgs(current));
            return current;
        }

        private Track BumpPlayCount(Track playedTrack)
        {
            lock (_lock)
            {
                var current = ResolveCurrent(playedTrack);
                current.PlayCount++;
                BumpChangeToken();
                _logger.LogDebug("PlayCount incremented to {NewCount} for {Title} ({Path})", current.PlayCount, current.Title, current.Path);
                return current;
            }
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
        private void InvalidateIndexes()
        {
            _byPath = null;
            Volatile.Write(ref _snapshot, null);
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
            var current = StampLastPlayed(playedTrack);
            Persist(() => _store!.UpdateStats(current));
            TrackStatsChanged?.Invoke(this, new TrackStatsChangedEventArgs(current));
            return current;
        }

        private Track StampLastPlayed(Track playedTrack)
        {
            lock (_lock)
            {
                var current = ResolveCurrent(playedTrack);
                current.LastPlayedAt = DateTimeOffset.UtcNow;
                BumpChangeToken();
                return current;
            }
        }

        // Whichever Track a Subsonic-style id names, or null. The id
        // vocabulary is the wire's, not this class's, which is why this
        // parses rather than taking a Guid: it is the boundary, and having
        // exactly one of them is the point (the playlist half has its own,
        // ResolveTracks below).
        public Track? Find(string? id) =>
            EntityId.FromWire(id) is { } parsed ? Snapshot.ById.GetValueOrDefault(parsed) : null;

        // "This track finished playing", by id - Subsonic's scrobble, and
        // what the client's own end-of-track path does in two calls.
        //
        // Both halves, because a scrobble is both: the count bump and the
        // played-at stamp live on separate methods above only because the
        // client triggers them at different moments (see Track.LastPlayedAt),
        // which a single request does not. One stats write covers the pair
        // rather than one per half.
        public bool RecordPlay(string? id)
        {
            if (Find(id) is not { } track)
                return false;

            var current = BumpPlayCount(track);
            current = StampLastPlayed(current);
            Persist(() => _store!.UpdateStats(current));

            TrackStatsChanged?.Invoke(this, new TrackStatsChangedEventArgs(current));
            return true;
        }

        // Stars or unstars every track behind one Subsonic id - a song, or every
        // track on an album or by an album artist - and hands back the tracks
        // it touched so the caller can persist them and report a count.
        //
        // Resolved through Snapshot rather than a scan: an album or artist id
        // is already a key in those indexes. Mutation is in place on the Track
        // objects, so the snapshot itself does not have to be rebuilt - Starred
        // is not part of what it indexes.
        //
        // Returns how many tracks were affected, so a caller can tell
        // "starred nothing" (a bad id) from a real change.
        public int SetStarred(StarTarget target, string value, bool starred)
        {
            var snapshot = Snapshot;
            var matches = target switch
            {
                StarTarget.Song => Find(value) is { } track ? (IReadOnlyList<Track>)[track] : [],
                StarTarget.Album => snapshot.AlbumTracks(value),
                _ => snapshot.ArtistTracks(value),
            };

            if (matches.Count == 0)
                return 0;

            var starredAt = starred ? DateTimeOffset.UtcNow : (DateTimeOffset?)null;
            lock (_lock)
            {
                foreach (var track in matches)
                {
                    track.Starred = starred;
                    track.StarredAt = starredAt;
                }

                BumpChangeToken();
            }

            // One indexed UPDATE over the matching rows rather than one per
            // track: the in-memory mutation is what reads will see, and the
            // database only has to end up agreeing.
            //
            // For a song, the store is told the id of the track that was
            // actually matched rather than the string the caller passed:
            // EntityId.FromWire accepts a dashed Guid, the id column holds
            // EntityId.ToKey's hex, and forwarding the caller's spelling would
            // resolve in memory and then update no row at all. Album and
            // artist ids are content-derived hashes with a single spelling, so
            // they pass through unchanged.
            var stored = target == StarTarget.Song ? matches[0].Id.ToKey() : value;
            Persist(() => _store!.SetStarred(target, stored, starred, starredAt));
            return matches.Count;
        }

        // The write half of a mutation, and the one part of it allowed to
        // fail without taking the mutation with it. Two reasons it is caught
        // here rather than left to the caller: the in-memory change has
        // already been applied and is what every reader sees, so a failed
        // write means the database is behind, not that the change did not
        // happen; and these run on threads where an escaping exception is
        // fatal rather than handled - IncrementPlayCount is called from
        // LibVLC's EndReached callback (see CLAUDE.md's Binding Notes), where
        // an unobserved throw takes the process down over a play count. This
        // is what LibraryStore.WriteStats used to catch on the client's
        // behalf, moved to where the write now happens so both hosts get it.
        private void Persist(Action write)
        {
            if (_store is null)
                return;

            try
            {
                write();
            }
            catch (Exception ex)
            {
                // Deliberately broad: the store is an interface here, so the
                // specific storage failures (a locked database, a data
                // directory deleted out from under a test) are not types this
                // class can name.
                _logger.LogError(ex, "Could not persist a library change; the in-memory library is ahead of the database");
            }
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
        // The whole-library form, for a mutation applied across every track at
        // once - the iTunes play-count/date-added sync. Rewrites the table,
        // because that is genuinely what changed.
        public void NotifyTrackChanged()
        {
            InvalidateIndexes();
            Persist(() => _store!.ReplaceAll(Tracks));
            TracksUpdated?.Invoke(this, EventArgs.Empty);
        }

        // The same signal for a known, bounded set of changed tracks - a
        // placeholder's Path after a download, a tag edit - which is one
        // upsert each rather than a rewrite of the whole table. Every one of
        // these call sites used to persist by saving the entire library: four
        // separate 16k-row writes to push one changed row.
        public void NotifyTracksChanged(IReadOnlyList<Track> changed)
        {
            InvalidateIndexes();
            Persist(() =>
            {
                foreach (var track in changed)
                    _store!.Upsert(track);
            });

            TracksUpdated?.Invoke(this, EventArgs.Empty);
        }

        public void NotifyTrackChanged(Track changed) => NotifyTracksChanged([changed]);

        public void AddPlaylist(Playlist playlist)
        {
            lock (_lock)
            {
                var next = new List<Playlist>(Playlists) { playlist };
                SwapPlaylists(next);
            }

            _logger.LogInformation("Playlist created: {Name} ({TrackCount} track(s))", playlist.Name, playlist.Tracks.Count);
            RaisePlaylistsChanged();
        }

        public void RemovePlaylist(Playlist playlist)
        {
            lock (_lock)
            {
                var next = new List<Playlist>(Playlists);
                if (!next.Remove(playlist))
                    return;

                SwapPlaylists(next);
            }

            _logger.LogInformation("Playlist deleted: {Name}", playlist.Name);
            RaisePlaylistsChanged();
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
            lock (_lock)
            {
                if (PlaylistsUnchanged(Playlists, playlists))
                    return;

                SwapPlaylists(new List<Playlist>(playlists));
            }

            // Outside the lock: both of these run arbitrary subscriber code
            // (a sidebar rebuild, a store write), and holding _lock across that
            // would let a subscriber that touches the library re-enter and
            // deadlock or, worse, observe a half-applied state.
            PlaylistsUpdated?.Invoke(this, EventArgs.Empty);
            RaisePlaylistsChanged();
        }

        // Installs a new playlist list, moving the Playlist.Changed subscription
        // from the outgoing set to the incoming one. That subscription is what
        // makes an *in-place* edit - a rename, a track added, a drag-reorder -
        // reach PlaylistsChanged at all; without it, persistence would still be
        // the caller announcing its own mutation, just under a new name.
        // Callers hold _lock.
        private void SwapPlaylists(List<Playlist> next)
        {
            foreach (var playlist in Playlists)
                playlist.Changed -= OnPlaylistChanged;

            Playlists = next;

            foreach (var playlist in Playlists)
                playlist.Changed += OnPlaylistChanged;
        }

        private void OnPlaylistChanged(object? sender, EventArgs e) => RaisePlaylistsChanged();

        // Persists first, then announces. Every playlist mutation ends here -
        // create, delete, sync replace, and (via Playlist.Changed above) an
        // in-place rename, track add/remove or drag-reorder - which is what
        // makes "the on-disk copy is stale" and "the write" the same event
        // rather than a rule each of the ~six call sites had to remember. The
        // whole set goes out, not one row: PlaylistRepository.Save is an
        // upsert plus a delete-not-in in one transaction, and a library has
        // tens of playlists.
        private void RaisePlaylistsChanged()
        {
            if (_playlistStore is not null)
            {
                try
                {
                    _playlistStore.Save(Playlists);
                }
                catch (Exception ex)
                {
                    // Same rule as Persist: the in-memory set is already
                    // changed and is what every reader sees, and a failed
                    // write is not a reason to take a rename down with it.
                    _logger.LogError(ex, "Could not persist playlists; the in-memory set is ahead of the database");
                }
            }

            PlaylistsChanged?.Invoke(this, EventArgs.Empty);
        }

        // Replaces the playlist set as loaded from storage - no write back, and
        // no PlaylistsChanged. The tracks counterpart of Reset: replaying what
        // is already on disk is not a change to persist, and announcing it as
        // one would have every startup write the set straight back out.
        public void ResetPlaylists(List<Playlist> playlists)
        {
            lock (_lock)
            {
                SwapPlaylists(new List<Playlist>(playlists));
            }
        }

        // The playlist a Subsonic id names, or null - the playlist half of
        // Find above.
        public Playlist? FindPlaylist(string? id) =>
            EntityId.FromWire(id) is { } parsed ? Playlists.FirstOrDefault(p => p.Id == parsed) : null;

        // Wire track ids -> the live Tracks they name, for a playlist built or
        // edited over the protocol. The only place ids are converted in bulk,
        // and deliberately so: past this point everything - Library, the
        // repository, the client, the server's endpoints - deals in Track
        // references, so both hosts agree on what a playlist *is* rather than
        // each holding it in the shape its own storage happened to want.
        //
        // An id that does not resolve is skipped rather than blocked by a
        // foreign key: a rescan can legitimately drop a track whose file was
        // deleted without that having to cascade through every playlist
        // referencing it. Same rule PlaylistRepository.Load applies on the way
        // back in.
        public List<Track> ResolveTracks(IEnumerable<string?> ids)
        {
            var byId = Snapshot.ById;
            var tracks = new List<Track>();
            foreach (var id in ids)
            {
                if (EntityId.FromWire(id) is { } parsed && byId.TryGetValue(parsed, out var track))
                    tracks.Add(track);
            }

            return tracks;
        }

        // Id+UpdatedAt (bumped by Playlist on every rename/track add/remove/reorder -
        // see Playlist.UpdatedAt) is enough to tell "identical" apart from "changed"
        // without a deep track-by-track comparison. Order matters too, since the
        // sidebar renders playlists in list order.
        private static bool PlaylistsUnchanged(IReadOnlyList<Playlist> a, IReadOnlyList<Playlist> b)
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
