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

namespace Flower.Tests.Views;

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
        MainViewModelHarness.UseSuiteTabs(parts);
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
}
