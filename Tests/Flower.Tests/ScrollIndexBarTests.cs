using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Flower.Controls;
using Flower.Models;
using Flower.Services;
using Flower.Tests.TestSupport;
using Flower.ViewModels.Mobile;
using Flower.Views.Mobile;
using Flower.Views.Mobile.Screens;

using Material.Icons.Avalonia;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

using Track = Flower.Models.Track;

namespace Flower.Tests;

// The strip down the right of mobile's long lists (ScrollIndexBar): a touch on
// a letter brings that letter's first entry to the top of the list, a drag
// keeps doing it, and on Recently Added - date order, no letters - the dots
// scrub the whole scroll. Driven with a real pointer through laid-out screens,
// because what can quietly do nothing here is the plumbing: whether the bar
// finds its list's scroller, whether a far jump realizes the row it lands on,
// and whether the gesture reaches the bar at all.
[Collection("PlatformDataDirectory")]
public class ScrollIndexBarTests : PinnedDataDirectory
{
    private const double Width = 390;
    private const double Height = 700;

    // Every letter but a few, so the bar has gaps to land past.
    private static readonly char[] UsedLetters = "ABCDEFGHIJKLNOPRSTUVWY".ToCharArray();

    private static Track TrackFor(string title, string album, string artist, int day) => new()
    {
        Title = title,
        Album = album,
        Artists = artist,
        Path = "/music/" + album + "/" + title + ".flac",
        DateAdded = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(day),
    };

    // Six albums per letter, one song each: a list long enough in every
    // shape - grid, songs, artists - that most letters are far off screen.
    private static MainViewModelHarness.MobileParts Build(int perLetter = 6)
    {
        var tracks = new List<Track>();
        var day = 0;
        foreach (var letter in UsedLetters)
        {
            for (var i = 0; i < perLetter; i++)
            {
                var name = $"{letter}{(char)('a' + i)} {i}";
                tracks.Add(TrackFor(name + " song", name + " album", name + " band", day++));
            }
        }

        var parts = MainViewModelHarness.BuildParts(new Library(tracks), new MainPlaylist(new List<Track>()));
        var mobile = new MobileMainViewModel(parts.Main, parts.PlaylistControl, parts.CurrentlyPlaying,
            NullLogger<MobileMainViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        return new MainViewModelHarness.MobileParts(mobile, parts);
    }

    private static Window Show(Control screen)
    {
        var window = new Window { Width = Width, Height = Height };
        window.Styles.Add(new FluentTheme());
        window.Content = screen;
        window.Show();
        Pump();
        return window;
    }

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static ScrollIndexBar Bar(Window window) =>
        window.GetVisualDescendants().OfType<ScrollIndexBar>().Single(b => b.IsEffectivelyVisible);

    private static Point OnBar(Window window, ScrollIndexBar bar, double y) =>
        bar.TranslatePoint(new Point(bar.Bounds.Width / 2, y), window)!.Value;

    private static void Tap(Window window, Point point)
    {
        window.MouseDown(point, MouseButton.Left);
        Pump();
        window.MouseUp(point, MouseButton.Left);
        Pump();
    }

    // How far below the top of the visible list item `index` of `items` is -
    // zero when it is the first thing showing.
    private static double DistanceFromTop(ItemsControl items, ScrollIndexBar bar, int index)
    {
        var container = items.ContainerFromIndex(index);
        Assert.NotNull(container);
        var window = TopLevel.GetTopLevel(bar)!;
        var top = container!.TranslatePoint(default, window)!.Value.Y;
        var barTop = bar.TranslatePoint(default, window)!.Value.Y;
        return top - barTop;
    }

    private static int RowWith(MobileMainViewModel vm, char letter) =>
        vm.AlbumGridRows.Select((row, i) => (row, i))
            .First(r => r.row.Tiles.Any(t => t.Name.StartsWith(letter))).i;

    [AvaloniaFact]
    public void Touching_a_letter_brings_its_first_album_to_the_top_of_the_grid()
    {
        using var parts = Build();
        var vm = parts.Mobile;
        vm.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        var window = Show(new AlbumGridScreenView { DataContext = vm });
        var bar = Bar(window);
        var rows = window.GetVisualDescendants().OfType<ItemsControl>().First(i => i.Name == "Rows");

        Tap(window, OnBar(window, bar, bar.YOf('O')));

        Assert.Equal(0, DistanceFromTop(rows, bar, RowWith(vm, 'O')), precision: 0);
        window.Close();
    }

    [AvaloniaFact]
    public void A_letter_nothing_is_filed_under_lands_on_the_next_one_that_has_something()
    {
        using var parts = Build();
        var vm = parts.Mobile;
        vm.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        var window = Show(new AlbumGridScreenView { DataContext = vm });
        var bar = Bar(window);
        var rows = window.GetVisualDescendants().OfType<ItemsControl>().First(i => i.Name == "Rows");

        // No album starts with M.
        Tap(window, OnBar(window, bar, bar.YOf('M')));

        Assert.Equal(0, DistanceFromTop(rows, bar, RowWith(vm, 'N')), precision: 0);
        window.Close();
    }

    // The grid gives the bar a column rather than letting it sit on the art:
    // the tiles are sized off what is left, and the gap from the last tile's
    // art to the bar is the same as from the screen's left edge to the first.
    [AvaloniaFact]
    public void The_album_art_stops_short_of_the_bar()
    {
        using var parts = Build();
        var vm = parts.Mobile;
        vm.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        var window = Show(new AlbumGridScreenView { DataContext = vm });
        var bar = Bar(window);

        var art = window.GetVisualDescendants().OfType<SquareAlbumArtView>()
            .Select(a => new Rect(a.TranslatePoint(default, window)!.Value, a.Bounds.Size)).ToList();
        var barLeft = bar.TranslatePoint(default, window)!.Value.X;

        Assert.True(art.Max(a => a.Right) < barLeft);
        Assert.Equal(art.Min(a => a.Left), barLeft - art.Max(a => a.Right), precision: 0);
        window.Close();
    }

    // The finger moving is the point of it: every letter it passes over is a
    // jump, not just the one it went down on.
    [AvaloniaFact]
    public void Dragging_along_the_bar_follows_the_finger()
    {
        using var parts = Build();
        var vm = parts.Mobile;
        vm.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        var window = Show(new AlbumGridScreenView { DataContext = vm });
        var bar = Bar(window);
        var rows = window.GetVisualDescendants().OfType<ItemsControl>().First(i => i.Name == "Rows");

        window.MouseDown(OnBar(window, bar, bar.YOf('C')), MouseButton.Left);
        Pump();
        Assert.Equal(0, DistanceFromTop(rows, bar, RowWith(vm, 'C')), precision: 0);

        window.MouseMove(OnBar(window, bar, bar.YOf('H')), RawInputModifiers.LeftMouseButton);
        Pump();
        Assert.Equal(0, DistanceFromTop(rows, bar, RowWith(vm, 'H')), precision: 0);

        // ...and back up, past where it started.
        window.MouseMove(OnBar(window, bar, bar.YOf('B')), RawInputModifiers.LeftMouseButton);
        Pump();
        Assert.Equal(0, DistanceFromTop(rows, bar, RowWith(vm, 'B')), precision: 0);

        window.MouseUp(OnBar(window, bar, bar.YOf('B')), MouseButton.Left);
        window.Close();
    }

    [AvaloniaFact]
    public void Touching_a_letter_on_Songs_brings_its_first_song_to_the_top()
    {
        using var parts = Build();
        var vm = parts.Mobile;
        vm.SelectTabCommand.Execute(nameof(MobileTab.Songs));
        WaitFor(() => vm.Main.Rows.Count == UsedLetters.Length * 6);
        var view = new TrackListScreenView { DataContext = vm };
        var window = Show(view);
        view.ObserveLive(vm);
        Pump();

        var bar = Bar(window);
        var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "TrackListBox");

        Tap(window, OnBar(window, bar, bar.YOf('P')));

        var first = vm.Main.Rows.Select((r, i) => (r, i)).First(r => r.r.Track.Title!.StartsWith('P')).i;
        Assert.Equal(0, DistanceFromTop(list, bar, first), precision: 0);
        window.Close();
    }

    [AvaloniaFact]
    public void Touching_a_letter_on_Artists_brings_its_first_artist_to_the_top()
    {
        using var parts = Build();
        var vm = parts.Mobile;
        vm.SelectTabCommand.Execute(nameof(MobileTab.Artists));
        WaitFor(() => vm.ArtistPickerItems.Count == UsedLetters.Length * 6);
        var window = Show(new ArtistPickerScreenView { DataContext = vm });
        var bar = Bar(window);
        var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "Artists");

        Tap(window, OnBar(window, bar, bar.YOf('K')));

        var first = vm.ArtistPickerItems.Select((r, i) => (r, i)).First(r => r.r.Name.StartsWith('K')).i;
        Assert.Equal(0, DistanceFromTop(list, bar, first), precision: 0);
        window.Close();
    }

    // Recently Added is in date order, so its bar is dots and scrubs the
    // scroll itself: the ends of the bar are the ends of the list.
    [AvaloniaFact]
    public void The_dots_on_Recently_Added_scrub_the_whole_scroll()
    {
        using var parts = Build();
        var vm = parts.Mobile;
        var window = Show(new RecentlyAddedScreenView { DataContext = vm });
        var bar = Bar(window);
        Assert.Null(bar.IndexOfLetter);
        var scroller = window.GetVisualDescendants().OfType<ScrollViewer>().First();
        // Read when asked, not once: the grid re-lays itself out around the
        // bar's column once the bar appears, which moves its length.
        double Max() => scroller.Extent.Height - scroller.Viewport.Height;
        var (top, bottom) = bar.Span;

        window.MouseDown(OnBar(window, bar, bottom + 20), MouseButton.Left);
        Pump();
        Assert.Equal(Max(), scroller.Offset.Y, precision: 0);

        window.MouseMove(OnBar(window, bar, (top + bottom) / 2), RawInputModifiers.LeftMouseButton);
        Pump();
        Assert.Equal(Max() / 2, scroller.Offset.Y, precision: 0);

        window.MouseMove(OnBar(window, bar, top - 20), RawInputModifiers.LeftMouseButton);
        Pump();
        Assert.Equal(0, scroller.Offset.Y, precision: 0);

        window.MouseUp(OnBar(window, bar, top), MouseButton.Left);
        window.Close();
    }

    // A handful of albums fits on the screen; a bar to jump around it would
    // be clutter, and on Songs and Artists it would cost the rows their width.
    [AvaloniaFact]
    public void A_list_with_nothing_to_scroll_gets_no_bar()
    {
        using var parts = Build(perLetter: 1);
        var vm = parts.Mobile;
        vm.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        var window = Show(new AlbumGridScreenView { DataContext = vm });
        var bar = window.GetVisualDescendants().OfType<ScrollIndexBar>().Single();
        Assert.NotEqual(0, bar.Bounds.Width);
        window.Close();

        // Two albums, one row: nothing to scroll.
        using var few = BuildWith(2);
        few.Mobile.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        window = Show(new AlbumGridScreenView { DataContext = few.Mobile });
        bar = window.GetVisualDescendants().OfType<ScrollIndexBar>().Single();
        Assert.Equal(0, bar.Bounds.Width);
        window.Close();
    }

    // Only the flat Songs list is in title order; an album is in its own.
    [AvaloniaFact]
    public void An_album_has_no_bar()
    {
        using var parts = Build();
        var vm = parts.Mobile;
        vm.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        var album = vm.AlbumGridRows[0].Tiles[0].Name;
        vm.SelectAlbumOrArtistCommand.Execute(album);
        MainViewModelHarness.WaitForTheDrillIn(vm, album);
        var view = new TrackListScreenView { DataContext = vm };
        var window = Show(view);
        view.ObserveLive(vm);
        Pump();

        Assert.DoesNotContain(window.GetVisualDescendants().OfType<ScrollIndexBar>(), b => b.IsEffectivelyVisible);
        window.Close();
    }

    // The screens sit in ScreenStackPanel, which takes a sideways drag for a
    // swipe between tabs. A finger on the bar that wanders sideways is still
    // scrubbing, and must not be taken off it mid-drag.
    [AvaloniaFact]
    public void A_sideways_wander_on_the_bar_is_not_a_swipe()
    {
        using var parts = Build();
        var vm = parts.Mobile;
        var window = new Window { Width = Width, Height = 844 };
        window.Styles.Add(new FluentTheme());
        window.Styles.Add(new MaterialIconStyles(null));
        window.Content = new MobileMainView { DataContext = vm };
        window.Show();
        vm.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        // The tab switch slides the grid in.
        LetEasingFinish();

        // The screens either side are kept alive, bars and all.
        var grid = window.GetVisualDescendants().OfType<AlbumGridScreenView>().Single();
        var bar = grid.GetVisualDescendants().OfType<ScrollIndexBar>().Single();
        var rows = grid.GetVisualDescendants().OfType<ItemsControl>().First(i => i.Name == "Rows");
        var start = OnBar(window, bar, bar.YOf('C'));
        window.MouseDown(start, MouseButton.Left);
        Pump();

        // The press landed on the bar, not on something over it: the grid jumped.
        Assert.Equal(0, DistanceFromTop(rows, bar, RowWith(vm, 'C')), precision: 0);

        window.MouseMove(start + new Point(-120, 10), RawInputModifiers.LeftMouseButton);
        Pump();
        window.MouseMove(start + new Point(-200, 60), RawInputModifiers.LeftMouseButton);
        Pump();
        window.MouseUp(start + new Point(-200, 60), MouseButton.Left);
        LetEasingFinish();

        Assert.Equal(MobileTab.Albums, vm.SelectedTab);
        window.Close();
    }

    // Tab switches and swipes ease on AnimationClock, whose timer only fires
    // under a running MainLoop - RunJobs alone leaves a screen parked
    // mid-slide. Same finish line as ScreenStackPanelSwipeTests': an idle
    // clock.
    private static void LetEasingFinish()
    {
        var deadline = Environment.TickCount64 + 10_000;
        do
        {
            using var cts = new CancellationTokenSource(20);
            Dispatcher.UIThread.MainLoop(cts.Token);
        }
        while (AnimationClock.Current.SubscriberCount > 0 && Environment.TickCount64 < deadline);

        Assert.Equal(0, AnimationClock.Current.SubscriberCount);
        Pump();
    }

    private static MainViewModelHarness.MobileParts BuildWith(int albums)
    {
        var tracks = Enumerable.Range(0, albums)
            .Select(i => TrackFor("Song " + i, "Album " + i, "Band", i)).ToList();
        var parts = MainViewModelHarness.BuildParts(new Library(tracks), new MainPlaylist(new List<Track>()));
        var mobile = new MobileMainViewModel(parts.Main, parts.PlaylistControl, parts.CurrentlyPlaying,
            NullLogger<MobileMainViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        return new MainViewModelHarness.MobileParts(mobile, parts);
    }

    private static void WaitFor(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < deadline, "the list never filled");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
    }
}
