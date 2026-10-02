using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Models;
using Flower.Services;
using Flower.Tests.TestSupport;

namespace Flower.Tests;

// A song's tags as one value (TrackTags), and the rule for whose tags a song
// ends up with when a device and its server each have some (Track.TagsEditedAt,
// Library.MergeSyncedTracks): the newest edit wins, and "never edited" is older
// than any edit.
public sealed class TrackTagsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("flower-tags").FullName;

    public void Dispose() => TempDirectory.DeleteWhenReleased(_root);

    private static readonly TrackTagsDto Everything = new(
        Title: "Title", Artists: "One, Two", Album: "Album", AlbumArtists: "Album Artist",
        TrackNumber: 3, TrackCount: 12, DiscNumber: 1, DiscCount: 2,
        Year: "1997", Genre: "Genre", BeatsPerMinute: 120, InitialKey: "Am", Grouping: "Grouping",
        Composers: "Composer", Conductor: "Conductor", RemixedBy: "Remixer",
        Subtitle: "Subtitle", Description: "Description", Comment: "Comment", Publisher: "Publisher",
        Copyright: "Copyright", Isrc: "USRC17607839", Lyrics: "La la la",
        TitleSort: "Title, The", ArtistsSort: "One", AlbumSort: "Album, The", ComposersSort: "Composer, A",
        IsCompilation: true);

    // What is written is what a scan reads back - which is the whole reason
    // an edit is written into the file before the library is told of it.
    [Fact]
    public void Tags_written_into_a_file_are_the_tags_a_scan_reads_back()
    {
        var path = TaggedAudioFile.Create(_root, "song.wav", "Before");

        TrackTags.WriteToFile(path, Everything);

        var scanned = new Flower.Importer.Importer(NullLogger<Flower.Importer.Importer>.Instance).ImportFile(path)!;
        Assert.Equal(Everything, TrackTags.Of(scanned));
    }

    [Fact]
    public void Tags_applied_to_a_track_are_the_tags_it_reports()
    {
        var track = new Track { Title = "Before", Comment = "old" };

        TrackTags.ApplyTo(track, Everything);

        Assert.Equal(Everything, TrackTags.Of(track));
    }

    // A blank and an absent tag are the same tag: a file read back says "",
    // a track that never had one says null, and they are not an edit apart.
    [Fact]
    public void Blank_and_absent_are_the_same_tags()
    {
        Assert.Equal(TrackTags.Of(new Track { Title = "A", Artists = "" }), TrackTags.Of(new Track { Title = " A ", Artists = null }));
    }

    // ── A server taking edits ────────────────────────────────────────────

    [Fact]
    public void A_server_writes_an_edit_into_its_file_and_dates_it()
    {
        var path = TaggedAudioFile.Create(_root, "song.wav", "Before");
        var track = new Track { Title = "Before", Path = path, PlayCount = 5 };
        var library = new Library([track]);
        var at = DateTimeOffset.UtcNow;

        var response = TrackTags.ApplyEdits(
            library, [new TrackTagEditDto(track.Id.ToKey(), at, Everything)], NullLogger.Instance);

        Assert.Equal(1, response.Applied);
        Assert.Empty(response.NotWritten);
        Assert.Equal("Title", track.Title);
        Assert.Equal(at, track.TagsEditedAt);
        Assert.Equal(5, track.PlayCount);
        using var file = TagLib.File.Create(path);
        Assert.Equal("Title", file.Tag.Title);
    }

    [Fact]
    public void An_edit_older_than_one_the_server_already_has_is_dropped()
    {
        var path = TaggedAudioFile.Create(_root, "song.wav", "Newer");
        var newer = DateTimeOffset.UtcNow;
        var track = new Track { Title = "Newer", Path = path, TagsEditedAt = newer };
        var library = new Library([track]);

        var response = TrackTags.ApplyEdits(
            library, [new TrackTagEditDto(track.Id.ToKey(), newer.AddMinutes(-5), Everything)], NullLogger.Instance);

        Assert.Equal(0, response.Applied);
        Assert.Equal("Newer", track.Title);
        Assert.Equal(newer, track.TagsEditedAt);
    }

    // The file is what the next scan reads. A library that took an edit its
    // file never got would agree with the file again only by losing the edit.
    [Fact]
    public void An_edit_whose_file_cannot_be_written_changes_nothing_and_is_reported_back()
    {
        var track = new Track { Title = "Before", Path = Path.Combine(_root, "not-there.wav") };
        var library = new Library([track]);

        var response = TrackTags.ApplyEdits(
            library, [new TrackTagEditDto(track.Id.ToKey(), DateTimeOffset.UtcNow, Everything)], NullLogger.Instance);

        Assert.Equal(0, response.Applied);
        Assert.Equal([track.Id.ToKey()], response.NotWritten);
        Assert.Equal("Before", track.Title);
        Assert.Null(track.TagsEditedAt);
    }

    // ── Whose tags a device ends up with ─────────────────────────────────

    private static Track FromServer(string title, DateTimeOffset? editedAt, string? comment = null) => new()
    {
        Title = title,
        Artists = "Artist",
        Album = "Album",
        Comment = comment,
        Duration = TimeSpan.FromSeconds(200),
        OriginDeviceFingerprint = "server",
        OriginTrackId = "s1",
        TagsEditedAt = editedAt,
    };

    private static Track Mine(string title, string? path, DateTimeOffset? editedAt = null) => new()
    {
        Title = title,
        Artists = "Artist",
        Album = "Album",
        Duration = TimeSpan.FromSeconds(200),
        Path = path,
        OriginDeviceFingerprint = "server",
        OriginTrackId = "s1",
        TagsEditedAt = editedAt,
    };

    [Fact]
    public void A_placeholder_takes_every_tag_of_a_song_the_server_says_was_edited()
    {
        var at = DateTimeOffset.UtcNow;
        var placeholder = Mine("Old", path: null);
        var library = new Library([placeholder]);

        library.MergeSyncedTracks("server", [FromServer("New", at, comment: "a note")]);

        Assert.Equal("New", placeholder.Title);
        Assert.Equal("a note", placeholder.Comment);
        Assert.Equal(at, placeholder.TagsEditedAt);
    }

    // The edit is on its way up. Taking the catalog's older tags now would
    // undo it on screen until the push lands.
    [Fact]
    public void An_owners_placeholder_with_a_newer_edit_of_its_own_is_not_overwritten_by_the_pull()
    {
        var mine = DateTimeOffset.UtcNow;
        var placeholder = Mine("My Edit", path: null, editedAt: mine);
        var library = new Library([placeholder]);

        library.MergeSyncedTracks("server", [FromServer("Server's", mine.AddHours(-1))], ownersDevice: true);

        Assert.Equal("My Edit", placeholder.Title);
        Assert.Equal(mine, placeholder.TagsEditedAt);
    }

    // A guest's edit goes nowhere, so it does not get to stick either.
    [Fact]
    public void A_listeners_placeholder_always_reads_as_the_server_has_it()
    {
        var placeholder = Mine("My Edit", path: null, editedAt: DateTimeOffset.UtcNow);
        var library = new Library([placeholder]);

        library.MergeSyncedTracks("server", [FromServer("Server's", editedAt: null)]);

        Assert.Equal("Server's", placeholder.Title);
        Assert.Null(placeholder.TagsEditedAt);
    }

    // A file's tags are the caller's to write, so the merge hands these back
    // rather than applying them - and only where the server's edit is news.
    [Fact]
    public void A_file_is_offered_the_servers_tags_only_when_the_server_has_an_edit_this_device_lacks()
    {
        var earlier = DateTimeOffset.UtcNow.AddHours(-1);
        var later = DateTimeOffset.UtcNow;

        int Offered(Track local, Track remote, bool owner)
        {
            var work = new SyncedFileWork();
            new Library([local]).MergeSyncedTracks("server", [remote], ownersDevice: owner, fileWork: work);
            return work.NewerTags.Count;
        }

        // Never edited anywhere: a file's tags are whatever its scan read.
        Assert.Equal(0, Offered(Mine("Local", "/m/a.mp3"), FromServer("Local", null), owner: true));
        // The server's is newer than none, and newer than an older one.
        Assert.Equal(1, Offered(Mine("Local", "/m/a.mp3"), FromServer("Server", later), owner: true));
        Assert.Equal(1, Offered(Mine("Local", "/m/a.mp3", earlier), FromServer("Server", later), owner: true));
        // An owner's device with the newer edit keeps it: it is on its way up.
        Assert.Equal(0, Offered(Mine("Local", "/m/a.mp3", later), FromServer("Server", earlier), owner: true));
        // The same edit is not news.
        Assert.Equal(0, Offered(Mine("Local", "/m/a.mp3", later), FromServer("Local", later), owner: true));

        // A guest gets an owner's edit like anyone else - into a copy it
        // downloaded, and into a file of its own that is the server's song.
        Assert.Equal(1, Offered(Mine("Local", "/m/a.mp3"), FromServer("Server", later), owner: false));
        // And has no edit of a server's song to protect: it may not make one.
        Assert.Equal(1, Offered(Mine("Local", "/m/a.mp3", later), FromServer("Server", earlier), owner: false));
    }

    // ── Who may edit what ────────────────────────────────────────────────

    [Fact]
    public void A_song_of_the_servers_is_an_administrators_to_edit_and_a_song_of_ones_own_is_anybodys()
    {
        var mine = new Track { Title = "Mine", Path = "/m/mine.mp3" };
        var theirsStreamed = Mine("Streamed", path: null);
        var theirsDownloaded = Mine("Downloaded", "/downloads/a.mp3");
        theirsDownloaded.IsLocallyDownloaded = true;
        var theirsAlsoMine = Mine("Both", "/m/both.mp3");
        var nowhere = new Track { Title = "No file, no server" };

        // A guest: only what is purely its own.
        Assert.True(SyncRolePolicy.MayEditSong(mine, "server", administersPairedServer: false));
        Assert.False(SyncRolePolicy.MayEditSong(theirsStreamed, "server", administersPairedServer: false));
        Assert.False(SyncRolePolicy.MayEditSong(theirsDownloaded, "server", administersPairedServer: false));
        Assert.False(SyncRolePolicy.MayEditSong(theirsAlsoMine, "server", administersPairedServer: false));

        // An administrator: all of the server's, file here or not.
        Assert.True(SyncRolePolicy.MayEditSong(mine, "server", administersPairedServer: true));
        Assert.True(SyncRolePolicy.MayEditSong(theirsStreamed, "server", administersPairedServer: true));
        Assert.True(SyncRolePolicy.MayEditSong(theirsDownloaded, "server", administersPairedServer: true));
        Assert.True(SyncRolePolicy.MayEditSong(theirsAlsoMine, "server", administersPairedServer: true));

        // Nowhere for an edit to live.
        Assert.False(SyncRolePolicy.MayEditSong(nowhere, "server", administersPairedServer: true));

        // Not paired with that server any more: a file is just a file again.
        Assert.True(SyncRolePolicy.MayEditSong(theirsAlsoMine, "another", administersPairedServer: false));
        Assert.True(SyncRolePolicy.MayEditSong(theirsAlsoMine, null, administersPairedServer: false));
    }

    // ── A file the server moved, and artwork it changed ──────────────────

    [Fact]
    public void A_file_whose_server_copy_moved_is_handed_back_to_be_moved_and_a_first_match_is_not_a_move()
    {
        var local = Mine("Song", "/m/old/a.mp3");
        var library = new Library([local]);

        var first = new SyncedFileWork();
        var remote = FromServer("Song", null);
        remote.OriginRelativePath = "old/a.mp3";
        library.MergeSyncedTracks("server", [remote], fileWork: first);
        Assert.Empty(first.Moved);

        var second = new SyncedFileWork();
        var moved = FromServer("Song", null);
        moved.OriginRelativePath = "new/a.mp3";
        library.MergeSyncedTracks("server", [moved], fileWork: second);

        var (track, from, to) = Assert.Single(second.Moved);
        Assert.Same(local, track);
        Assert.Equal(("old/a.mp3", "new/a.mp3"), (from, to));
        Assert.Equal("new/a.mp3", local.OriginRelativePath);
    }

    // A whole new file brings its tags and its picture with it, so a song
    // whose file was replaced is in that list and neither of the others.
    [Fact]
    public void A_replaced_file_is_handed_back_once_and_settles_its_tags_and_artwork_with_it()
    {
        var at = DateTimeOffset.UtcNow;
        var local = Mine("Song", "/m/a.mp3");
        var library = new Library([local]);
        var remote = FromServer("New Title", at);
        remote.ArtEditedAt = at;
        remote.FileReplacedAt = at;

        var work = new SyncedFileWork();
        library.MergeSyncedTracks("server", [remote], fileWork: work);

        Assert.Same(local, Assert.Single(work.NewerFile).Local);
        Assert.Empty(work.NewerTags);
        Assert.Empty(work.NewerArt);

        // A copy already of that version is not fetched again.
        local.FileReplacedAt = at;
        var again = new SyncedFileWork();
        library.MergeSyncedTracks("server", [remote], fileWork: again);
        Assert.Empty(again.NewerFile);
    }

    [Fact]
    public void Newer_artwork_is_handed_back_for_a_file_and_marked_stale_for_a_song_with_none()
    {
        var at = DateTimeOffset.UtcNow;
        var file = Mine("File", "/m/a.mp3");
        var placeholder = Mine("Streamed", path: null);
        placeholder.OriginTrackId = "s2";
        var library = new Library([file, placeholder]);
        var newFile = FromServer("File", null);
        newFile.ArtEditedAt = at;
        var newStreamed = FromServer("Streamed", null);
        newStreamed.OriginTrackId = "s2";
        newStreamed.ArtEditedAt = at;

        var work = new SyncedFileWork();
        library.MergeSyncedTracks("server", [newFile, newStreamed], fileWork: work);

        Assert.Same(file, Assert.Single(work.NewerArt).Local);
        Assert.Same(placeholder, Assert.Single(work.StaleArt));
        Assert.Equal(at, placeholder.ArtEditedAt);

        // The file's date is the caller's to set, once the picture is in it.
        Assert.Null(file.ArtEditedAt);
    }

    // Announced as somebody else's change, or it would be stamped as an edit
    // made here and sent straight back.
    [Fact]
    public void Tags_that_arrived_are_not_announced_as_an_edit_made_here()
    {
        var track = Mine("Old", "/m/a.mp3");
        var library = new Library([track]);
        var at = DateTimeOffset.UtcNow.AddMinutes(-3);
        TrackChangedEventArgs? raised = null;
        library.TrackChanged += (_, e) => raised = e;

        library.ApplySyncedTags([(track, Everything, at, null)]);

        Assert.Equal(ChangeSource.Remote, raised!.Source);
        Assert.Equal(at, track.TagsEditedAt);
        Assert.Equal("Title", track.Title);
    }

    [Fact]
    public void A_tag_edit_made_here_is_dated_when_it_is_announced()
    {
        var track = Mine("Old", "/m/a.mp3");
        var library = new Library([track]);

        track.Title = "New";
        library.NotifyTrackChanged(track, TrackChange.Tags);

        Assert.NotNull(track.TagsEditedAt);

        // And only a tag edit is: new artwork is not one.
        var other = Mine("Other", "/m/b.mp3");
        library = new Library([other]);
        library.NotifyTrackChanged(other, TrackChange.Artwork);
        Assert.Null(other.TagsEditedAt);
    }
}
