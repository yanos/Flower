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
