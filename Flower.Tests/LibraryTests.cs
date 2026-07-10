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

        var incrementTask = Task.Run(() => library.IncrementPlayCount(oldTrack));
        var rescanTask = Task.Run(() => library.UpdateTracks(new List<Track> { new Track { Path = "/music/a.mp3" } }));
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

    [Fact]
    public void MergeSyncedTracks_inserts_a_new_placeholder_for_a_track_not_already_known()
    {
        var library = new Library(new List<Track> { new Track { Title = "Local", Artists = "A", Album = "Al", Duration = TimeSpan.FromSeconds(100), Path = "/music/local.mp3" } });
        var remote = new Track { Title = "Remote", Artists = "B", Album = "Bl", Duration = TimeSpan.FromSeconds(200), OriginDeviceFingerprint = "peer-1" };

        library.MergeSyncedTracks(new List<Track> { remote });

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

        library.MergeSyncedTracks(new List<Track> { remoteAgain });

        Assert.Single(library.Tracks);
        Assert.Equal("new-peer", library.Tracks.Single().OriginDeviceFingerprint);
    }

    [Fact]
    public void MergeSyncedTracks_updates_OriginAlbumArtHash_for_an_existing_placeholder()
    {
        var placeholder = new Track { Title = "Remote", Artists = "B", Album = "Bl", Duration = TimeSpan.FromSeconds(200), OriginDeviceFingerprint = "peer-1", OriginAlbumArtHash = "old-hash" };
        var library = new Library(new List<Track> { placeholder });
        var remoteAgain = new Track { Title = "Remote", Artists = "B", Album = "Bl", Duration = TimeSpan.FromSeconds(200), OriginDeviceFingerprint = "peer-1", OriginAlbumArtHash = "new-hash" };

        library.MergeSyncedTracks(new List<Track> { remoteAgain });

        Assert.Equal("new-hash", library.Tracks.Single().OriginAlbumArtHash);
    }

    [Fact]
    public void MergeSyncedTracks_does_not_touch_a_track_already_backed_by_a_real_file()
    {
        var local = new Track { Title = "Same Song", Artists = "A", Album = "Al", Duration = TimeSpan.FromSeconds(100), Path = "/music/local.mp3" };
        var library = new Library(new List<Track> { local });
        var remote = new Track { Title = "Same Song", Artists = "A", Album = "Al", Duration = TimeSpan.FromSeconds(100), OriginDeviceFingerprint = "peer-1" };

        library.MergeSyncedTracks(new List<Track> { remote });

        Assert.Single(library.Tracks);
        Assert.Same(local, library.Tracks.Single());
        Assert.Equal("/music/local.mp3", library.Tracks.Single().Path);
        Assert.Null(library.Tracks.Single().OriginDeviceFingerprint);
    }

    [Fact]
    public void MergeSyncedTracks_never_removes_a_track_the_peer_did_not_mention()
    {
        var local = new Track { Title = "Local Only", Path = "/music/local.mp3" };
        var library = new Library(new List<Track> { local });

        library.MergeSyncedTracks(new List<Track>());

        Assert.Single(library.Tracks);
        Assert.Same(local, library.Tracks.Single());
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
}
