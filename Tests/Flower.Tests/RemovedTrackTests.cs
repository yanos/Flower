using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;
using Flower.Services;

namespace Flower.Tests;

// A song that leaves the library is kept on record, and one that comes back is
// the song it was - see Library.RemovedTracks. These pin the two halves: what
// goes on record and with what owed to a paired server, and what a returning
// file gets back.
public class RemovedTrackTests
{
    private static readonly DateTimeOffset LongAgo = new(2019, 3, 4, 0, 0, 0, TimeSpan.Zero);

    private static Track Song(string title, string? path = null, double seconds = 200) => new()
    {
        Title = title,
        Artists = "Artist",
        Album = "Album",
        Duration = TimeSpan.FromSeconds(seconds),
        Path = path ?? $"/music/{title}.mp3",
    };

    private static Track Lived(string title, string? path = null)
    {
        var track = Song(title, path);
        track.DateAdded = LongAgo;
        track.PlayCount = 7;
        track.ImportedPlayCount = 2;
        track.LastPlayedAt = LongAgo.AddDays(30);
        track.Starred = true;
        track.StarredAt = LongAgo.AddDays(1);
        track.IgnoreWhenShuffling = true;
        track.VolumeAdjustment = -20;
        track.RemotePlayCounts["phone"] = 4;
        return track;
    }

    private static void AssertRestored(Track original, Track returned)
    {
        Assert.Equal(original.Id, returned.Id);
        Assert.Equal(LongAgo, returned.DateAdded);
        Assert.Equal(7, returned.PlayCount);
        Assert.Equal(2, returned.ImportedPlayCount);
        Assert.Equal(LongAgo.AddDays(30), returned.LastPlayedAt);
        Assert.True(returned.Starred);
        Assert.Equal(LongAgo.AddDays(1), returned.StarredAt);
        Assert.True(returned.IgnoreWhenShuffling);
        Assert.Equal(-20, returned.VolumeAdjustment);
        Assert.Equal(4, returned.RemotePlayCounts["phone"]);
    }

    // The case this exists for: a music folder that was not there for one scan.
    [Fact]
    public void A_file_a_scan_stops_finding_and_then_finds_again_is_the_song_it_was()
    {
        var original = Lived("Song");
        var library = new Library([original]);

        library.UpdateTracks([]);
        Assert.Empty(library.Tracks);
        Assert.Single(library.RemovedTracks);

        library.UpdateTracks([Song("Song")]);

        AssertRestored(original, library.Tracks.Single());
        Assert.Empty(library.RemovedTracks);
    }

    [Fact]
    public void A_file_that_comes_back_somewhere_else_is_recognised_by_what_it_is()
    {
        var original = Lived("Song");
        var library = new Library([original]);
        library.UpdateTracks([]);

        library.UpdateTracks([Song("Song", "/elsewhere/Song.mp3")]);

        AssertRestored(original, library.Tracks.Single());
    }

    // No title, no artist, no album: its key is its length and nothing else,
    // and every untagged file of that length shares it.
    [Fact]
    public void An_untitled_file_is_recognised_only_where_it_was()
    {
        var original = new Track { Path = "/music/untitled.mp3", Duration = TimeSpan.FromSeconds(200), PlayCount = 5 };
        var library = new Library([original]);
        library.UpdateTracks([]);

        library.UpdateTracks([new Track { Path = "/elsewhere/other.mp3", Duration = TimeSpan.FromSeconds(200) }]);
        Assert.Equal(0, library.Tracks.Single().PlayCount);
        Assert.Single(library.RemovedTracks);

        library.UpdateTracks([new Track { Path = "/music/untitled.mp3", Duration = TimeSpan.FromSeconds(200) }]);
        Assert.Equal(5, library.Tracks.Single().PlayCount);
        Assert.Equal(original.Id, library.Tracks.Single().Id);
    }

    [Fact]
    public void A_song_removed_on_purpose_and_put_back_is_the_song_it_was()
    {
        var original = Lived("Song");
        var library = new Library([original]);

        library.RemoveTracks([original], excludePaths: []);
        Assert.Single(library.RemovedTracks);

        library.UpdateTracks([Song("Song")]);

        AssertRestored(original, library.Tracks.Single());
    }

    // The record is what the library knew when the song left. The instance
    // that left may still be in a play queue, being played.
    [Fact]
    public void What_goes_on_record_is_a_copy()
    {
        var original = Lived("Song");
        var library = new Library([original]);
        library.RemoveTracks([original], excludePaths: []);

        original.PlayCount = 99;

        Assert.Equal(7, library.RemovedTracks.Single().Track.PlayCount);
    }

    [Fact]
    public void One_record_restores_one_file()
    {
        var original = Lived("Song");
        var library = new Library([original]);
        library.UpdateTracks([]);

        library.UpdateTracks([Song("Song", "/a/Song.mp3"), Song("Song", "/b/Song.mp3")]);

        Assert.Equal(2, library.Tracks.Select(t => t.Id).Distinct().Count());
        Assert.Single(library.Tracks, t => t.Id == original.Id);
        Assert.Single(library.Tracks, t => t.PlayCount == 7);
    }

    // ── What is owed to a paired server ──────────────────────────────────

    [Fact]
    public void A_missing_file_the_server_knows_is_owed_to_it_and_one_it_does_not_know_is_not()
    {
        var known = Song("Known");
        known.OriginDeviceFingerprint = "server";
        known.OriginTrackId = "s1";
        var library = new Library([known, Song("Private")]);

        library.UpdateTracks([]);

        Assert.True(library.RemovedTracks.Single(r => r.Track.Title == "Known").OwedToOrigin);
        Assert.False(library.RemovedTracks.Single(r => r.Track.Title == "Private").OwedToOrigin);
    }

    // The server was told before anything was removed here - see
    // LibraryRemovalService.
    [Fact]
    public void A_removal_asked_for_owes_the_server_nothing()
    {
        var known = Song("Known");
        known.OriginDeviceFingerprint = "server";
        known.OriginTrackId = "s1";
        var library = new Library([known]);

        library.RemoveTracks([known], excludePaths: []);

        Assert.False(library.RemovedTracks.Single().OwedToOrigin);
    }

    // A download is the server's song, lent. A scan not finding it is the
    // scan not looking where downloads live.
    [Fact]
    public void A_downloaded_file_a_scan_does_not_find_is_not_a_removal()
    {
        var downloaded = Song("Lent");
        downloaded.IsLocallyDownloaded = true;
        downloaded.OriginDeviceFingerprint = "server";
        downloaded.OriginTrackId = "s1";
        var library = new Library([downloaded]);

        library.UpdateTracks([]);

        Assert.Single(library.Tracks);
        Assert.Empty(library.RemovedTracks);
    }

    [Fact]
    public void Settling_a_record_keeps_it_and_stops_it_being_owed()
    {
        var known = Lived("Known");
        known.OriginDeviceFingerprint = "server";
        known.OriginTrackId = "s1";
        var library = new Library([known]);
        library.UpdateTracks([]);

        library.SettleRemovedTracks([known.Id]);

        Assert.False(library.RemovedTracks.Single().OwedToOrigin);
        library.UpdateTracks([Song("Known")]);
        AssertRestored(known, library.Tracks.Single());
    }

    // ── A file arriving on its own ───────────────────────────────────────

    [Fact]
    public void An_arriving_file_the_library_has_had_before_is_restored_from_the_record()
    {
        var original = Lived("Song");
        var library = new Library([original]);
        library.RemoveTracks([original], excludePaths: []);

        var added = library.AddScannedTrack(Song("Song", "/music/again/Song.mp3"), knownSince: DateTimeOffset.UtcNow);

        AssertRestored(original, added);
        Assert.Same(added, library.Tracks.Single());
        Assert.Empty(library.RemovedTracks);
    }

    // The device it came from has had it since before this library existed.
    [Fact]
    public void An_arriving_file_is_dated_by_whoever_has_known_it_longest()
    {
        var library = new Library([]);

        var added = library.AddScannedTrack(Song("New"), knownSince: LongAgo);

        Assert.Equal(LongAgo, added.DateAdded);
    }

    [Fact]
    public void A_date_from_the_future_is_not_a_date_added()
    {
        var library = new Library([]);

        var added = library.AddScannedTrack(Song("New"), knownSince: DateTimeOffset.UtcNow.AddYears(1));

        Assert.True(added.DateAdded <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public void An_arriving_file_at_a_path_already_in_the_library_replaces_that_track_and_keeps_its_state()
    {
        var original = Lived("Song");
        var library = new Library([original]);

        var added = library.AddScannedTrack(Song("Song"));

        Assert.Same(added, library.Tracks.Single());
        Assert.Equal(original.Id, added.Id);
        Assert.Equal(7, added.PlayCount);
    }

    // ── A pull, on a device that has had the song before ─────────────────

    private static Track FromServer(string title, string id, DateTimeOffset? dateAdded = null) => new()
    {
        Title = title,
        Artists = "Artist",
        Album = "Album",
        Duration = TimeSpan.FromSeconds(200),
        OriginDeviceFingerprint = "server",
        OriginTrackId = id,
        DateAdded = dateAdded ?? DateTimeOffset.UtcNow,
    };

    // A drive that is not plugged in: the songs on it become the server's
    // placeholders, and every playlist holding them goes on holding them.
    [Fact]
    public void A_song_whose_file_is_gone_comes_back_from_the_server_under_the_id_it_had()
    {
        var original = Lived("Song");
        original.OriginDeviceFingerprint = "server";
        original.OriginTrackId = "s1";
        var library = new Library([original]);
        library.UpdateTracks([]);

        library.MergeSyncedTracks("server", [FromServer("Song", "s1")]);

        var placeholder = library.Tracks.Single();
        Assert.Null(placeholder.Path);
        Assert.Equal(original.Id, placeholder.Id);
        Assert.Equal(7, placeholder.PlayCount);
        Assert.Empty(library.RemovedTracks);
    }

    // ── Date Added, between an owner's device and its server ─────────────

    [Fact]
    public void An_admin_device_keeps_the_older_date_added_and_a_listener_takes_the_servers()
    {
        var recent = LongAgo.AddYears(5);

        var owner = new Library([Lived("Song")]);
        owner.MergeSyncedTracks("server", [FromServer("Song", "s1", recent)], ownersDevice: true);
        Assert.Equal(LongAgo, owner.Tracks.Single().DateAdded);

        var listener = new Library([Lived("Song")]);
        listener.MergeSyncedTracks("server", [FromServer("Song", "s1", recent)]);
        Assert.Equal(recent, listener.Tracks.Single().DateAdded);
    }

    [Fact]
    public void An_admin_device_takes_the_servers_date_added_when_that_is_the_older()
    {
        var owner = new Library([Song("Song")]);

        owner.MergeSyncedTracks("server", [FromServer("Song", "s1", LongAgo)], ownersDevice: true);

        Assert.Equal(LongAgo, owner.Tracks.Single().DateAdded);
    }

    [Fact]
    public void A_reported_date_added_moves_the_servers_backwards_only_and_only_for_an_admin()
    {
        var track = Song("Song");
        track.DateAdded = LongAgo.AddYears(2);
        var server = new Library([track]);
        var id = track.Id.ToKey();

        server.MergeReportedTrackState("guest", [new TrackStateDto(id, 0, DateAdded: LongAgo)], callerIsAdmin: false);
        Assert.Equal(LongAgo.AddYears(2), track.DateAdded);

        server.MergeReportedTrackState("owner", [new TrackStateDto(id, 0, DateAdded: LongAgo.AddYears(3))], callerIsAdmin: true);
        Assert.Equal(LongAgo.AddYears(2), track.DateAdded);

        var moved = server.MergeReportedTrackState("owner", [new TrackStateDto(id, 0, DateAdded: LongAgo)], callerIsAdmin: true);
        Assert.Equal(1, moved);
        Assert.Equal(LongAgo, track.DateAdded);
    }

    // ── A local copy of a song the server dropped ────────────────────────

    [Fact]
    public void A_local_file_the_server_stops_listing_is_marked_withdrawn_until_the_server_lists_it_again()
    {
        var mine = Song("Song");
        var library = new Library([mine]);
        library.MergeSyncedTracks("server", [FromServer("Song", "s1")]);
        Assert.False(mine.WithdrawnByOrigin);

        library.MergeSyncedTracks("server", []);
        Assert.True(mine.WithdrawnByOrigin);
        Assert.Null(mine.OriginTrackId);

        library.MergeSyncedTracks("server", [FromServer("Song", "s2")]);
        Assert.False(mine.WithdrawnByOrigin);
        Assert.Equal("s2", mine.OriginTrackId);
    }

    // ── A song the server says was removed ───────────────────────────────

    // "Removed" travels as a statement. An owner's device lets go of its own
    // copy; the file itself is set aside, not deleted.
    [Fact]
    public void An_owners_copy_of_a_song_the_server_removed_leaves_the_library_and_is_set_aside()
    {
        var mine = Lived("Song");
        var library = new Library([mine]);
        library.MergeSyncedTracks("server", [FromServer("Song", "s1")], ownersDevice: true);

        var gone = library.MergeSyncedTracks(
            "server", [], ownersDevice: true, removedAtSource: new HashSet<string> { "s1" });

        Assert.Equal(1, gone);
        Assert.Empty(library.Tracks);
        Assert.Equal(["/music/Song.mp3"], library.ExcludedPaths.Select(e => e.Path));
        Assert.True(library.RemovedTracks.Single().Deliberate);

        // And a scan that finds the file leaves it out.
        library.UpdateTracks([Song("Song")]);
        Assert.Empty(library.Tracks);
    }

    // A guest's own file is not the owner's to take away.
    [Fact]
    public void A_listeners_own_copy_of_a_song_the_server_removed_stays()
    {
        var mine = Song("Song");
        var library = new Library([mine]);
        library.MergeSyncedTracks("server", [FromServer("Song", "s1")]);

        library.MergeSyncedTracks("server", [], removedAtSource: new HashSet<string> { "s1" });

        Assert.Same(mine, library.Tracks.Single());
        Assert.Null(mine.OriginTrackId);
        Assert.Empty(library.ExcludedPaths);
    }

    // ...but a copy it downloaded was never its own.
    [Fact]
    public void A_downloaded_copy_of_a_song_the_server_removed_goes_on_any_device()
    {
        var lent = Song("Song");
        lent.IsLocallyDownloaded = true;
        var library = new Library([lent]);
        library.MergeSyncedTracks("server", [FromServer("Song", "s1")]);

        library.MergeSyncedTracks("server", [], removedAtSource: new HashSet<string> { "s1" });

        Assert.Empty(library.Tracks);
    }

    // A catalog can be short for reasons that are nobody's decision.
    [Fact]
    public void A_song_merely_missing_from_the_catalog_is_not_a_song_removed()
    {
        var mine = Song("Song");
        var library = new Library([mine]);
        library.MergeSyncedTracks("server", [FromServer("Song", "s1")], ownersDevice: true);

        library.MergeSyncedTracks("server", [], ownersDevice: true, removedAtSource: new HashSet<string>());

        Assert.Same(mine, library.Tracks.Single());
        Assert.True(mine.WithdrawnByOrigin);
        Assert.Empty(library.ExcludedPaths);
    }

    // The song is back on the server: the copy that was set aside here is
    // wanted again, and the next scan takes it back in under the id it had.
    [Fact]
    public void A_set_aside_copy_is_wanted_again_once_the_server_lists_the_song()
    {
        var mine = Lived("Song");
        var library = new Library([mine]);
        library.MergeSyncedTracks("server", [FromServer("Song", "s1")], ownersDevice: true);
        library.MergeSyncedTracks("server", [], ownersDevice: true, removedAtSource: new HashSet<string> { "s1" });

        library.MergeSyncedTracks("server", [FromServer("Song", "s1")], ownersDevice: true);

        Assert.Empty(library.ExcludedPaths);
        Assert.Equal(mine.Id, library.Tracks.Single().Id);

        library.UpdateTracks([Song("Song")]);
        var back = library.Tracks.Single();
        Assert.Equal("/music/Song.mp3", back.Path);
        Assert.Equal(mine.Id, back.Id);
        Assert.Equal(7, back.PlayCount);
    }

    // ── A file found somewhere new ───────────────────────────────────────

    [Fact]
    public void A_moved_file_the_server_knows_remembers_where_it_was_until_the_server_is_told()
    {
        var mine = Song("Song", "/music/old/Song.mp3");
        var library = new Library([mine]);
        library.MergeSyncedTracks("server", [FromServer("Song", "s1")]);

        library.UpdateTracks([Song("Song", "/music/new/Song.mp3")]);
        Assert.Equal("/music/old/Song.mp3", library.Tracks.Single().MovedFromPath);

        // Moved again before the server heard: the first place is the one kept.
        library.UpdateTracks([Song("Song", "/music/newer/Song.mp3")]);
        Assert.Equal("/music/old/Song.mp3", library.Tracks.Single().MovedFromPath);

        library.RecordOrigin(library.Tracks.Single(), "server", "s1", "newer/Song.mp3", null);
        Assert.Null(library.Tracks.Single().MovedFromPath);
    }

    [Fact]
    public void A_moved_file_the_server_does_not_know_owes_nobody_the_news()
    {
        var library = new Library([Song("Song", "/music/old/Song.mp3")]);

        library.UpdateTracks([Song("Song", "/music/new/Song.mp3")]);

        Assert.Null(library.Tracks.Single().MovedFromPath);
    }

    // The file is still the copy the server withdrew, whether or not a drive
    // was unplugged in between.
    [Fact]
    public void A_withdrawn_file_that_goes_missing_and_returns_is_still_withdrawn()
    {
        var mine = Song("Song");
        var library = new Library([mine]);
        library.MergeSyncedTracks("server", [FromServer("Song", "s1")]);
        library.MergeSyncedTracks("server", []);

        library.UpdateTracks([]);
        library.UpdateTracks([Song("Song")]);

        Assert.True(library.Tracks.Single().WithdrawnByOrigin);
    }

    // Removing it and putting it back is the user choosing to have it here
    // again, and that copy is offered like any other.
    [Fact]
    public void A_withdrawn_file_removed_on_purpose_and_put_back_is_no_longer_withdrawn()
    {
        var mine = Song("Song");
        var library = new Library([mine]);
        library.MergeSyncedTracks("server", [FromServer("Song", "s1")]);
        library.MergeSyncedTracks("server", []);

        library.RemoveTracks([mine], excludePaths: []);
        library.UpdateTracks([Song("Song")]);

        Assert.False(library.Tracks.Single().WithdrawnByOrigin);
    }

    [Fact]
    public void Unpairing_forgets_what_that_server_withdrew()
    {
        var mine = Song("Song");
        var library = new Library([mine]);
        library.MergeSyncedTracks("server", [FromServer("Song", "s1")]);
        library.MergeSyncedTracks("server", []);

        library.RemoveTracksFromOrigin("server");

        Assert.False(mine.WithdrawnByOrigin);
    }
}
