using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Flower.Models;

namespace Flower.Tests;

public class LibraryTests
{
    [Fact]
    public void UpdateTracks_replaces_the_track_list()
    {
        var library = new Library(new List<Track> { new Track { Title = "Old" } });
        library.UpdateTracks(new List<Track> { new Track { Title = "New1" }, new Track { Title = "New2" } });

        Assert.Equal(2, library.Tracks.Count);
        Assert.DoesNotContain(library.Tracks, t => t.Title == "Old");
    }

    [Fact]
    public void UpdateTracks_raises_TracksUpdated_exactly_once()
    {
        var library = new Library(new List<Track>());
        int raised = 0;
        library.TracksUpdated += (_, _) => raised++;

        library.UpdateTracks(new List<Track> { new Track { Title = "A" } });

        Assert.Equal(1, raised);
    }

    [Fact]
    public void UpdateTracks_copies_the_list_so_later_mutating_the_source_has_no_effect()
    {
        var source = new List<Track> { new Track { Title = "A" } };
        var library = new Library(source);

        source.Add(new Track { Title = "B" });

        Assert.Single(library.Tracks);
    }

    [Fact]
    public void UpdateTracks_preserves_DateAdded_for_a_track_matched_by_path()
    {
        var originalDate = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var library = new Library(new List<Track>
        {
            new Track { Title = "Old", Path = "/music/a.mp3", DateAdded = originalDate }
        });

        // Simulates a rescan: Importer builds a brand-new Track for the same file
        // (tags re-read from disk), defaulting DateAdded to "now" like a genuinely
        // new file would - UpdateTracks must recognize it's the same file by Path
        // and keep the original date instead.
        var rescanned = new Track { Title = "Old (retagged)", Path = "/music/a.mp3" };

        library.UpdateTracks(new List<Track> { rescanned });

        Assert.Equal(originalDate, library.Tracks.Single().DateAdded);
    }

    [Fact]
    public void UpdateTracks_matches_paths_case_insensitively()
    {
        var originalDate = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var library = new Library(new List<Track> { new Track { Path = "/Music/A.mp3", DateAdded = originalDate } });

        library.UpdateTracks(new List<Track> { new Track { Path = "/music/a.mp3" } });

        Assert.Equal(originalDate, library.Tracks.Single().DateAdded);
    }

    [Fact]
    public void UpdateTracks_leaves_DateAdded_alone_for_a_track_with_no_previous_match()
    {
        var freshDate = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var library = new Library(new List<Track> { new Track { Path = "/music/other.mp3" } });

        library.UpdateTracks(new List<Track> { new Track { Path = "/music/new.mp3", DateAdded = freshDate } });

        Assert.Equal(freshDate, library.Tracks.Single().DateAdded);
    }

    [Fact]
    public void UpdateTracks_preserves_PlayCount_and_ImportedPlayCount_for_a_track_matched_by_path()
    {
        var library = new Library(new List<Track>
        {
            new Track { Path = "/music/a.mp3", PlayCount = 5, ImportedPlayCount = 42 }
        });

        // Simulates a rescan: Importer builds a brand-new Track for the same file,
        // defaulting both play counts to 0 like a genuinely new file would -
        // UpdateTracks must recognize it's the same file by Path and carry the
        // originals forward instead, exactly like it already does for DateAdded.
        var rescanned = new Track { Path = "/music/a.mp3" };

        library.UpdateTracks(new List<Track> { rescanned });

        Assert.Equal(5, library.Tracks.Single().PlayCount);
        Assert.Equal(42, library.Tracks.Single().ImportedPlayCount);
    }

    [Fact]
    public void UpdateTracks_preserves_LastPlayedAt_for_a_track_matched_by_path()
    {
        var lastPlayed = new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var library = new Library(new List<Track>
        {
            new Track { Path = "/music/a.mp3", LastPlayedAt = lastPlayed }
        });

        // Simulates a rescan: Importer builds a brand-new Track for the same
        // file, defaulting LastPlayedAt to null like a genuinely new file
        // would - UpdateTracks must carry the original forward, exactly like
        // it already does for DateAdded/PlayCount.
        var rescanned = new Track { Path = "/music/a.mp3" };

        library.UpdateTracks(new List<Track> { rescanned });

        Assert.Equal(lastPlayed, library.Tracks.Single().LastPlayedAt);
    }

    [Fact]
    public void UpdateTracks_leaves_LastPlayedAt_null_for_a_never_played_track()
    {
        var library = new Library(new List<Track> { new Track { Path = "/music/other.mp3" } });

        library.UpdateTracks(new List<Track> { new Track { Path = "/music/new.mp3" } });

        Assert.Null(library.Tracks.Single().LastPlayedAt);
    }

    [Fact]
    public void RecordPlayed_resolves_the_current_track_by_path_and_stamps_it()
    {
        var oldTrack = new Track { Path = "/music/a.mp3" };
        var library = new Library(new List<Track> { oldTrack });

        // A rescan swapped in a brand-new Track instance for the same file -
        // like the Track a caller still holding a reference to oldTrack (e.g.
        // PlaylistControlViewModel.CurrentlyPlayingTrack) would now be stale
        // against.
        var newTrack = new Track { Path = "/music/a.mp3" };
        library.UpdateTracks(new List<Track> { newTrack });

        var before = DateTimeOffset.UtcNow;
        var played = library.RecordPlayed(oldTrack);
        var after = DateTimeOffset.UtcNow;

        Assert.Same(newTrack, played);
        Assert.NotNull(newTrack.LastPlayedAt);
        Assert.InRange(newTrack.LastPlayedAt!.Value, before, after);
        Assert.Null(oldTrack.LastPlayedAt);
    }

    [Fact]
    public void IncrementPlayCount_resolves_the_current_track_by_path_and_increments_it()
    {
        var oldTrack = new Track { Path = "/music/a.mp3" };
        var library = new Library(new List<Track> { oldTrack });

        // A rescan swapped in a brand-new Track instance for the same file -
        // like the Track a caller still holding a reference to oldTrack (e.g.
        // PlaylistControlViewModel.CurrentlyPlayingTrack) would now be stale
        // against.
        var newTrack = new Track { Path = "/music/a.mp3" };
        library.UpdateTracks(new List<Track> { newTrack });

        var incremented = library.IncrementPlayCount(oldTrack);

        Assert.Same(newTrack, incremented);
        Assert.Equal(1, newTrack.PlayCount);
        Assert.Equal(0, oldTrack.PlayCount);
    }

    // Without Library's internal lock, concurrent int++ from multiple threads on
    // the same object is a classic lost-update race - some increments overwrite
    // each other instead of accumulating, so this would be flaky (occasionally
    // land below concurrentPlays) if the locking were removed.
    [Fact]
    public void IncrementPlayCount_is_thread_safe_under_concurrent_calls()
    {
        var track = new Track { Path = "/music/a.mp3" };
        var library = new Library(new List<Track> { track });
        const int concurrentPlays = 200;

        Parallel.For(0, concurrentPlays, _ => library.IncrementPlayCount(track));

        Assert.Equal(concurrentPlays, library.Tracks.Single().PlayCount);
    }

    // The actual reported bug's mechanism: EndReached (fires on a LibVLC
    // callback thread) racing the startup rescan's UpdateTracks (runs on a
    // threadpool Task.Run - see App.axaml.cs) used to let the rescan's swap
    // land between "resolve the current track" and "increment it", discarding
    // the play. Library's lock makes the two operations mutually exclusive, so
    // regardless of which one the scheduler runs first, the increment always
    // ends up reflected in the post-rescan track - never silently dropped.
    [Fact]
    public async Task IncrementPlayCount_racing_a_concurrent_rescan_never_loses_the_increment()
    {
        var oldTrack = new Track { Path = "/music/a.mp3" };
        var library = new Library(new List<Track> { oldTrack });

        var incrementTask = Task.Run(() => library.IncrementPlayCount(oldTrack), TestContext.Current.CancellationToken);
        var rescanTask = Task.Run(() => library.UpdateTracks(new List<Track> { new Track { Path = "/music/a.mp3" } }), TestContext.Current.CancellationToken);
        await Task.WhenAll(incrementTask, rescanTask);

        Assert.Equal(1, library.Tracks.Single().PlayCount);
    }

    // A sync placeholder (Path == null, OriginDeviceFingerprint set - see
    // LibrarySyncService) must survive a rescan even though it was never on
    // disk. This is distinct from a plain Path == null Track with no
    // OriginDeviceFingerprint (e.g. UpdateTracks_replaces_the_track_list's
    // "Old" track above), which still gets wiped like before - only a genuine
    // sync placeholder is special-cased.
    [Fact]
    public void UpdateTracks_preserves_a_sync_placeholder_across_a_rescan()
    {
        var placeholder = new Track { Title = "Remote Song", Path = null, OriginDeviceFingerprint = "peer-1" };
        var library = new Library(new List<Track> { placeholder });

        library.UpdateTracks(new List<Track> { new Track { Title = "Local", Path = "/music/local.mp3" } });

        Assert.Equal(2, library.Tracks.Count);
        Assert.Contains(library.Tracks, t => t.Title == "Remote Song" && t.OriginDeviceFingerprint == "peer-1");
    }

    // A track downloaded via LibraryDownloadService (Path now set, but still
    // carrying OriginDeviceFingerprint) must also survive a rescan that doesn't
    // happen to find it - e.g. Android, where a downloaded file lives in
    // app-private storage the system MediaStore scan never indexes. Without
    // this, such a track would vanish the very next time the app rescans.
    [Fact]
    public void UpdateTracks_preserves_a_downloaded_sync_track_the_fresh_scan_does_not_find()
    {
        var downloaded = new Track { Title = "Downloaded Song", Path = "/private/app/downloads/abc.mp3", OriginDeviceFingerprint = "peer-1" };
        var library = new Library(new List<Track> { downloaded });

        // Simulates an Android MediaStore rescan that only ever sees system-indexed
        // files - it has no way to find something written to app-private storage.
        library.UpdateTracks(new List<Track> { new Track { Title = "Local", Path = "/music/local.mp3" } });

        Assert.Equal(2, library.Tracks.Count);
        Assert.Contains(library.Tracks, t => t.Title == "Downloaded Song" && t.Path == "/private/app/downloads/abc.mp3");
    }

    // The flip side: if the fresh scan *does* independently find the same file
    // (e.g. iOS's Documents-folder scan re-discovering a track this device
    // downloaded earlier), the old sync-tracked instance must NOT also be carried
    // forward - otherwise the same file would show up as two rows.
    [Fact]
    public void UpdateTracks_does_not_duplicate_a_downloaded_sync_track_the_fresh_scan_also_finds()
    {
        var downloaded = new Track { Title = "Downloaded Song", Path = "/private/app/Documents/abc.mp3", OriginDeviceFingerprint = "peer-1" };
        var library = new Library(new List<Track> { downloaded });

        var rescanned = new Track { Title = "Downloaded Song (retagged)", Path = "/private/app/Documents/abc.mp3" };
        library.UpdateTracks(new List<Track> { rescanned });

        var only = Assert.Single(library.Tracks);
        Assert.Same(rescanned, only);
    }

    // Confirmed on a real device: iOS can reassign the sandboxed app's Data
    // container UUID across a reinstall, shifting every absolute path under
    // it - a downloaded file's Path (and the freshly-rescanned Track's Path
    // for that same, unmoved, unchanged physical file) can then differ by
    // container UUID alone even though it's the exact same file. The plain
    // Path-based dedup above can't recognize that, so UpdateTracks also falls
    // back to matching by SyncKey (Title/Artist/Album/Duration - unaffected
    // by where the file happens to sit) for any OriginDeviceFingerprint-
    // carrying track, to avoid the old-container entry surviving as a
    // duplicate alongside the new one.
    [Fact]
    public void UpdateTracks_does_not_duplicate_a_downloaded_sync_track_the_fresh_scan_finds_at_a_different_path()
    {
        var downloaded = new Track
        {
            Title = "Fabienk", Artists = "Angine de Poitrine", Album = "Vol.II", Duration = TimeSpan.FromSeconds(391),
            Path = "/var/mobile/Containers/Data/Application/OLD-UUID/Documents/abc.mp3",
            OriginDeviceFingerprint = "peer-1",
            RemotePlayCounts = new Dictionary<string, int> { ["peer-1"] = 16 },
        };
        var library = new Library(new List<Track> { downloaded });

        var rescanned = new Track
        {
            Title = "Fabienk", Artists = "Angine de Poitrine", Album = "Vol.II", Duration = TimeSpan.FromSeconds(391),
            Path = "/var/mobile/Containers/Data/Application/NEW-UUID/Documents/abc.mp3",
        };
        library.UpdateTracks(new List<Track> { rescanned });

        var only = Assert.Single(library.Tracks);
        Assert.Same(rescanned, only);
        Assert.Equal("peer-1", only.OriginDeviceFingerprint);
        Assert.Equal(16, only.RemotePlayCounts["peer-1"]);
    }

    [Fact]
    public void MergeSyncedTracks_inserts_a_new_placeholder_for_a_track_not_already_known()
    {
        var library = new Library(new List<Track> { new Track { Title = "Local", Artists = "A", Album = "Al", Duration = TimeSpan.FromSeconds(100), Path = "/music/local.mp3" } });
        var remote = new Track { Title = "Remote", Artists = "B", Album = "Bl", Duration = TimeSpan.FromSeconds(200), OriginDeviceFingerprint = "peer-1" };

        library.MergeSyncedTracks("peer-1", new List<Track> { remote });

        Assert.Equal(2, library.Tracks.Count);
        var inserted = library.Tracks.Single(t => t.Title == "Remote");
        Assert.Null(inserted.Path);
        Assert.Equal("peer-1", inserted.OriginDeviceFingerprint);
    }

    [Fact]
    public void MergeSyncedTracks_updates_OriginDeviceFingerprint_for_an_existing_placeholder()
    {
        var placeholder = new Track { Title = "Remote", Artists = "B", Album = "Bl", Duration = TimeSpan.FromSeconds(200), OriginDeviceFingerprint = "old-peer" };
        var library = new Library(new List<Track> { placeholder });
        var remoteAgain = new Track { Title = "Remote", Artists = "B", Album = "Bl", Duration = TimeSpan.FromSeconds(200), OriginDeviceFingerprint = "new-peer" };

        library.MergeSyncedTracks("new-peer", new List<Track> { remoteAgain });

        Assert.Single(library.Tracks);
        Assert.Equal("new-peer", library.Tracks.Single().OriginDeviceFingerprint);
    }

    [Fact]
    public void MergeSyncedTracks_updates_OriginAlbumArtHash_for_an_existing_placeholder()
    {
        var placeholder = new Track { Title = "Remote", Artists = "B", Album = "Bl", Duration = TimeSpan.FromSeconds(200), OriginDeviceFingerprint = "peer-1", OriginAlbumArtHash = "old-hash" };
        var library = new Library(new List<Track> { placeholder });
        var remoteAgain = new Track { Title = "Remote", Artists = "B", Album = "Bl", Duration = TimeSpan.FromSeconds(200), OriginDeviceFingerprint = "peer-1", OriginAlbumArtHash = "new-hash" };

        library.MergeSyncedTracks("peer-1", new List<Track> { remoteAgain });

        Assert.Equal("new-hash", library.Tracks.Single().OriginAlbumArtHash);
    }

    [Fact]
    public void MergeSyncedTracks_never_replaces_Path_for_a_track_already_backed_by_a_real_file_but_still_records_its_origin()
    {
        // Path itself is never overwritten - this device's own real file always
        // wins. OriginDeviceFingerprint IS still recorded even so (unlike an
        // earlier version of this behavior), so a later "delete downloaded
        // file" action can tell this copy is recoverable from that peer rather
        // than treating it as an unrecoverable delete - see
        // MobileMainViewModel.CanDeleteDownloadedFile/IsRecoverableDownload.
        var local = new Track { Title = "Same Song", Artists = "A", Album = "Al", Duration = TimeSpan.FromSeconds(100), Path = "/music/local.mp3" };
        var library = new Library(new List<Track> { local });
        var remote = new Track { Title = "Same Song", Artists = "A", Album = "Al", Duration = TimeSpan.FromSeconds(100), OriginDeviceFingerprint = "peer-1" };

        library.MergeSyncedTracks("peer-1", new List<Track> { remote });

        Assert.Single(library.Tracks);
        Assert.Same(local, library.Tracks.Single());
        Assert.Equal("/music/local.mp3", library.Tracks.Single().Path);
        Assert.Equal("peer-1", library.Tracks.Single().OriginDeviceFingerprint);
    }

    [Fact]
    public void MergeSyncedTracks_merges_remote_play_counts_into_a_track_already_backed_by_a_real_file()
    {
        // Unlike OriginDeviceFingerprint/Path (see the test above), play counts
        // ARE news even for a track this device already has for real - a peer
        // may have played its own copy of the same song, and that should still
        // count toward the total shown here.
        var local = new Track { Title = "Same Song", Artists = "A", Album = "Al", Duration = TimeSpan.FromSeconds(100), Path = "/music/local.mp3", PlayCount = 3 };
        var library = new Library(new List<Track> { local });
        var remote = new Track
        {
            Title = "Same Song", Artists = "A", Album = "Al", Duration = TimeSpan.FromSeconds(100),
            OriginDeviceFingerprint = "peer-1",
            RemotePlayCounts = new Dictionary<string, int> { ["peer-1"] = 6 },
        };

        library.MergeSyncedTracks("peer-1", new List<Track> { remote });

        var merged = library.Tracks.Single();
        Assert.Same(local, merged);
        Assert.Equal(3, merged.PlayCount); // Local's own count is untouched, not overwritten.
        Assert.Equal(6, merged.RemotePlayCounts["peer-1"]);
        Assert.Equal(9, merged.TotalPlayCount);
    }

    [Fact]
    public void MergeSyncedTracks_merges_remote_play_counts_into_an_existing_placeholder_by_max()
    {
        var placeholder = new Track
        {
            Title = "Remote", Artists = "B", Album = "Bl", Duration = TimeSpan.FromSeconds(200),
            OriginDeviceFingerprint = "peer-1",
            RemotePlayCounts = new Dictionary<string, int> { ["peer-1"] = 5, ["peer-2"] = 10 },
        };
        var library = new Library(new List<Track> { placeholder });
        var remoteAgain = new Track
        {
            Title = "Remote", Artists = "B", Album = "Bl", Duration = TimeSpan.FromSeconds(200),
            OriginDeviceFingerprint = "peer-1",
            // peer-1 played it twice more since the last sync (5 -> 7); peer-2's
            // count reported here is stale/lower than what's already known (10 ->
            // 8, e.g. a report relayed through a third device with older
            // information) and must not regress the existing higher value.
            RemotePlayCounts = new Dictionary<string, int> { ["peer-1"] = 7, ["peer-2"] = 8 },
        };

        library.MergeSyncedTracks("peer-1", new List<Track> { remoteAgain });

        var merged = library.Tracks.Single();
        Assert.Equal(7, merged.RemotePlayCounts["peer-1"]);
        Assert.Equal(10, merged.RemotePlayCounts["peer-2"]);
    }

    [Fact]
    public void MergeSyncedTracks_never_removes_a_track_backed_by_a_real_file()
    {
        // Doubly safe here: Path != null exempts it from pruning outright, and
        // it has no OriginDeviceFingerprint at all (never synced), so it
        // wouldn't match "peer-1" even if it were a placeholder.
        var local = new Track { Title = "Local Only", Path = "/music/local.mp3" };
        var library = new Library(new List<Track> { local });

        library.MergeSyncedTracks("peer-1", new List<Track>());

        Assert.Single(library.Tracks);
        Assert.Same(local, library.Tracks.Single());
    }

    [Fact]
    public void MergeSyncedTracks_prunes_a_placeholder_the_syncing_peer_no_longer_has()
    {
        var stale = new Track { Title = "Gone Song", Artists = "A", Album = "Al", Duration = TimeSpan.FromSeconds(100), OriginDeviceFingerprint = "peer-1" };
        var library = new Library(new List<Track> { stale });

        var removedCount = library.MergeSyncedTracks("peer-1", new List<Track>());

        Assert.Empty(library.Tracks);
        Assert.Equal(1, removedCount);
    }

    [Fact]
    public void MergeSyncedTracks_does_not_prune_a_placeholder_from_a_different_peer()
    {
        // "peer-2" is being synced with here, not "peer-1" - a placeholder
        // last known to come from some other, currently-unrelated peer (e.g.
        // left over from before a re-pair) must survive this sync untouched.
        var fromOtherPeer = new Track { Title = "Elsewhere Song", Artists = "A", Album = "Al", Duration = TimeSpan.FromSeconds(100), OriginDeviceFingerprint = "peer-1" };
        var library = new Library(new List<Track> { fromOtherPeer });

        var removedCount = library.MergeSyncedTracks("peer-2", new List<Track>());

        Assert.Single(library.Tracks);
        Assert.Equal(0, removedCount);
    }

    [Fact]
    public void MergeSyncedTracks_does_not_prune_a_real_file_the_syncing_peer_no_longer_mentions()
    {
        // The core "only prune what has no real file" rule - a downloaded (or
        // locally-imported) track is never deleted just because its origin
        // server's manifest stopped mentioning it.
        var downloaded = new Track
        {
            Title = "Downloaded Song", Artists = "A", Album = "Al", Duration = TimeSpan.FromSeconds(100),
            Path = "/music/downloaded.mp3", OriginDeviceFingerprint = "peer-1",
        };
        var library = new Library(new List<Track> { downloaded });

        var removedCount = library.MergeSyncedTracks("peer-1", new List<Track>());

        Assert.Single(library.Tracks);
        Assert.Same(downloaded, library.Tracks.Single());
        Assert.Equal(0, removedCount);
    }

    [Fact]
    public void AddPlaylist_appends_to_Playlists()
    {
        var library = new Library(new List<Track>());
        var playlist = new Playlist("Mix", new List<Track>());

        library.AddPlaylist(playlist);

        Assert.Single(library.Playlists);
        Assert.Same(playlist, library.Playlists.Single());
    }

    [Fact]
    public void RemovePlaylist_removes_the_given_playlist()
    {
        var library = new Library(new List<Track>());
        var keep = new Playlist("Keep", new List<Track>());
        var remove = new Playlist("Remove", new List<Track>());
        library.AddPlaylist(keep);
        library.AddPlaylist(remove);

        library.RemovePlaylist(remove);

        Assert.Single(library.Playlists);
        Assert.Same(keep, library.Playlists.Single());
    }

    [Fact]
    public void RemovePlaylist_for_a_playlist_not_in_the_library_is_a_no_op()
    {
        var library = new Library(new List<Track>());
        var playlist = new Playlist("Mix", new List<Track>());
        library.AddPlaylist(playlist);

        library.RemovePlaylist(new Playlist("Not Present", new List<Track>()));

        Assert.Single(library.Playlists);
    }

    // ── Rescan carry-forward and playlist rebinding ──────────────────────────

    [Fact]
    public void UpdateTracks_preserves_Id_for_a_track_matched_by_path()
    {
        var existing = new Track { Title = "Old", Path = "/music/a.mp3" };
        var library = new Library(new List<Track> { existing });

        library.UpdateTracks(new List<Track> { new Track { Title = "Old (retagged)", Path = "/music/a.mp3" } });

        Assert.Equal(existing.Id, library.Tracks.Single().Id);
    }

    // A rescan replaces Library.Tracks with brand-new Track instances. Playlists
    // resolve their membership to instances exactly once, at startup - so
    // without rebinding, a playlist spent the rest of the session pointing at
    // orphaned objects: a play count incremented via the library never showed up
    // in the playlist view, and vice versa.
    [Fact]
    public void UpdateTracks_repoints_playlists_at_the_freshly_scanned_track_instances()
    {
        var original = new Track { Title = "A", Path = "/music/a.mp3" };
        var library = new Library(new List<Track> { original });
        var playlist = new Playlist("Mix", new List<Track> { original });
        library.AddPlaylist(playlist);

        library.UpdateTracks(new List<Track> { new Track { Title = "A", Path = "/music/a.mp3" } });

        Assert.Same(library.Tracks.Single(), playlist.Tracks.Single());

        // And the shared instance really is shared, which is the whole point.
        library.IncrementPlayCount(playlist.Tracks.Single());
        Assert.Equal(1, playlist.Tracks.Single().PlayCount);
    }

    // Rebinding is not an edit. PlaylistSyncPlanner three-way-merges on
    // Playlist.UpdatedAt, so bumping it here would make every launch's rescan
    // look like a local playlist change and manufacture sync conflicts.
    [Fact]
    public void UpdateTracks_does_not_bump_playlist_UpdatedAt_when_rebinding()
    {
        var original = new Track { Title = "A", Path = "/music/a.mp3" };
        var library = new Library(new List<Track> { original });
        var playlist = new Playlist("Mix", new List<Track> { original });
        library.AddPlaylist(playlist);
        var before = playlist.UpdatedAt;

        library.UpdateTracks(new List<Track> { new Track { Title = "A", Path = "/music/a.mp3" } });

        Assert.Equal(before, playlist.UpdatedAt);
    }

    // A scan not finding a file is not proof the file is gone (see the
    // carried-forward sync tracks above for the same reasoning) - dropping the
    // entry would silently edit the user's playlist.
    [Fact]
    public void UpdateTracks_keeps_a_playlist_entry_the_fresh_scan_did_not_find()
    {
        var missing = new Track { Title = "Gone", Path = "/music/gone.mp3" };
        var library = new Library(new List<Track> { missing });
        var playlist = new Playlist("Mix", new List<Track> { missing });
        library.AddPlaylist(playlist);

        library.UpdateTracks(new List<Track> { new Track { Title = "B", Path = "/music/b.mp3" } });

        Assert.Same(missing, playlist.Tracks.Single());
    }

    // ── Tier 1.1 / 1.5: stats changes are not list changes ───────────────────

    [Fact]
    public void IncrementPlayCount_raises_TrackStatsChanged_not_TracksUpdated()
    {
        var track   = new Track { Title = "A", Path = "/music/a.mp3" };
        var library = new Library(new List<Track> { track });

        var tracksUpdated = 0;
        Track? statsChanged = null;
        library.TracksUpdated    += (_, _) => tracksUpdated++;
        library.TrackStatsChanged += (_, e) => statsChanged = e.Track;

        library.IncrementPlayCount(track);

        // A play count bump used to arrive as TracksUpdated, which means a full
        // UI rebuild and a peer library sync - twice per song change.
        Assert.Equal(0, tracksUpdated);
        Assert.Same(track, statsChanged);
    }

    [Fact]
    public void RecordPlayed_raises_TrackStatsChanged_with_the_resolved_track()
    {
        var oldTrack = new Track { Title = "A", Path = "/music/a.mp3" };
        var library  = new Library(new List<Track> { oldTrack });

        // A rescan replaces the instance; the event must carry the one that was
        // actually mutated, not the stale reference the caller passed in.
        var rescanned = new Track { Title = "A", Path = "/music/a.mp3" };
        library.UpdateTracks(new List<Track> { rescanned });

        Track? statsChanged = null;
        library.TrackStatsChanged += (_, e) => statsChanged = e.Track;

        library.RecordPlayed(oldTrack);

        Assert.Same(rescanned, statsChanged);
        Assert.NotNull(rescanned.LastPlayedAt);
    }

    [Fact]
    public void IncrementPlayCount_sees_a_Path_set_after_the_index_was_first_built()
    {
        var placeholder = new Track { Title = "A" };
        var library     = new Library(new List<Track> { placeholder });

        // Build the path index while this track still has no Path...
        library.IncrementPlayCount(new Track { Title = "B", Path = "/music/b.mp3" });

        // ...then let a download set one in place, as LibraryDownloadService
        // does, and announce it the way that service does.
        placeholder.Path = "/music/a.mp3";
        library.NotifyTrackChanged();

        var incremented = library.IncrementPlayCount(new Track { Title = "A", Path = "/music/a.mp3" });

        Assert.Same(placeholder, incremented);
        Assert.Equal(1, placeholder.PlayCount);
    }

    // ── Tier 1.5: SyncKey is cached, and the cache is invalidated ────────────

    [Fact]
    public void SyncKey_is_stable_across_reads()
    {
        var track = new Track { Title = "A", Artists = "B", Album = "C", Duration = TimeSpan.FromSeconds(10) };

        Assert.Equal(track.SyncKey, track.SyncKey);
    }

    [Theory]
    [InlineData("title")]
    [InlineData("artists")]
    [InlineData("album")]
    [InlineData("duration")]
    public void SyncKey_is_recomputed_after_an_edit_to_any_field_it_derives_from(string field)
    {
        var track = new Track { Title = "A", Artists = "B", Album = "C", Duration = TimeSpan.FromSeconds(10) };
        var before = track.SyncKey; // Populates the cache.

        switch (field)
        {
            case "title":    track.Title    = "Z"; break;
            case "artists":  track.Artists  = "Z"; break;
            case "album":    track.Album    = "Z"; break;
            case "duration": track.Duration = TimeSpan.FromSeconds(99); break;
        }

        // A tag edit in TrackInfoWindow must not leave a stale key behind - the
        // whole sync layer matches tracks across devices on this.
        Assert.NotEqual(before, track.SyncKey);
    }

    // ── ChangeToken (ARCHITECTURE-REVIEW Tier 1.4) ───────────────────────────

    [Fact]
    public void ChangeToken_is_stable_while_nothing_mutates_the_catalog()
    {
        var library = new Library([new Track { Title = "A", Path = "/music/a.mp3" }]);

        var token = library.ChangeToken;

        Assert.Equal(token, library.ChangeToken);
        // Reading the list is not a mutation - a peer polling /info every ~5s
        // must not see the token move on its own, or it would resync forever.
        _ = library.Tracks.Count;
        Assert.Equal(token, library.ChangeToken);
    }

    [Fact]
    public void ChangeToken_moves_for_every_mutation_that_the_sync_manifest_can_see()
    {
        var track = new Track { Title = "A", Path = "/music/a.mp3" };
        var library = new Library([track]);
        var seen = new HashSet<string> { library.ChangeToken };

        library.UpdateTracks([track, new Track { Title = "B", Path = "/music/b.mp3" }]);
        Assert.True(seen.Add(library.ChangeToken), "UpdateTracks must move the token");

        library.IncrementPlayCount(track);
        Assert.True(seen.Add(library.ChangeToken), "A play count rides along in the manifest");

        library.RecordPlayed(track);
        Assert.True(seen.Add(library.ChangeToken), "LastPlayedAt rides along in the manifest");

        library.MergeSyncedTracks("peer", [new Track { Title = "C", OriginDeviceFingerprint = "peer" }]);
        Assert.True(seen.Add(library.ChangeToken), "MergeSyncedTracks must move the token");

        // A placeholder gaining a Path after a download is an in-place
        // mutation with no list replacement - the one case a naive
        // "did the list change" check would miss.
        library.NotifyTrackChanged();
        Assert.True(seen.Add(library.ChangeToken), "NotifyTrackChanged must move the token");
    }

    [Fact]
    public void Two_libraries_never_share_a_change_token_even_with_identical_contents()
    {
        // The token is session-scoped on purpose: a bare counter would let a
        // restarted device hand a peer a token it already holds for entirely
        // different content, which reads as a false "nothing changed".
        var tracks = new List<Track> { new() { Title = "A", Path = "/music/a.mp3" } };

        Assert.NotEqual(new Library(tracks).ChangeToken, new Library(tracks).ChangeToken);
    }
}
