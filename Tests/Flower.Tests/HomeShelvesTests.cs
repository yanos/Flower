using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;
using Flower.Services;

using Xunit;

namespace Flower.Tests;

// What each of Home's shelves holds, and in what order.
public class HomeShelvesTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private static List<Track> Album(string name, int songs, int addedDaysAgo = 30, int? playedMinutesAgo = null) =>
        Enumerable.Range(1, songs).Select(i => new Track
        {
            Title = $"{name} {i}",
            Path = $"/music/{name}/{i}.flac",
            Album = name,
            Artists = "Artist",
            TrackNumber = (uint)i,
            Duration = TimeSpan.FromMinutes(4),
            DateAdded = Now.AddDays(-addedDaysAgo),
            LastPlayedAt = playedMinutesAgo is { } m ? Now.AddMinutes(-m - i) : null,
        }).ToList();

    private static AlbumProgressEntry Entry(Track at, TimeSpan position) =>
        new(AlbumProgressTracker.AlbumIdOf(at)!, at.Id, position, Now);

    [Fact]
    public void Continue_playing_says_how_far_through_the_album_the_tape_is()
    {
        var album = Album("Blue", 4);
        var library = LibrarySnapshot.Build(album);

        var item = Assert.Single(HomeShelves.ContinuePlaying([Entry(album[2], TimeSpan.FromMinutes(1))], library, 10));

        Assert.Equal(2, item.TrackIndex);
        Assert.Equal(TimeSpan.FromMinutes(9), item.Elapsed);
        Assert.Equal(TimeSpan.FromMinutes(16), item.Total);
        Assert.Equal("Blue", item.Tile.Name);
        Assert.Equal(album.Select(t => t.Id), item.Tracks.Select(t => t.Id));
    }

    [Fact]
    public void Continue_playing_skips_an_album_that_is_no_longer_there()
    {
        var blue = Album("Blue", 3);
        var red = Album("Red", 3);
        var library = LibrarySnapshot.Build(red);

        var items = HomeShelves.ContinuePlaying([Entry(blue[1], TimeSpan.Zero), Entry(red[1], TimeSpan.Zero)], library, 10);

        Assert.Equal("Red", Assert.Single(items).Tile.Name);
    }

    [Fact]
    public void Continue_playing_is_limited_and_keeps_the_shelf_order()
    {
        var albums = Enumerable.Range(0, 5).Select(i => Album($"A{i}", 2)).ToList();
        var library = LibrarySnapshot.Build(albums.SelectMany(a => a).ToList());

        var items = HomeShelves.ContinuePlaying(albums.Select(a => Entry(a[1], TimeSpan.Zero)), library, 3);

        Assert.Equal(new[] { "A0", "A1", "A2" }, items.Select(i => i.Tile.Name));
    }

    [Fact]
    public void Recently_played_is_most_recent_first_and_leaves_out_the_unplayed()
    {
        var older = Album("Older", 2, playedMinutesAgo: 600);
        var newer = Album("Newer", 2, playedMinutesAgo: 5);
        var never = Album("Never", 2);
        var library = LibrarySnapshot.Build(older.Concat(newer).Concat(never).ToList());

        var tiles = HomeShelves.RecentlyPlayed(library, new HashSet<string>(), 10);

        Assert.Equal(new[] { "Newer", "Older" }, tiles.Select(t => t.Name));
    }

    // Continue Playing already says it was played lately, and more.
    [Fact]
    public void Recently_played_leaves_out_what_continue_playing_shows()
    {
        var blue = Album("Blue", 2, playedMinutesAgo: 5);
        var red = Album("Red", 2, playedMinutesAgo: 10);
        var library = LibrarySnapshot.Build(blue.Concat(red).ToList());

        var tiles = HomeShelves.RecentlyPlayed(library, new HashSet<string> { AlbumProgressTracker.AlbumIdOf(blue[0])! }, 10);

        Assert.Equal("Red", Assert.Single(tiles).Name);
    }

    [Fact]
    public void Recently_added_is_newest_first_limited_and_only_real_albums()
    {
        var old = Album("Old", 1, addedDaysAgo: 100);
        var mid = Album("Mid", 1, addedDaysAgo: 10);
        var fresh = Album("Fresh", 1, addedDaysAgo: 1);
        var loose = new Track { Title = "Loose", Path = "/loose.mp3", DateAdded = Now };
        var library = LibrarySnapshot.Build(old.Concat(mid).Concat(fresh).Append(loose).ToList());

        var tiles = HomeShelves.RecentlyAdded(library, 2);

        Assert.Equal(new[] { "Fresh", "Mid" }, tiles.Select(t => t.Name));
    }
}
