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

namespace Flower.Tests.Views;

// What only a laid-out screen can show about a tab coming back: the album it
// was on comes back scrolled where it was, and a titled list still
// virtualizes. Which screen a tab comes back on is the view model's, and is
// MobileTabHistoryTests'.
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
