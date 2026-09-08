using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Flower.Controls;
using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels.Mobile;
using Flower.Views.Mobile.Screens;

using Xunit;

namespace Flower.Tests;

// What a screen sliding in has actually MEASURED by the time it lands.
//
// ScreenStackPanel plays a forward navigation as an entrance: the incoming
// screen starts a screen-width off to one side and eases to 0 (see
// SyncToCurrentFrame's own comment, and MobileNavigationTransitionTests for
// the ViewModel half deciding which side). That start position is a
// RenderTransform, and an effective viewport is computed through render
// transforms - so a screen laid out while it is parked off-screen sees an
// empty viewport, and every virtualizing list on it realizes a single row.
// Nothing re-measures it afterwards either, because easing the transform back
// to 0 repaints without invalidating layout. The screen arrives holding one
// entry and holds it until some unrelated navigation happens to measure it
// again - reported as "the view comes up with two or three songs, and the
// rest appears if I switch away and come back".
//
// Real rows in a real window, not a transform assertion: the whole bug lives
// between the transform and the layout system, so anything mocking either end
// of that would pass with the bug in place.
[Collection("PlatformDataDirectory")]
public class ScreenStackPanelEntranceTests : PinnedDataDirectory
{
    private const double PanelHeight = 700;

    // Far more than one screenful, so "realized what fits" and "realized
    // essentially nothing" are impossible to confuse.
    private const int TrackCount = 300;

    private sealed class Harness : IDisposable
    {
        public Window Window { get; }
        public ScreenStackPanel Panel { get; }
        public MobileMainViewModel Vm => _vm.Mobile;

        private readonly MainViewModelHarness.MobileParts _vm;

        public Harness()
        {
            var tracks = Enumerable.Range(0, TrackCount).Select(i => new Track
            {
                Title = $"Track {i:D3}", Path = $"/music/{i}.mp3",
                Album = $"Album {i / 10}", Artists = $"Artist {i % 7}",
            }).ToList();

            _vm = MainViewModelHarness.BuildMobile(new Library(tracks), new MainPlaylist(tracks));
            Panel = new ScreenStackPanel { DataContext = Vm, Background = Brushes.Transparent };
            Window = new Window { Width = 400, Height = PanelHeight };
            // Without a theme nothing here has a control template, so a
            // ListBox never builds an items presenter and the visual tree
            // comes back empty - see TrackRowArtistTests, same reason.
            Window.Styles.Add(new FluentTheme());
            Window.Content = Panel;
            Window.Show();
            Pump();
        }

        // A real dispatcher loop rather than RunJobs: the entrance easing
        // ticks on AnimationClock's timer, and this test is about what the
        // layout system does around it.
        public static void Pump(int milliseconds = 120)
        {
            using var cts = new CancellationTokenSource(milliseconds);
            Dispatcher.UIThread.MainLoop(cts.Token);
        }

        public void SelectTab(MobileTab tab)
        {
            Vm.SelectTabCommand.Execute(tab.ToString());
            Pump();
        }

        // How many row containers the current screen's track list actually
        // built - the count a virtualizing panel decides from its viewport.
        public int RealizedRows()
        {
            var trackList = Panel.Children.LastOrDefault()?
                .GetLogicalDescendants().OfType<TrackListScreenView>().FirstOrDefault();
            Assert.NotNull(trackList);
            var box = trackList!.GetLogicalDescendants().OfType<ListBox>().FirstOrDefault();
            Assert.NotNull(box);
            Assert.Equal(TrackCount, box!.ItemCount);
            return box.GetVisualDescendants().OfType<ListBoxItem>().Count();
        }

        // Enough rows to be filling the viewport rather than answering an
        // empty one. A 700px window fits well over five rows; the bug leaves
        // exactly one.
        public const int EnoughToBeShowing = 5;

        public void Dispose()
        {
            // Let any entrance still in flight finish, so its easing does not
            // outlive the window it is animating.
            Pump(600);
            Window.Close();
            _vm.Dispose();
        }
    }

    [AvaloniaFact]
    public void A_screen_that_slides_in_arrives_already_populated()
    {
        using var h = new Harness();

        // A tab tap, which is the animated case: Songs is reached from the
        // right, over a Recently Added that stays put underneath.
        h.SelectTab(MobileTab.Songs);

        Assert.True(h.RealizedRows() >= Harness.EnoughToBeShowing,
            $"the Songs list arrived with {h.RealizedRows()} of {TrackCount} rows realized - "
            + "it was measured while parked off-screen by its entrance animation");
    }

    // The same list, reached the same way, after it has been laid out once
    // before - the state the reporter reached by "switching view and going
    // back", and the reason the bug reads as intermittent rather than total.
    [AvaloniaFact]
    public void A_screen_revisited_stays_populated()
    {
        using var h = new Harness();
        h.SelectTab(MobileTab.Songs);
        h.SelectTab(MobileTab.Albums);
        h.SelectTab(MobileTab.Songs);

        Assert.True(h.RealizedRows() >= Harness.EnoughToBeShowing);
    }
}
