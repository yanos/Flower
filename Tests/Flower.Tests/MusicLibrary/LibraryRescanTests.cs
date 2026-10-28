using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Flower.Models;
using Flower.Services;

using Xunit;

namespace Flower.Tests.MusicLibrary;

// Library.UpdateTracks, the rescan: what a song keeps when its file is found
// again (id, Date Added, plays), what a sync placeholder or a downloaded copy
// does when the scan does or does not find it, renamed and copied files, and
// play counts landing on the right track under a concurrent rescan.
public class LibraryRescanTests
{
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
    public void UpdateTracks_leaves_DateAdded_alone_for_a_track_with_no_previous_match()
    {
        var freshDate = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var library = new Library(new List<Track> { new Track { Path = "/music/other.mp3" } });

        library.UpdateTracks(new List<Track> { new Track { Path = "/music/new.mp3", DateAdded = freshDate } });

        Assert.Equal(freshDate, library.Tracks.Single().DateAdded);
    }

    // A song this device streams, given its Music.app plays, then downloaded
    // (or found on a scan): the file is the same song, and its imported plays
    // are still this device's tally to report. See docs/SYNC-PLAN.md,
    // "Music.app history from an admin device".
    [Fact]
    public void A_placeholders_imported_plays_survive_it_becoming_a_file()
    {
        var placeholder = new Track
        {
            Title = "Wishwanderer", Artists = "A", Album = "B", Duration = TimeSpan.FromSeconds(200),
            Path = null, OriginDeviceFingerprint = "server", OriginTrackId = "t1", ImportedPlayCount = 19,
        };
        var library = new Library(new List<Track> { placeholder });

        library.UpdateTracks(new List<Track>
        {
            new() { Title = "Wishwanderer", Artists = "A", Album = "B", Duration = TimeSpan.FromSeconds(200), Path = "/music/w.m4a" },
        });

        var song = library.Tracks.Single();
        Assert.Equal("/music/w.m4a", song.Path);
        Assert.Equal(19, song.ImportedPlayCount);
        Assert.Equal("t1", song.OriginTrackId);
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

        var before = DateTimeOffset.UtcNow;
        var incremented = library.IncrementPlayCount(oldTrack);
        var after = DateTimeOffset.UtcNow;

        Assert.Same(newTrack, incremented);
        Assert.Equal(1, newTrack.PlayCount);
        Assert.InRange(newTrack.LastPlayedAt!.Value, before, after);
        Assert.Equal(0, oldTrack.PlayCount);
        Assert.Null(oldTrack.LastPlayedAt);
    }

    // The browser head's case. PlaylistControlViewModel.ResolveForPlaybackAsync
    // hands the audio manager a *clone* of the queued placeholder carrying a
    // minted stream URL in Path, and that clone is what comes back through
    // IncrementPlayCount - so resolving by path alone matched
    // nothing and every play a tab made was counted onto an object thrown away
    // at the next track change. Clone() keeps Id, which is what this resolves on.
    [Fact]
    public void A_played_clone_carrying_a_stream_url_still_counts_against_the_library_track()
    {
        var placeholder = new Track { Title = "On A Server", Path = null, OriginTrackId = "tr-7" };
        var library = new Library(new List<Track> { placeholder });

        var streaming = placeholder.Clone();
        streaming.Path = "http://origin.local/rest/stream?id=tr-7&ticket=abc";

        var incremented = library.IncrementPlayCount(streaming);

        Assert.Same(placeholder, incremented);
        Assert.Equal(1, placeholder.PlayCount);
        Assert.NotNull(placeholder.LastPlayedAt);
        Assert.Equal(0, streaming.PlayCount);
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
    //
    // Says "while its origin is still paired" out loud now, via IsOriginPaired,
    // which is the condition this test always meant and never stated. The
    // counterpart below is what it was quietly asserting the opposite of.
    [Fact]
    public void UpdateTracks_preserves_a_sync_placeholder_across_a_rescan()
    {
        var placeholder = new Track { Title = "Remote Song", Path = null, OriginDeviceFingerprint = "peer-1" };
        var library = new Library(new List<Track> { placeholder })
        {
            IsOriginPaired = fingerprint => fingerprint == "peer-1",
        };

        library.UpdateTracks(new List<Track> { new Track { Title = "Local", Path = "/music/local.mp3" } });

        Assert.Equal(2, library.Tracks.Count);
        Assert.Contains(library.Tracks, t => t.Title == "Remote Song" && t.OriginDeviceFingerprint == "peer-1");
    }

    // The case the test above used to cover too, by saying nothing about
    // pairing at all: a placeholder whose origin this device is no longer
    // paired with. Nothing can ever resolve it - PeerTrackResolver refuses to
    // dial a peer that isn't the paired Server, whatever a stale fingerprint
    // says - so carrying it forward produces a library of rows that look
    // playable and are not. Observed on a real client left holding 86 of them
    // after an unpair, each click logging "no currently paired, reachable
    // origin device" and doing nothing visible.
    [Fact]
    public void UpdateTracks_drops_a_placeholder_whose_origin_is_no_longer_paired()
    {
        var orphaned = new Track { Title = "Remote Song", Path = null, OriginDeviceFingerprint = "old-server" };
        var library = new Library(new List<Track> { orphaned })
        {
            IsOriginPaired = _ => false,
        };

        library.UpdateTracks(new List<Track> { new Track { Title = "Local", Path = "/music/local.mp3" } });

        Assert.Equal("Local", library.Tracks.Single().Title);
    }

    // A file this device downloaded from that server is not a placeholder and
    // is not the pairing's to take away - it is on disk and plays with nothing
    // else involved. Only the never-downloaded rows are the pairing's.
    [Fact]
    public void UpdateTracks_keeps_a_downloaded_file_whose_origin_is_no_longer_paired()
    {
        var downloaded = new Track
        {
            Title = "Downloaded Song", Path = "/private/downloads/abc.mp3",
            OriginDeviceFingerprint = "old-server", IsLocallyDownloaded = true,
        };
        var library = new Library(new List<Track> { downloaded })
        {
            IsOriginPaired = _ => false,
        };

        library.UpdateTracks(new List<Track>());

        Assert.Equal("Downloaded Song", library.Tracks.Single().Title);
    }

    // OriginTrackId is how a downloaded-then-deleted track gets re-fetched
    // later (see LibraryDownloadService.DeleteDownloadedFileAsync, which leaves
    // the origin metadata in place on purpose), so it has to ride the same
    // carry-forward as the rest of it. A rescan re-reads a file's tags and
    // builds a brand new Track from them - nothing on disk carries the peer's
    // id, so losing it here would make the track permanently unrecoverable
    // with no visible symptom until the user tried.
    [Fact]
    public void UpdateTracks_carries_the_origin_track_id_forward_across_a_rescan()
    {
        var downloaded = new Track
        {
            Title = "Downloaded Song", Path = "/music/abc.mp3",
            OriginDeviceFingerprint = "peer-1", OriginTrackId = "peer-track-id",
        };
        var library = new Library(new List<Track> { downloaded });

        library.UpdateTracks(new List<Track> { new Track { Title = "Downloaded Song", Path = "/music/abc.mp3" } });

        Assert.Equal("peer-track-id", library.Tracks.Single().OriginTrackId);
    }

    // A track downloaded via LibraryDownloadService (Path now set, but still
    // carrying OriginDeviceFingerprint) must also survive a rescan that doesn't
    // happen to find it - e.g. Android, where a downloaded file lives in
    // app-private storage the system MediaStore scan never indexes. Without
    // this, such a track would vanish the very next time the app rescans.
    [Fact]
    public void UpdateTracks_preserves_a_downloaded_sync_track_the_fresh_scan_does_not_find()
    {
        // IsLocallyDownloaded, not just an origin fingerprint: the origin says
        // some peer has a copy (true of plenty of ordinary local files once
        // paired - see MergeSyncedTracks), while this says *this* device put the
        // file where it is, which is what makes a scan's silence about it
        // meaningless. See Track.IsLocallyDownloaded.
        var downloaded = new Track
        {
            Title = "Downloaded Song", Path = "/private/app/downloads/abc.mp3",
            OriginDeviceFingerprint = "peer-1", IsLocallyDownloaded = true,
        };
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
        var downloaded = new Track
        {
            Title = "Downloaded Song", Path = "/private/app/Documents/abc.mp3",
            OriginDeviceFingerprint = "peer-1", IsLocallyDownloaded = true,
        };
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

    // The same fallback, for the case it is mostly there for: the user renames
    // or moves a file inside a library folder. Nothing about the track changed
    // except where it sits, so a rescan that treated it as a new file would
    // reset its Id, DateAdded, play counts and stars - making a rename
    // indistinguishable from deleting the track and adding a different one.
    // A server's own library goes through this same method (see
    // Flower.Server's LibraryImportService.RescanAsync), so this is also what
    // stops a rename on the server invalidating the OriginTrackId every paired
    // client is holding.
    [Fact]
    public void UpdateTracks_carries_a_renamed_local_files_history_forward()
    {
        var before = new Track
        {
            Title = "Fabienk", Artists = "Angine de Poitrine", Album = "Vol.II", Duration = TimeSpan.FromSeconds(391),
            Path = "/music/01 fabienk.mp3",
            DateAdded = new DateTime(2019, 4, 2),
            PlayCount = 12,
            Starred = true,
        };
        var library = new Library(new List<Track> { before });

        var renamed = new Track
        {
            Title = "Fabienk", Artists = "Angine de Poitrine", Album = "Vol.II", Duration = TimeSpan.FromSeconds(391),
            Path = "/music/Angine de Poitrine/Vol.II/01 Fabienk.mp3",
        };
        library.UpdateTracks(new List<Track> { renamed });

        var only = Assert.Single(library.Tracks);
        Assert.Same(renamed, only);
        Assert.Equal(before.Id, only.Id);
        Assert.Equal(new DateTime(2019, 4, 2), only.DateAdded);
        Assert.Equal(12, only.PlayCount);
        Assert.True(only.Starred);
    }

    // The guard on that fallback. Copying an album into a second folder and
    // leaving the original in place produces a fresh track whose SyncKey
    // matches one the scan also still finds at its own path - and handing it
    // that track's Id would put two Tracks with one Id in the library, which
    // breaks every by-Id lookup there is (playlist membership, the play queue,
    // Track Info). Only a track the scan did *not* rediscover is claimable,
    // and only once.
    [Fact]
    public void UpdateTracks_does_not_give_a_copy_the_identity_of_the_original_it_sits_beside()
    {
        var original = new Track
        {
            Title = "Fabienk", Album = "Vol.II", Duration = TimeSpan.FromSeconds(391),
            Path = "/music/original.mp3", PlayCount = 12,
        };
        var library = new Library(new List<Track> { original });

        var rescannedOriginal = new Track
        {
            Title = "Fabienk", Album = "Vol.II", Duration = TimeSpan.FromSeconds(391), Path = "/music/original.mp3",
        };
        var copy = new Track
        {
            Title = "Fabienk", Album = "Vol.II", Duration = TimeSpan.FromSeconds(391), Path = "/music/copy.mp3",
        };
        library.UpdateTracks(new List<Track> { rescannedOriginal, copy });

        Assert.Equal(2, library.Tracks.Count);
        Assert.Equal(original.Id, rescannedOriginal.Id);
        Assert.NotEqual(original.Id, copy.Id);
        Assert.Equal(12, rescannedOriginal.PlayCount);
        Assert.Equal(0, copy.PlayCount);
    }

    // The other guard. An untitled track's SyncKey collapses to its duration
    // alone, so every untagged file of the same length in the library shares
    // it - matching on that would hand a genuinely new file the history of
    // some unrelated one that happens to run as long. No title, no match.
    [Fact]
    public void UpdateTracks_does_not_match_an_untitled_file_by_key()
    {
        var untitled = new Track { Path = "/music/track01.wav", Duration = TimeSpan.FromSeconds(180), PlayCount = 9 };
        var library = new Library(new List<Track> { untitled });

        var unrelated = new Track { Path = "/music/track02.wav", Duration = TimeSpan.FromSeconds(180) };
        library.UpdateTracks(new List<Track> { unrelated });

        var only = Assert.Single(library.Tracks);
        Assert.NotEqual(untitled.Id, only.Id);
        Assert.Equal(0, only.PlayCount);
    }

    // The bug this rule was rewritten for, end to end.
    //
    // Once a device pairs, MergeSyncedTracks stamps the server's fingerprint
    // onto local files the server also has - by design, so the mobile
    // delete-a-download warning can tell "re-downloadable" from "permanent".
    // The carry-forward test was OriginDeviceFingerprint != null, which that
    // turned into "every track": from then on no rescan could remove anything.
    // Seen in the field as a client with no library folders configured at all
    // still listing 16k songs, launch after launch, logging "0 track(s) from
    // scan, 16115 synced-only track(s) carried forward" every time.
    [Fact]
    public void UpdateTracks_removes_a_local_file_the_scan_no_longer_finds_even_when_a_peer_also_has_it()
    {
        var localButAlsoOnTheServer = new Track
        {
            Title = "Socket", Path = "/music/socket.mp3", OriginDeviceFingerprint = "server-1",
        };
        var library = new Library(new List<Track> { localButAlsoOnTheServer });

        // What the importer returns once the last library folder is removed:
        // "No library folders configured - nothing to scan".
        library.UpdateTracks(new List<Track>());

        Assert.Empty(library.Tracks);
    }

    // The disk is the source of truth for local files, so removing the folder
    // list empties the library even when every track in it came back from a
    // sync - the sync's own placeholders are a separate question, re-answered
    // by the next MergeSyncedTracks.
    [Fact]
    public void UpdateTracks_empties_a_fully_synced_library_when_no_folders_are_configured()
    {
        var library = new Library(Enumerable.Range(0, 100)
            .Select(i => new Track { Title = $"Song {i}", Path = $"/music/{i}.mp3", OriginDeviceFingerprint = "server-1" })
            .ToList());

        library.UpdateTracks(new List<Track>());

        Assert.Empty(library.Tracks);
    }

    // What UnpairServer calls (see PeerSyncCoordinator): the pairing going away
    // takes the placeholders with it, because a placeholder is nothing but a
    // promise that peer would serve the file on request.
    [Fact]
    public void RemoveTracksFromOrigin_drops_that_origins_placeholders()
    {
        var library = new Library(new List<Track>
        {
            new Track { Title = "From The Server", Path = null, OriginDeviceFingerprint = "server-1" },
            new Track { Title = "From Someone Else", Path = null, OriginDeviceFingerprint = "server-2" },
            new Track { Title = "Mine", Path = "/music/mine.mp3" },
        });

        var removed = library.RemoveTracksFromOrigin("server-1");

        Assert.Equal(1, removed);
        Assert.DoesNotContain(library.Tracks, t => t.Title == "From The Server");
        Assert.Contains(library.Tracks, t => t.Title == "From Someone Else");
        Assert.Contains(library.Tracks, t => t.Title == "Mine");
    }

    // A real file stays and only loses the origin metadata: it is this device's
    // own copy whatever the sync once said about who else had one, and the
    // stale fingerprint would otherwise leave the mobile delete-a-download
    // warning still calling it re-downloadable from a server we unpaired from.
    [Fact]
    public void RemoveTracksFromOrigin_keeps_a_downloaded_file_and_clears_its_origin()
    {
        var downloaded = new Track
        {
            Title = "Downloaded Song", Path = "/private/downloads/abc.mp3",
            OriginDeviceFingerprint = "server-1", OriginTrackId = "peer-track-id",
            OriginFileExtension = "mp3", OriginAlbumArtId = "al-art",
            IsLocallyDownloaded = true,
        };
        var library = new Library(new List<Track> { downloaded });

        var removed = library.RemoveTracksFromOrigin("server-1");

        Assert.Equal(0, removed);
        var kept = library.Tracks.Single();
        Assert.Equal("/private/downloads/abc.mp3", kept.Path);
        Assert.Null(kept.OriginDeviceFingerprint);
        Assert.Null(kept.OriginTrackId);
        Assert.Null(kept.OriginFileExtension);
        Assert.Null(kept.OriginAlbumArtId);
    }

    // A track with no local file survives whatever its origin. Usually that is a
    // sync placeholder, but DeleteDownloadedFileAsync also produces one with no
    // origin at all (deleting the only copy of a purely local import - see its
    // own comment, and MobileMainViewModel.IsRecoverableDownload, which warns
    // about exactly that case rather than preventing it). Either way there is
    // nothing on disk for a scan to find, so a scan not finding it is not
    // evidence of anything.
    [Fact]
    public void UpdateTracks_keeps_a_track_with_no_local_file_whatever_its_origin()
    {
        var orphaned = new Track { Title = "Deleted Download", Path = null };
        var library = new Library(new List<Track> { orphaned });

        library.UpdateTracks(new List<Track> { new Track { Title = "Local", Path = "/music/local.mp3" } });

        Assert.Contains(library.Tracks, t => t.Title == "Deleted Download");
    }

    [Fact]
    public void UpdateTracks_preserves_Id_for_a_track_matched_by_path()
    {
        var existing = new Track { Title = "Old", Path = "/music/a.mp3" };
        var library = new Library(new List<Track> { existing });

        library.UpdateTracks(new List<Track> { new Track { Title = "Old (retagged)", Path = "/music/a.mp3" } });

        Assert.Equal(existing.Id, library.Tracks.Single().Id);
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
        library.NotifyTrackChanged(placeholder, TrackChange.File);

        var incremented = library.IncrementPlayCount(new Track { Title = "A", Path = "/music/a.mp3" });

        Assert.Same(placeholder, incremented);
        Assert.Equal(1, placeholder.PlayCount);
    }
}
