using System;
using System.Linq;
using System.Threading;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Flower.Controls;
using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels.Mobile;
using Flower.Views.Mobile;

using Xunit;

namespace Flower.Tests;

// A tab remembers what it was showing. Albums, into one album, over to Artists
// and back by the tab bar lands on that album again - scrolled where it was,
// with the grid under it for Back - rather than at the top of the grid. And a
// screen's title is the first line of its scroller, so it moves with the list
// instead of staying pinned in the header band.
//
// The whole MobileMainView rather than a bare ScreenStackPanel, because the
// list screens take their title from the screenScroll template declared there.
[Collection("PlatformDataDirectory")]
public class MobileTabMemoryTests : PinnedDataDirectory
{
    private const string BigAlbum = "Album 0";

    private sealed class Harness : IDisposable
    {
        public Window Window { get; }
        public ScreenStackPanel Panel { get; }
        public MobileMainViewModel Vm => _vm.Mobile;

        private readonly MainViewModelHarness.MobileParts _vm;

        public Harness()
        {
            // One long album and a long list of artists, so both have somewhere
            // to scroll to.
            var tracks = Enumerable.Range(0, 200).Select(i => new Track
            {
                Title = $"Track {i:D3}", Path = $"/music/{i}.mp3",
                Album = i < 100 ? BigAlbum : $"Album {i}", Artists = $"Artist {i:D3}",
            }).ToList();

            _vm = MainViewModelHarness.BuildMobile(new Library(tracks), new MainPlaylist(tracks));
            var view = new MobileMainView { DataContext = Vm };
            Window = new Window { Width = 400, Height = 700 };
            Window.Styles.Add(new FluentTheme());
            Window.Content = view;
            Window.Show();
            Panel = view.FindControl<ScreenStackPanel>("ScreenStack")!;
            Pump();
        }

        public static void Pump(int milliseconds = 150)
        {
            using var cts = new CancellationTokenSource(milliseconds);
            Dispatcher.UIThread.MainLoop(cts.Token);
        }

        public void SelectTab(MobileTab tab)
        {
            Vm.SelectTabCommand.Execute(tab.ToString());
            // Past the entrance easing, so the screen is at rest.
            Pump(500);
        }

        public Control CurrentScreen => Panel.Children.Last();

        public ScrollViewer Scroller =>
            CurrentScreen.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.IsEffectivelyVisible);

        public void ScrollTo(double y)
        {
            Scroller.Offset = new Vector(0, y);
            Pump();
            Assert.Equal(y, Scroller.Offset.Y);
        }

        public void Dispose()
        {
            Pump(600);
            Window.Close();
            _vm.Dispose();
        }
    }

    [AvaloniaFact]
    public void A_tab_comes_back_on_the_album_it_was_showing_with_the_grid_under_it()
    {
        using var h = new Harness();
        h.SelectTab(MobileTab.Albums);
        h.Vm.SelectAlbumOrArtistCommand.Execute(BigAlbum);
        MainViewModelHarness.WaitForTheDrillIn(h.Vm, BigAlbum);

        h.SelectTab(MobileTab.Artists);
        Assert.True(h.Vm.IsShowingArtistPicker);

        h.SelectTab(MobileTab.Albums);
        MainViewModelHarness.WaitForTheDrillIn(h.Vm, BigAlbum);
        Assert.True(h.Vm.IsShowingAlbumTrackList);

        // Back walks down through the tab before leaving it.
        h.Vm.BackCommand.Execute(null);
        Harness.Pump(300);
        Assert.True(h.Vm.IsShowingAlbumGrid);

        h.Vm.BackCommand.Execute(null);
        Harness.Pump(300);
        Assert.True(h.Vm.IsShowingArtistPicker);
    }

    [AvaloniaFact]
    public void A_tab_never_left_drilled_in_starts_at_its_root()
    {
        using var h = new Harness();
        h.SelectTab(MobileTab.Albums);
        h.SelectTab(MobileTab.Artists);
        h.SelectTab(MobileTab.Albums);

        Assert.True(h.Vm.IsShowingAlbumGrid);
    }

    [AvaloniaFact]
    public void An_album_comes_back_scrolled_where_it_was()
    {
        using var h = new Harness();
        h.SelectTab(MobileTab.Albums);
        h.Vm.SelectAlbumOrArtistCommand.Execute(BigAlbum);
        MainViewModelHarness.WaitForTheDrillIn(h.Vm, BigAlbum);
        Harness.Pump(500);
        h.ScrollTo(900);

        h.SelectTab(MobileTab.Artists);
        h.SelectTab(MobileTab.Albums);
        MainViewModelHarness.WaitForTheDrillIn(h.Vm, BigAlbum);
        Harness.Pump(300);

        Assert.Equal(900, h.Scroller.Offset.Y);
    }

    [AvaloniaFact]
    public void A_list_comes_back_scrolled_where_it_was()
    {
        using var h = new Harness();
        h.SelectTab(MobileTab.Artists);
        h.ScrollTo(1500);

        h.SelectTab(MobileTab.Albums);
        h.SelectTab(MobileTab.Artists);

        Assert.Equal(1500, h.Scroller.Offset.Y);
    }

    [AvaloniaFact]
    public void A_fresh_drill_in_starts_at_the_top()
    {
        using var h = new Harness();
        h.SelectTab(MobileTab.Albums);
        h.Vm.SelectAlbumOrArtistCommand.Execute(BigAlbum);
        MainViewModelHarness.WaitForTheDrillIn(h.Vm, BigAlbum);
        Harness.Pump(500);
        h.ScrollTo(900);
        h.Vm.BackCommand.Execute(null);
        Harness.Pump(300);

        h.Vm.SelectAlbumOrArtistCommand.Execute(BigAlbum);
        MainViewModelHarness.WaitForTheDrillIn(h.Vm, BigAlbum);
        Harness.Pump(500);

        Assert.Equal(0, h.Scroller.Offset.Y);
    }

    [AvaloniaFact]
    public void Tapping_the_tab_inside_an_album_goes_back_to_the_top_of_the_grid()
    {
        using var h = new Harness();
        h.SelectTab(MobileTab.Artists);
        h.SelectTab(MobileTab.Albums);
        h.ScrollTo(300);
        h.Vm.SelectAlbumOrArtistCommand.Execute(BigAlbum);
        MainViewModelHarness.WaitForTheDrillIn(h.Vm, BigAlbum);
        Harness.Pump(500);
        h.ScrollTo(900);

        h.SelectTab(MobileTab.Albums);

        Assert.True(h.Vm.IsShowingAlbumGrid);
        Assert.Equal(0, h.Scroller.Offset.Y);
        // The album is closed, not under the grid: Back leaves the tab.
        h.Vm.BackCommand.Execute(null);
        Harness.Pump(300);
        Assert.True(h.Vm.IsShowingArtistPicker);
    }

    [AvaloniaFact]
    public void Tapping_the_tab_on_its_first_screen_scrolls_it_to_the_top()
    {
        using var h = new Harness();
        h.SelectTab(MobileTab.Artists);
        h.ScrollTo(1500);

        h.SelectTab(MobileTab.Artists);

        Assert.True(h.Vm.IsShowingArtistPicker);
        // Scrolling back to the top is animated, so SelectTab's fixed pump is
        // a race against it rather than a wait for it - see UiWait.Settle.
        UiWait.Settle(() => h.Scroller.Offset.Y == 0, "the tab never scrolled back to the top");
    }

    [AvaloniaTheory]
    [InlineData(MobileTab.Albums, "Albums")]
    [InlineData(MobileTab.Artists, "Artists")]
    [InlineData(MobileTab.Playlists, "Playlists")]
    public void The_title_is_the_first_line_of_the_scroller(MobileTab tab, string title)
    {
        using var h = new Harness();
        h.SelectTab(tab);

        var titles = h.CurrentScreen.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Text == title && t.IsEffectivelyVisible)
            .ToList();
        var line = Assert.Single(titles);
        Assert.Same(h.Scroller, line.FindAncestorOfType<ScrollViewer>());

        // Under the header band, at rest, and carried up by a scroll.
        var before = line.TranslatePoint(default, h.Window)!.Value.Y;
        Assert.True(before >= ScreenSlot.HeaderHeight, $"title at {before}, inside the header band");
        if (h.Scroller.Extent.Height > h.Scroller.Viewport.Height + 40)
        {
            h.ScrollTo(40);
            Assert.Equal(before - 40, line.TranslatePoint(default, h.Window)!.Value.Y);
        }
    }

    // The list template wraps the items in a StackPanel with the title; the
    // panel inside must still realize a screenful, not the whole list.
    [AvaloniaFact]
    public void A_titled_list_still_virtualizes()
    {
        using var h = new Harness();
        h.SelectTab(MobileTab.Artists);

        var box = h.CurrentScreen.GetVisualDescendants().OfType<ListBox>().First(b => b.IsEffectivelyVisible);
        var realized = box.GetVisualDescendants().OfType<ListBoxItem>().Count();
        Assert.InRange(realized, 5, 60);
        Assert.Equal(200, box.ItemCount);
    }
}
