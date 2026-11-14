using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;
using Flower.Services;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests.ViewModels.Home;

// Row reuse and layout data do not need the app shell or a dispatcher.
public class RecentlyPlayedModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static List<Track> Album(string name, int playedMinutesAgo) =>
        Enumerable.Range(1, 2).Select(i => new Track
        {
            Title = $"{name} {i}",
            Path = $"/music/{name}/{i}.flac",
            Album = name,
            Artists = "Artist",
            TrackNumber = (uint)i,
            Duration = TimeSpan.FromMinutes(4),
            LastPlayedAt = Now.AddMinutes(-playedMinutesAgo - i),
        }).ToList();

    private static HomeViewModel Home(params List<Track>[] albums)
    {
        var library = new Library(albums.SelectMany(a => a).ToList());
        return new HomeViewModel(library, new AlbumProgressTracker(library), null, clock: new FixedClock());
    }

    // A song still on the shelf keeps its row across a rebuild, and with it
    // the cover the row has already loaded.
    [Fact]
    public void A_song_still_on_the_shelf_keeps_its_row()
    {
        using var home = Home(Album("Blue", 5), Album("Red", 50));
        home.Rebuild();
        var red = home.RecentlyPlayed.Single(r => r.Track.Title == "Red 1");

        home.RecentlyPlayed[0].Track.LastPlayedAt = Now.AddMinutes(-500);
        home.Rebuild();

        Assert.Same(red, home.RecentlyPlayed.Single(r => r.Track.Title == "Red 1"));
    }

    [Fact]
    public void The_songs_go_so_many_to_a_row_under_a_heading_that_can_lead_to_history()
    {
        using var home = Home(Album("Blue", 5), Album("Red", 50));
        home.RecentlyAddedMonths = 0;
        home.Rebuild();

        var desktop = HomeShelfRows.Build(home, columns: 5, songColumns: 2, historyLink: true);
        var header = Assert.IsType<HomeShelfHeaderRow>(desktop[0]);
        Assert.True(header.ShowsHistoryLink);
        Assert.Equal(new[] { 2, 2 }, desktop.Skip(1).Cast<HomeSongRow>().Select(r => r.Songs.Count));

        var phone = HomeShelfRows.Build(home, columns: 2);
        Assert.False(Assert.IsType<HomeShelfHeaderRow>(phone[0]).ShowsHistoryLink);
        Assert.Equal(4, phone.OfType<HomeSongRow>().Count());
    }
}
