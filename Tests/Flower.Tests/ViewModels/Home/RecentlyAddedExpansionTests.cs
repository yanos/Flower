using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;
using Flower.Services;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests.ViewModels.Home;

// A Recently Added cover clicked on the desktop opens its album under its row,
// as the Albums grid does, instead of going to the Albums page
// (HomeViewModel.ToggleAlbum, HomeShelfRows.ApplyExpansion).
[Collection("PlatformDataDirectory")]
public class RecentlyAddedExpansionTests : PinnedDataDirectory
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static List<Track> Album(string name, int addedDaysAgo) =>
        Enumerable.Range(1, 2).Select(i => new Track
        {
            Title = $"{name} {i}",
            Path = $"/music/{name}/{i}.flac",
            Album = name,
            Artists = $"{name} Band",
            TrackNumber = (uint)i,
            Duration = TimeSpan.FromMinutes(4),
            DateAdded = Now.AddDays(-addedDaysAgo),
        }).ToList();

    private static HomeViewModel Home(params List<Track>[] albums)
    {
        var library = new Library(albums.SelectMany(a => a).ToList());
        var home = new HomeViewModel(library, new AlbumProgressTracker(library), null, clock: new FixedClock());
        home.Rebuild();
        return home;
    }

    private static List<HomeAlbumRow> AlbumRows(List<HomeShelfRow> rows) => rows.OfType<HomeAlbumRow>().ToList();

    [Fact]
    public void A_click_opens_the_album_under_its_row_and_another_click_closes_it()
    {
        using var home = Home(Album("Blue", 1), Album("Red", 2), Album("Green", 3));
        var rows = HomeShelfRows.Build(home, columns: 2);
        var red = home.RecentlyAdded.Single(t => t.Name == "Red");

        var opened = false;
        home.AlbumOpened += (_, _) => opened = true;
        home.ToggleAlbumCommand.Execute(red);
        HomeShelfRows.ApplyExpansion(rows, home);

        var open = Assert.Single(AlbumRows(rows), r => r.Album.IsExpanded);
        Assert.Contains(red, open.Tiles);
        Assert.Same(red, open.Album.ExpandedTile);
        Assert.Equal(new[] { "Red 1", "Red 2" }, open.Album.ExpandedTracks.Select(t => t.Title));
        Assert.False(opened);

        home.ToggleAlbumCommand.Execute(red);
        HomeShelfRows.ApplyExpansion(rows, home);

        Assert.DoesNotContain(AlbumRows(rows), r => r.Album.IsExpanded);
        Assert.Null(home.ExpandedAlbumKey);
    }

    [Fact]
    public void Opening_another_album_closes_the_first()
    {
        using var home = Home(Album("Blue", 1), Album("Red", 2), Album("Green", 3));
        var rows = HomeShelfRows.Build(home, columns: 2);

        home.ToggleAlbum(home.RecentlyAdded.Single(t => t.Name == "Blue"));
        HomeShelfRows.ApplyExpansion(rows, home);
        home.ToggleAlbum(home.RecentlyAdded.Single(t => t.Name == "Green"));
        HomeShelfRows.ApplyExpansion(rows, home);

        var open = Assert.Single(AlbumRows(rows), r => r.Album.IsExpanded);
        Assert.Equal("Green", open.Album.ExpandedTile?.Name);
        Assert.Equal(new[] { "Green" }, home.RecentlyAdded.Where(t => t.IsExpanded).Select(t => t.Name));
    }

    // The shelf is rebuilt whenever a song plays; the album stays open
    // through that, and closes once the filter takes it off the shelf.
    [Fact]
    public void The_album_stays_open_across_a_rebuild_until_it_leaves_the_shelf()
    {
        using var home = Home(Album("Blue", 1), Album("Red", 2));
        home.ToggleAlbum(home.RecentlyAdded.Single(t => t.Name == "Red"));

        home.Rebuild();
        var open = Assert.Single(AlbumRows(HomeShelfRows.Build(home, columns: 2)), r => r.Album.IsExpanded);
        Assert.Equal("Red", open.Album.ExpandedTile?.Name);

        home.Filter = "blue";
        home.Rebuild();
        Assert.Null(home.ExpandedAlbumKey);
        Assert.DoesNotContain(AlbumRows(HomeShelfRows.Build(home, columns: 2)), r => r.Album.IsExpanded);
    }
}
