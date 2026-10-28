using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;

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
public class HomeShelfSettingsTests : PinnedDataDirectory
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

    [AvaloniaFact]
    public void Recently_played_shows_eight_songs_unless_told_otherwise()
    {
        using var home = Home(Enumerable.Range(0, 10).Select(i => Album($"P{i}", addedDaysAgo: 400, playedMinutesAgo: i * 10)));

        home.Rebuild();
        Assert.Equal(8, home.RecentlyPlayed.Count);

        home.RecentlyPlayedLimit = 3;
        home.Rebuild();
        Assert.Equal(new[] { "P0 1", "P0 2", "P1 1" }, home.RecentlyPlayed.Select(r => r.Track.Title));
    }

    [AvaloniaFact]
    public void Recently_added_shows_the_last_three_months_unless_told_otherwise()
    {
        using var home = Home([Album("Ten", 10), Album("Fifty", 50), Album("Hundred", 100), Album("Older", 400)]);

        home.Rebuild();
        Assert.Equal(new[] { "Ten", "Fifty" }, home.RecentlyAdded.Select(t => t.Name));

        home.RecentlyAddedMonths = 12;
        home.Rebuild();
        Assert.Equal(new[] { "Ten", "Fifty", "Hundred" }, home.RecentlyAdded.Select(t => t.Name));
    }

    [AvaloniaFact]
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

    [AvaloniaFact]
    public void The_settings_are_held_to_their_ceilings()
    {
        using var home = Home([]);

        home.ContinuePlayingLimit = 1000;
        home.RecentlyAddedMonths = -4;

        Assert.Equal(HomeViewModel.MaxAlbumsPerShelf, home.ContinuePlayingLimit);
        Assert.Equal(0, home.RecentlyAddedMonths);
    }

    [AvaloniaFact]
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

    // Set on either head through MainViewModel, applied to the shelves and
    // saved - clamped first, so what is saved is what Home shows.
    [AvaloniaFact]
    public void A_setting_changed_is_applied_and_saved()
    {
        using var parts = MainViewModelHarness.BuildParts(new Library([]), new MainPlaylist([]));

        parts.Main.HomeRecentlyPlayedLimit = 3;
        parts.Main.HomeContinuePlayingLimit = 999;

        Assert.Equal(3, parts.Main.Home.RecentlyPlayedLimit);
        Assert.Equal(3, parts.AppSettings.HomeRecentlyPlayedLimit);
        Assert.Equal(HomeViewModel.MaxAlbumsPerShelf, parts.AppSettings.HomeContinuePlayingLimit);
    }

    [AvaloniaFact]
    public void Home_starts_from_what_was_saved()
    {
        var settings = new Flower.Persistence.AppSettings
        {
            HomeContinuePlayingLimit = 2,
            HomeRecentlyPlayedLimit = 4,
            HomeRecentlyAddedMonths = 9,
        };
        using var parts = MainViewModelHarness.BuildParts(new Library([]), new MainPlaylist([]), settings);

        Assert.Equal(2, parts.Main.Home.ContinuePlayingLimit);
        Assert.Equal(4, parts.Main.Home.RecentlyPlayedLimit);
        Assert.Equal(9, parts.Main.Home.RecentlyAddedMonths);
    }

    // The shelves come in the order Settings put them in, on either head.
    [AvaloniaFact]
    public void The_shelves_come_in_the_order_chosen()
    {
        using var home = Home(Enumerable.Range(0, 3).Select(i => Album($"A{i}", addedDaysAgo: i + 1, playedMinutesAgo: i * 10)));
        home.Rebuild();

        home.ShelfOrder = [HomeShelf.RecentlyAdded, HomeShelf.ContinuePlaying, HomeShelf.RecentlyPlayed];
        var titles = HomeShelfRows.Build(home, columns: 4).OfType<HomeShelfHeaderRow>().Select(h => h.Title);

        Assert.Equal(new[] { "Recently Added", "Recently Played" }, titles);
    }

    // A shelf dragged to a new place is there at once, and still there at the
    // next launch.
    [AvaloniaFact]
    public void A_shelf_moved_in_settings_moves_on_home_and_is_saved()
    {
        var settings = new Flower.Persistence.AppSettings();
        using var parts = MainViewModelHarness.BuildParts(new Library([]), new MainPlaylist([]), settings);
        var main = parts.Main;

        main.MoveHomeShelf(HomeShelf.RecentlyAdded, 0);

        var moved = new[] { HomeShelf.RecentlyAdded, HomeShelf.ContinuePlaying, HomeShelf.RecentlyPlayed };
        Assert.Equal(moved, main.Home.ShelfOrder);
        Assert.Equal(moved, main.HomeShelfSettings.Select(r => r.Shelf));
        Assert.Equal(moved, HomeShelfOrder.Parse(settings.HomeShelfOrder));
    }

    [AvaloniaFact]
    public void Home_starts_in_the_saved_order()
    {
        var settings = new Flower.Persistence.AppSettings { HomeShelfOrder = ["RecentlyPlayed", "RecentlyAdded", "ContinuePlaying"] };
        using var parts = MainViewModelHarness.BuildParts(new Library([]), new MainPlaylist([]), settings);

        Assert.Equal(new[] { HomeShelf.RecentlyPlayed, HomeShelf.RecentlyAdded, HomeShelf.ContinuePlaying }, parts.Main.Home.ShelfOrder);
    }

    // The desktop's page header is over Home now, and its filter box is
    // Home's filter - but only while Home is the page.
    [AvaloniaFact]
    public void The_desktop_page_filter_is_homes_while_home_is_showing()
    {
        using var parts = MainViewModelHarness.BuildParts(new Library([]), new MainPlaylist([]));
        var main = parts.Main;
        main.SelectedSidebarItem = main.SidebarItems.First(i => i.Kind == SidebarItemKind.Home);

        Assert.True(main.ShowsPageHeader);
        main.FilterText = "blue";
        Assert.Equal("blue", main.Home.Filter);

        main.SelectedSidebarItem = main.SidebarItems.First(i => i.Kind == SidebarItemKind.Songs);
        main.FilterText = "red";
        Assert.Equal("blue", main.Home.Filter);
    }

    // One list of rows for a virtualizing panel: each shelf's heading, then
    // its covers so many to a row, a short last row keeping the full count.
    [AvaloniaFact]
    public void The_shelves_become_headed_rows_of_songs_and_covers()
    {
        using var home = Home(Enumerable.Range(0, 5).Select(i => Album($"A{i}", addedDaysAgo: i + 1, playedMinutesAgo: i * 10)));
        home.RecentlyPlayedLimit = 2;
        home.Rebuild();

        var rows = HomeShelfRows.Build(home, columns: 2);

        Assert.IsType<HomeShelfHeaderRow>(rows[0]);
        Assert.Single(Assert.IsType<HomeSongRow>(rows[1]).Songs);
        Assert.Single(Assert.IsType<HomeSongRow>(rows[2]).Songs);
        Assert.IsType<HomeShelfHeaderRow>(rows[3]);
        var last = Assert.IsType<HomeAlbumRow>(rows[^1]);
        Assert.Single(last.Tiles);
        Assert.Equal(7, rows.Count); // 2 headings, 2 + 3 rows
    }

    // What made resizing on Home slow: every cover on every shelf was built
    // and laid out, and Recently Added on a library scanned within its window
    // is the whole library. Only the rows on screen are built now, as in the
    // Albums grid.
    [AvaloniaFact]
    public void Only_the_covers_on_screen_are_built()
    {
        using var home = Home(Enumerable.Range(0, 300).Select(i => Album($"A{i}", addedDaysAgo: 1)));
        home.Rebuild();
        Assert.Equal(300, home.RecentlyAdded.Count);

        // Theme first: content added before it never gets its templates.
        var window = new Avalonia.Controls.Window { Width = 1200, Height = 800 };
        window.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        window.Content = new Flower.Views.HomeView { DataContext = home };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var built = window.GetVisualDescendants().OfType<AlbumCard>().Count();
        Assert.InRange(built, 1, 60);
        window.Close();
    }
}
