using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Models;
using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests;

// Continue Playing's rules, on the tracker alone: what starts an album, where
// its tape is left, and what finishes it. PlaylistControlViewModel feeding it
// from real playback events is AlbumProgressPlaybackTests.
[Collection("PlatformDataDirectory")]
public class AlbumProgressTrackerTests : PinnedDataDirectory
{
    private static List<Track> Album(string name, int songs, string artist = "Artist") =>
        Enumerable.Range(1, songs).Select(i => new Track
        {
            Title = $"{name} {i}",
            Path = $"/music/{name}/{i}.flac",
            Album = name,
            Artists = artist,
            TrackNumber = (uint)i,
            Duration = TimeSpan.FromMinutes(4),
        }).ToList();

    private static AlbumProgressTracker Tracker(IEnumerable<Track> tracks, AlbumProgressStore? store = null) =>
        new(new Library(tracks.ToList()), store);

    [Fact]
    public void One_song_from_an_album_is_not_listening_to_it()
    {
        var album = Album("Blue", 5);
        var tracker = Tracker(album);

        tracker.TrackStarted(album[0], shuffling: false);

        Assert.Empty(tracker.Entries);
    }

    [Fact]
    public void Two_songs_in_a_row_from_one_album_put_it_on_the_shelf_at_the_second()
    {
        var album = Album("Blue", 5);
        var tracker = Tracker(album);

        tracker.TrackStarted(album[0], shuffling: false);
        tracker.TrackStarted(album[1], shuffling: false);

        var entry = Assert.Single(tracker.Entries);
        Assert.Equal(AlbumProgressTracker.AlbumIdOf(album[0]), entry.AlbumId);
        Assert.Equal(album[1].Id, entry.TrackId);
        Assert.Equal(TimeSpan.Zero, entry.Position);
    }

    [Fact]
    public void Two_songs_from_different_albums_start_nothing()
    {
        var blue = Album("Blue", 3);
        var red = Album("Red", 3);
        var tracker = Tracker(blue.Concat(red));

        tracker.TrackStarted(blue[0], shuffling: false);
        tracker.TrackStarted(red[0], shuffling: false);
        tracker.TrackStarted(blue[1], shuffling: false);

        Assert.Empty(tracker.Entries);
    }

    [Fact]
    public void One_song_repeated_is_not_two_songs()
    {
        var album = Album("Blue", 3);
        var tracker = Tracker(album);

        tracker.TrackStarted(album[0], shuffling: false);
        tracker.TrackStarted(album[0], shuffling: false);

        Assert.Empty(tracker.Entries);
    }

    [Fact]
    public void Neighbours_under_shuffle_are_a_coincidence()
    {
        var album = Album("Blue", 3);
        var tracker = Tracker(album);

        tracker.TrackStarted(album[0], shuffling: true);
        tracker.TrackStarted(album[1], shuffling: true);

        Assert.Empty(tracker.Entries);
    }

    [Fact]
    public void Songs_with_no_album_are_never_an_album()
    {
        var loose = new[] { new Track { Title = "a", Path = "/a" }, new Track { Title = "b", Path = "/b" } };
        var tracker = Tracker(loose);

        tracker.TrackStarted(loose[0], shuffling: false);
        tracker.TrackStarted(loose[1], shuffling: false);

        Assert.Empty(tracker.Entries);
    }

    [Fact]
    public void Putting_playback_down_writes_down_where_it_got_to()
    {
        var album = Album("Blue", 5);
        var tracker = Tracker(album);
        tracker.TrackStarted(album[0], shuffling: false);
        tracker.TrackStarted(album[1], shuffling: false);

        tracker.UpdatePosition(95_000);
        tracker.Flush();

        Assert.Equal(TimeSpan.FromSeconds(95), Assert.Single(tracker.Entries).Position);
    }

    // The cassette: put another album on half-way through, and the first one
    // stays where it was stopped.
    [Fact]
    public void Another_album_put_on_leaves_the_first_where_it_was()
    {
        var blue = Album("Blue", 5);
        var red = Album("Red", 5);
        var tracker = Tracker(blue.Concat(red));
        tracker.TrackStarted(blue[0], shuffling: false);
        tracker.TrackStarted(blue[1], shuffling: false);
        tracker.UpdatePosition(61_000);

        tracker.TrackStarted(red[2], shuffling: false);
        tracker.UpdatePosition(10_000);
        tracker.TrackStarted(red[3], shuffling: false);

        var entries = tracker.Entries;
        Assert.Equal(2, entries.Count);
        Assert.Equal(red[3].Id, entries[0].TrackId);
        Assert.Equal(TimeSpan.Zero, entries[0].Position);
        Assert.Equal(blue[1].Id, entries[1].TrackId);
        Assert.Equal(TimeSpan.FromSeconds(61), entries[1].Position);
    }

    // Once on the shelf, any of its songs starting moves the tape, two in a
    // row or not - picking it back up is one song.
    [Fact]
    public void An_album_on_the_shelf_follows_whichever_of_its_songs_starts()
    {
        var blue = Album("Blue", 5);
        var red = Album("Red", 5);
        var tracker = Tracker(blue.Concat(red));
        tracker.TrackStarted(blue[0], shuffling: false);
        tracker.TrackStarted(blue[1], shuffling: false);
        tracker.TrackStarted(red[0], shuffling: false);

        tracker.TrackStarted(blue[3], shuffling: false);

        Assert.Equal(blue[3].Id, Assert.Single(tracker.Entries).TrackId);
    }

    // Continue Playing starts the very song the tape is at and seeks into it:
    // until playback reports a new position, the old one is still the place.
    [Fact]
    public void Starting_the_song_the_tape_is_at_keeps_the_place()
    {
        var blue = Album("Blue", 5);
        var red = Album("Red", 5);
        var tracker = Tracker(blue.Concat(red));
        tracker.TrackStarted(blue[0], shuffling: false);
        tracker.TrackStarted(blue[1], shuffling: false);
        tracker.UpdatePosition(80_000);
        tracker.TrackStarted(red[0], shuffling: false);

        tracker.TrackStarted(blue[1], shuffling: false);
        tracker.Flush();

        Assert.Equal(TimeSpan.FromSeconds(80), tracker.Entries.Single(e => e.TrackId == blue[1].Id).Position);
    }

    [Fact]
    public void A_song_played_to_its_end_moves_the_tape_to_the_top_of_the_next()
    {
        var album = Album("Blue", 5);
        var tracker = Tracker(album);
        tracker.TrackStarted(album[0], shuffling: false);
        tracker.TrackStarted(album[1], shuffling: false);
        tracker.UpdatePosition(239_000);

        tracker.TrackFinished(album[1]);

        var entry = Assert.Single(tracker.Entries);
        Assert.Equal(album[2].Id, entry.TrackId);
        Assert.Equal(TimeSpan.Zero, entry.Position);
    }

    [Fact]
    public void The_last_song_played_to_its_end_finishes_the_album()
    {
        var album = Album("Blue", 3);
        var tracker = Tracker(album);
        tracker.TrackStarted(album[1], shuffling: false);
        tracker.TrackStarted(album[2], shuffling: false);

        tracker.TrackFinished(album[2]);

        Assert.Empty(tracker.Entries);
    }

    // The order is the album's own - disc, then track - not the order the
    // library happens to hold its songs in.
    [Fact]
    public void Last_means_last_on_the_album_not_last_in_the_library()
    {
        var album = Album("Blue", 3);
        album[0].DiscNumber = 2;
        album[1].DiscNumber = 1;
        album[2].DiscNumber = 1;
        var tracker = Tracker(album);
        tracker.TrackStarted(album[1], shuffling: false);
        tracker.TrackStarted(album[2], shuffling: false);

        tracker.TrackFinished(album[2]);

        Assert.Equal(album[0].Id, Assert.Single(tracker.Entries).TrackId);
    }

    [Fact]
    public void An_album_can_be_taken_off_by_hand()
    {
        var album = Album("Blue", 3);
        var tracker = Tracker(album);
        tracker.TrackStarted(album[0], shuffling: false);
        tracker.TrackStarted(album[1], shuffling: false);

        tracker.Forget(AlbumProgressTracker.AlbumIdOf(album[0])!);

        Assert.Empty(tracker.Entries);
    }

    [Fact]
    public void The_shelf_survives_a_relaunch()
    {
        var album = Album("Blue", 5);
        var store = new AlbumProgressStore(NullLogger<AlbumProgressStore>.Instance);
        var tracker = Tracker(album, store);
        tracker.TrackStarted(album[0], shuffling: false);
        tracker.TrackStarted(album[1], shuffling: false);
        tracker.UpdatePosition(42_000);

        tracker.Flush(synchronously: true);

        var reloaded = Tracker(album, store);
        var entry = Assert.Single(reloaded.Entries);
        Assert.Equal(album[1].Id, entry.TrackId);
        Assert.Equal(TimeSpan.FromSeconds(42), entry.Position);
    }

    [Fact]
    public void The_shelf_remembers_only_so_many_albums()
    {
        var albums = Enumerable.Range(0, AlbumProgressTracker.MaxEntries + 5).Select(i => Album($"A{i}", 2)).ToList();
        var tracker = Tracker(albums.SelectMany(a => a));

        foreach (var album in albums)
        {
            tracker.TrackStarted(album[0], shuffling: false);
            tracker.TrackStarted(album[1], shuffling: false);
        }

        Assert.Equal(AlbumProgressTracker.MaxEntries, tracker.Entries.Count);
        Assert.Equal(AlbumProgressTracker.AlbumIdOf(albums[^1][0]), tracker.Entries[0].AlbumId);
    }
}
