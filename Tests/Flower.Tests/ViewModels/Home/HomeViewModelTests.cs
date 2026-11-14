using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Controls;
using Flower.Models;
using Flower.Services;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests.ViewModels.Home;

// How much Home shows - so many albums on Continue Playing, so many songs on
// Recently Played, so many months of Recently Added
// (AppSettings.HomeContinuePlayingLimit and friends) - and the page filter
// narrowing what it shows.
[Collection("PlatformDataDirectory")]
public class HomeViewModelTests : PinnedDataDirectory
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static List<Track> Album(string name, int addedDaysAgo, int? playedMinutesAgo = null) =>
        Enumerable.Range(1, 2).Select(i => new Track
        {
            Title = $"{name} {i}",
            Path = $"/music/{name}/{i}.flac",
            Album = name,
            Artists = $"{name} Band",
            TrackNumber = (uint)i,
            Duration = TimeSpan.FromMinutes(4),
            DateAdded = Now.AddDays(-addedDaysAgo),
            LastPlayedAt = playedMinutesAgo is { } m ? Now.AddMinutes(-m - i) : null,
        }).ToList();

    private static HomeViewModel Home(IEnumerable<List<Track>> albums)
    {
        var library = new Library(albums.SelectMany(a => a).ToList());
        return new HomeViewModel(library, new AlbumProgressTracker(library), null, clock: new FixedClock());
    }

    [Fact]
    public void Recently_added_shows_the_last_three_months_unless_told_otherwise()
    {
        using var home = Home([Album("Ten", 10), Album("Fifty", 50), Album("Hundred", 100), Album("Older", 400)]);

        home.Rebuild();
        Assert.Equal(new[] { "Ten", "Fifty" }, home.RecentlyAdded.Select(t => t.Name));

        home.RecentlyAddedMonths = 12;
        home.Rebuild();
        Assert.Equal(new[] { "Ten", "Fifty", "Hundred" }, home.RecentlyAdded.Select(t => t.Name));
    }

    [Fact]
    public void Zero_hides_the_shelf()
    {
        using var home = Home([Album("Ten", 10, playedMinutesAgo: 5)]);

        home.RecentlyPlayedLimit = 0;
        home.RecentlyAddedMonths = 0;
        home.Rebuild();

        Assert.False(home.HasRecentlyPlayed);
        Assert.False(home.HasRecentlyAdded);
        Assert.True(home.ShowsNothingYet);
    }

    [Fact]
    public void The_filter_narrows_every_shelf_and_says_when_nothing_matches()
    {
        using var home = Home([Album("Blue", 10, playedMinutesAgo: 5), Album("Red", 20, playedMinutesAgo: 50)]);
        home.Rebuild();
        Assert.Equal(4, home.RecentlyPlayed.Count);

        home.Filter = "red band";
        home.Rebuild();
        Assert.Equal(new[] { "Red 1", "Red 2" }, home.RecentlyPlayed.Select(r => r.Track.Title));
        Assert.Equal(new[] { "Red" }, home.RecentlyAdded.Select(t => t.Name));

        home.Filter = "green";
        home.Rebuild();
        Assert.True(home.ShowsNoMatches);
        Assert.False(home.ShowsNothingYet);

        home.Filter = "  ";
        home.Rebuild();
        Assert.Equal(4, home.RecentlyPlayed.Count);
    }

    // The shelves come in the order Settings put them in, on either head.
    [Fact]
    public void The_shelves_come_in_the_order_chosen()
    {
        using var home = Home(Enumerable.Range(0, 3).Select(i => Album($"A{i}", addedDaysAgo: i + 1, playedMinutesAgo: i * 10)));
        home.Rebuild();

        home.ShelfOrder = [HomeShelf.RecentlyAdded, HomeShelf.ContinuePlaying, HomeShelf.RecentlyPlayed];
        var titles = HomeShelfRows.Build(home, columns: 4).OfType<HomeShelfHeaderRow>().Select(h => h.Title);

        Assert.Equal(new[] { "Recently added", "Recently played" }, titles);
    }
}
