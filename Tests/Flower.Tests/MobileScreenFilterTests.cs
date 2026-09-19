using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Flower.Controls;
using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels.Mobile;
using Flower.Views.Mobile;
using Material.Icons;
using Material.Icons.Avalonia;

using Xunit;

namespace Flower.Tests;

// The filter box a phone screen reveals when pulled down past its top
// (MobileMainViewModel.ScreenFilter, drawn by MobileMainView as an oval). It narrows
// whichever list the screen is - album grid, artist or playlist picker, or a
// track list - by anything a song in it would be found by, belongs to that one
// screen, and comes back with it on Back.
[Collection("PlatformDataDirectory")]
public class MobileScreenFilterTests : PinnedDataDirectory
{
    private static Track Song(string title, string album, string artist) => new()
    {
        Title = title,
        Album = album,
        Artists = artist,
        Path = $"/music/{album}/{title}.flac",
        DateAdded = DateTimeOffset.UtcNow,
    };

    private static MainViewModelHarness.MobileParts Build()
    {
        var tracks = new List<Track>
        {
            Song("Heroes", "Heroes", "David Bowie"),
            Song("Sons of the Silent Age", "Heroes", "David Bowie"),
            Song("Karma Police", "OK Computer", "Radiohead"),
            Song("Airbag", "OK Computer", "Radiohead"),
            Song("Jóga", "Homogenic", "Björk"),
        };
        var parts = MainViewModelHarness.BuildMobile(new Library(tracks), new MainPlaylist(tracks));
        Dispatcher.UIThread.RunJobs();
        return parts;
    }

    private static void WaitUntil(Func<bool> condition, string what, int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition())
                return;
            if (Environment.TickCount64 >= deadline)
                break;
            Thread.Sleep(10);
        }
        Assert.Fail($"{what} did not happen within {timeoutMs}ms");
    }

    private static List<string> GridAlbums(IEnumerable<AlbumGridRow> rows) =>
        rows.SelectMany(r => r.Tiles).Select(t => t.Name).ToList();

    [AvaloniaFact]
    public void A_song_title_finds_the_album_it_is_on()
    {
        using var scope = Build();
        var mobile = scope.Mobile;
        mobile.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        Assert.Equal(3, GridAlbums(mobile.AlbumGridRows).Count);

        mobile.ScreenFilter = "karma";

        WaitUntil(() => GridAlbums(mobile.AlbumGridRows).Count == 1, "the grid narrowing");
        Assert.Equal(["OK Computer"], GridAlbums(mobile.AlbumGridRows));
    }

    [AvaloniaFact]
    public void Clearing_the_filter_brings_every_album_back()
    {
        using var scope = Build();
        var mobile = scope.Mobile;
        mobile.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        mobile.ScreenFilter = "karma";
        WaitUntil(() => GridAlbums(mobile.AlbumGridRows).Count == 1, "the grid narrowing");

        mobile.ScreenFilter = "";

        WaitUntil(() => GridAlbums(mobile.AlbumGridRows).Count == 3, "the grid widening again");
    }

    // Accents are folded the way the Search tab folds them - see SearchText.
    [AvaloniaFact]
    public void An_artist_is_found_by_name_or_by_one_of_their_songs()
    {
        using var scope = Build();
        var mobile = scope.Mobile;
        mobile.SelectTabCommand.Execute(nameof(MobileTab.Artists));
        WaitUntil(() => mobile.ArtistPickerItems.Count == 3, "the artist list");

        mobile.ScreenFilter = "bjork";
        WaitUntil(() => mobile.ArtistPickerItems.Count == 1, "the artists narrowing by name");
        Assert.Equal(["Björk"], mobile.ArtistPickerItems);

        mobile.ScreenFilter = "airbag";
        WaitUntil(() => mobile.ArtistPickerItems.SequenceEqual(["Radiohead"]), "the artists narrowing by song");
    }

    [AvaloniaFact]
    public void The_songs_list_is_narrowed_to_matching_songs()
    {
        using var scope = Build();
        var mobile = scope.Mobile;
        mobile.SelectTabCommand.Execute(nameof(MobileTab.Songs));
        WaitUntil(() => mobile.Main.Rows.Count == 5, "the songs list");

        mobile.ScreenFilter = "heroes";

        // By title and by album: both of the Heroes album's songs.
        WaitUntil(() => mobile.Main.Rows.Count == 2, "the songs narrowing");
        Assert.All(mobile.Main.Rows, r => Assert.Equal("Heroes", r.Track.Album));
    }

    // A drill-in lands on a screen of its own, with nothing filtered out of
    // it - the word typed over the grid was about the grid.
    [AvaloniaFact]
    public void A_drill_in_starts_unfiltered()
    {
        using var scope = Build();
        var mobile = scope.Mobile;
        mobile.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        mobile.ScreenFilter = "karma";
        WaitUntil(() => GridAlbums(mobile.AlbumGridRows).Count == 1, "the grid narrowing");

        mobile.SelectAlbumOrArtistCommand.Execute("OK Computer");
        MainViewModelHarness.WaitForTheDrillIn(mobile, "OK Computer");

        Assert.Null(mobile.ScreenFilter);
        Assert.Null(mobile.Main.FilterText);
        WaitUntil(() => mobile.AlbumDetailRows.Count == 2, "both of the album's songs");
    }

    [AvaloniaFact]
    public void Back_puts_the_filter_back()
    {
        using var scope = Build();
        var mobile = scope.Mobile;
        mobile.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        mobile.ScreenFilter = "karma";
        WaitUntil(() => GridAlbums(mobile.AlbumGridRows).Count == 1, "the grid narrowing");
        mobile.SelectAlbumOrArtistCommand.Execute("OK Computer");
        MainViewModelHarness.WaitForTheDrillIn(mobile, "OK Computer");

        mobile.BackCommand.Execute(null);

        WaitUntil(() => mobile.IsShowingAlbumGrid, "landing back on the grid");
        Assert.Equal("karma", mobile.ScreenFilter);
        Assert.Equal(["OK Computer"], GridAlbums(mobile.AlbumGridRows));
    }

    // A filtered track list is where Main.FilterText is set at all; leaving
    // it for another tab must not leave the rows the next list builds cut by
    // it.
    [AvaloniaFact]
    public void Leaving_a_filtered_track_list_clears_the_rows_filter()
    {
        using var scope = Build();
        var mobile = scope.Mobile;
        mobile.SelectTabCommand.Execute(nameof(MobileTab.Songs));
        mobile.ScreenFilter = "heroes";
        Assert.Equal("heroes", mobile.Main.FilterText);

        mobile.SelectTabCommand.Execute(nameof(MobileTab.Albums));

        Assert.Null(mobile.Main.FilterText);
        Assert.Null(mobile.ScreenFilter);
    }

    [AvaloniaFact]
    public void Nothing_matching_says_so()
    {
        using var scope = Build();
        var mobile = scope.Mobile;
        mobile.SelectTabCommand.Execute(nameof(MobileTab.Albums));

        mobile.ScreenFilter = "zzz";

        WaitUntil(() => mobile.IsContentEmpty, "the empty state");
        Assert.Equal("No Matches", mobile.EmptyStateTitle);
    }

    // The x is the one way out, and it takes the filtering with it at once.
    [AvaloniaFact]
    public void Closing_the_filter_shows_everything_again()
    {
        using var scope = Build();
        var mobile = scope.Mobile;
        mobile.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        Assert.True(mobile.OpenScreenFilter());
        Assert.True(mobile.IsScreenFilterOpen);
        mobile.ScreenFilter = "karma";
        WaitUntil(() => GridAlbums(mobile.AlbumGridRows).Count == 1, "the grid narrowing");

        mobile.CloseScreenFilterCommand.Execute(null);

        Assert.False(mobile.IsScreenFilterOpen);
        Assert.Null(mobile.ScreenFilter);
        Assert.Equal(3, GridAlbums(mobile.AlbumGridRows).Count);
    }

    [AvaloniaFact]
    public void The_search_screen_has_no_filter()
    {
        using var scope = Build();
        var mobile = scope.Mobile;
        mobile.SelectTabCommand.Execute(nameof(MobileTab.Search));

        Assert.False(mobile.OpenScreenFilter());
        Assert.False(mobile.IsScreenFilterOpen);
    }

    // Search's box is the filter's oval, in the same place, always there on
    // that screen - its own box in it rather than the filter's, and the
    // results starting below it. A pull there opens nothing more.
    [AvaloniaFact]
    public void The_search_box_is_the_oval()
    {
        using var scope = Build();
        var mobile = scope.Mobile;
        mobile.SelectTabCommand.Execute(nameof(MobileTab.Search));
        var window = ShowView(mobile);

        var slot = LiveSlot(window);
        var oval = OvalOf(slot);
        var search = slot.GetVisualDescendants().OfType<TextInput>().Single(b => b.Name == "SearchTabBox");
        Assert.True(oval.IsEffectivelyVisible);
        Assert.True(search.IsEffectivelyVisible);
        Assert.False(BoxOf(slot).IsEffectivelyVisible);
        var icon = Assert.Single(oval.GetVisualDescendants().OfType<MaterialIcon>(), i => i.IsEffectivelyVisible && i.Kind != MaterialIconKind.Close);
        Assert.Equal(MaterialIconKind.Magnify, icon.Kind);
        Assert.Same(oval, search.GetVisualAncestors().OfType<Border>().First(b => b.Name == "FilterOval"));

        Pull(slot);
        Assert.False(mobile.IsScreenFilterOpen);
        window.Close();
    }

    private static Window ShowView(MobileMainViewModel mobile)
    {
        var view = new MobileMainView { DataContext = mobile };
        // Theme first: content added before it never gets its templates.
        var window = new Window { Width = 390, Height = 800 };
        window.Styles.Add(new FluentTheme());
        window.Content = view;
        window.Show();
        Settle(window);
        return window;
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 2; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private static ScreenSlot LiveSlot(Window window) =>
        window.GetVisualDescendants().OfType<ScreenSlot>().Single(s => s.IsLive);

    private static Border OvalOf(ScreenSlot slot) =>
        slot.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "FilterOval");

    private static Border BackPillOf(ScreenSlot slot) =>
        slot.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "BackPill");

    private static TextInput BoxOf(ScreenSlot slot) =>
        slot.GetVisualDescendants().OfType<TextInput>().Single(b => b.Name == "FilterBox");

    private static Control ScrollerOf(ScreenSlot slot) =>
        slot.GetVisualDescendants().OfType<Control>().First(RubberBandScroll.GetIsEnabled);

    private static void Pull(ScreenSlot slot) =>
        ScrollerOf(slot).RaiseEvent(new RoutedEventArgs(RubberBandScroll.PulledDownEvent));

    private static void PullPartway(ScreenSlot slot, double progress) =>
        ScrollerOf(slot).RaiseEvent(new PullingDownEventArgs(progress));

    // The view half: pulling the list down opens the oval in the header band,
    // between the back and settings buttons, with focus in it, what is typed reaches the view model, putting
    // the keyboard away leaves it up and the list filtered, and its x closes
    // it and turns the filtering off.
    [AvaloniaFact]
    public void Pulling_down_opens_the_oval_ready_to_type_and_its_x_turns_the_filter_off()
    {
        using var scope = Build();
        var mobile = scope.Mobile;
        mobile.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        var window = ShowView(mobile);

        var slot = LiveSlot(window);
        var oval = OvalOf(slot);
        var box = BoxOf(slot);
        Assert.False(oval.IsEffectivelyVisible);

        var heading = slot.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Albums");
        var headingTop = heading.TranslatePoint(default, window)!.Value.Y;

        Pull(slot);
        Settle(window);

        Assert.True(oval.IsEffectivelyVisible);
        Assert.Equal(1, oval.Opacity);
        Assert.True(box.IsFocused);
        Assert.Equal("Filter albums", box.PlaceholderText);
        // In the band, left of the settings button, and the screen below it
        // does not move to make room.
        var ovalTop = oval.TranslatePoint(default, window)!.Value;
        var settingsLeft = slot.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "SettingsPill")
            .TranslatePoint(default, window)!.Value.X;
        Assert.True(ovalTop.Y + oval.Bounds.Height <= ScreenSlot.HeaderHeight, $"oval ends at {ovalTop.Y + oval.Bounds.Height}");
        Assert.True(ovalTop.X + oval.Bounds.Width <= settingsLeft, $"oval ends at {ovalTop.X + oval.Bounds.Width}, settings starts at {settingsLeft}");
        Assert.Equal(headingTop, heading.TranslatePoint(default, window)!.Value.Y);

        box.Text = "karma";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("karma", mobile.ScreenFilter);
        WaitUntil(() => GridAlbums(mobile.AlbumGridRows).Count == 1, "the grid narrowing");

        window.FocusManager!.Focus(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(oval.IsEffectivelyVisible);
        Assert.Equal("karma", mobile.ScreenFilter);

        var close = oval.GetVisualDescendants().OfType<Button>().Single(b => b.Command == mobile.CloseScreenFilterCommand);
        close.Command!.Execute(null);
        Settle(window);

        Assert.False(oval.IsEffectivelyVisible);
        Assert.Null(mobile.ScreenFilter);
        Assert.Equal(3, GridAlbums(mobile.AlbumGridRows).Count);
        window.Close();
    }

    // The oval belongs to its screen: a screen being left keeps it up while
    // the next one gets ready and slides over it, rather than dropping it the
    // moment the navigation starts, and Back brings the screen in with it on.
    [AvaloniaFact]
    public void The_oval_goes_and_comes_back_with_its_screen()
    {
        using var scope = Build();
        var mobile = scope.Mobile;
        mobile.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        var window = ShowView(mobile);
        var grid = LiveSlot(window);
        Pull(grid);
        Settle(window);
        BoxOf(grid).Text = "karma";
        WaitUntil(() => GridAlbums(mobile.AlbumGridRows).Count == 1, "the grid narrowing");

        mobile.SelectAlbumOrArtistCommand.Execute("OK Computer");

        // The live filter is gone at once; the screen being left still shows it.
        Assert.Null(mobile.ScreenFilter);
        Assert.True(OvalOf(grid).IsEffectivelyVisible);
        Assert.Equal("karma", BoxOf(grid).Text);

        MainViewModelHarness.WaitForTheDrillIn(mobile, "OK Computer");
        Settle(window);
        var album = LiveSlot(window);
        Assert.NotSame(grid, album);
        Assert.False(OvalOf(album).IsEffectivelyVisible);
        // The back button is the screen's too, in its own slot.
        Assert.True(BackPillOf(album).IsVisible);
        Assert.NotSame(BackPillOf(grid), BackPillOf(album));

        mobile.BackCommand.Execute(null);
        WaitUntil(() => mobile.IsShowingAlbumGrid, "landing back on the grid");
        Settle(window);

        var back = LiveSlot(window);
        Assert.True(OvalOf(back).IsEffectivelyVisible);
        Assert.Equal("karma", BoxOf(back).Text);
        window.Close();
    }

    // The pull fades the oval in as it goes - the further, the more opaque -
    // and a pull let go too early fades it back out without opening anything.
    [AvaloniaFact]
    public void Pulling_fades_the_oval_in_by_how_far_the_pull_has_gone()
    {
        using var scope = Build();
        var mobile = scope.Mobile;
        mobile.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        var window = ShowView(mobile);
        var slot = LiveSlot(window);
        var oval = OvalOf(slot);

        PullPartway(slot, 0.3);
        Settle(window);
        Assert.True(oval.IsEffectivelyVisible);
        Assert.Equal(0.3, oval.Opacity, 3);
        Assert.True(BoxOf(slot).IsEffectivelyVisible);
        Assert.Equal("Filter albums", BoxOf(slot).PlaceholderText);
        Assert.False(oval.IsHitTestVisible);

        PullPartway(slot, 0.8);
        Assert.Equal(0.8, oval.Opacity, 3);

        PullPartway(slot, 0);
        Settle(window);
        Assert.False(oval.IsEffectivelyVisible);
        Assert.False(mobile.IsScreenFilterOpen);
        window.Close();
    }
}
