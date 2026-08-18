using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

using Flower.Controls;
using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels.Mobile;

using Xunit;

namespace Flower.Tests;

// docs/ARCHITECTURE-REVIEW.md §5.8 / §5.9: ScreenStackPanel's swipe state
// machine, driven through a shown headless window's real input pipeline
// rather than by calling its private handlers, so the two-stage detection
// (direction decided early at EarlyCommitThreshold, go/no-go decided on
// release at SwipeThreshold), the explicit pointer capture, and the live
// interactive drag are all exercised as gestures.
//
// The panel hosts real screen controls built from a real MobileMainViewModel
// (see MainViewModelHarness), because the interactive path only engages when
// there is genuinely something to reveal - PeekOneBack/PeekOneForward have to
// be real frames with real controls behind them.
[Collection("PlatformDataDirectory")]
public class ScreenStackPanelSwipeTests : PinnedDataDirectory
{
    private const double PanelWidth = 400;

    // Comfortably past EarlyCommitThreshold (18) and SwipeThreshold (60).
    private const double PastThreshold = 140;

    // Past EarlyCommitThreshold, so the gesture is recognised and captured,
    // but under SwipeThreshold, so release must cancel rather than commit.
    private const double UnderThreshold = 30;

    private sealed class Harness : IDisposable
    {
        public Window Window { get; }
        public ScreenStackPanel Panel { get; }
        public MobileMainViewModel Vm { get; }

        public Harness()
        {
            var tracks = Enumerable.Range(0, 8).Select(i => new Track
            {
                Title = $"Track {i}", Path = $"/music/{i}.mp3", Album = $"Album {i / 4}", Artists = "An Artist",
            }).ToList();

            Vm    = MainViewModelHarness.BuildMobile(new Library(tracks), new MainPlaylist(tracks));
            // Transparent (not null) background purely so the panel is
            // hit-testable here. In the app each screen paints its own opaque
            // AppBackgroundBrush, but that is an App.axaml resource and this
            // suite runs on a bare Application, so it resolves to nothing and
            // no pointer event would ever reach the panel. Nothing about the
            // gesture itself is changed by this - the panel's own handlers are
            // Tunnel with handledEventsToo, so they see the event either way.
            Panel = new ScreenStackPanel { DataContext = Vm, Background = Brushes.Transparent };
            Window = new Window { Width = PanelWidth, Height = 700, Content = Panel };
            Window.Show();
            Pump();
        }

        public static void Pump(int milliseconds = 60)
        {
            using var cts = new CancellationTokenSource(milliseconds);
            Dispatcher.UIThread.MainLoop(cts.Token);
        }

        // Vertical centre of the panel, well clear of any header.
        private static Point At(double x) => new(x, 400);

        public void Press(double x) => Window.MouseDown(At(x), MouseButton.Left);
        public void Move(double x)  => Window.MouseMove(At(x), RawInputModifiers.LeftMouseButton);
        public void Release(double x) => Window.MouseUp(At(x), MouseButton.Left);

        // One whole gesture: press at 200, drag by `dx` in a few steps, release.
        public void Swipe(double dx, bool release = true)
        {
            const double startX = 200;
            Press(startX);
            for (var i = 1; i <= 4; i++)
                Move(startX + dx * i / 4);
            if (release)
                Release(startX + dx);
            Dispatcher.UIThread.RunJobs();
        }

        // A drag that is mostly vertical - an ordinary scroll, not a swipe.
        // Deliberately long enough horizontally (100px, past SwipeThreshold) to
        // have committed had it been mistaken for a swipe - it is rejected on
        // the dx-vs-dy ratio alone, not for being short.
        public void DragVertically()
        {
            Window.MouseDown(new Point(150, 100), MouseButton.Left);
            Window.MouseMove(new Point(175, 250), RawInputModifiers.LeftMouseButton);
            Window.MouseMove(new Point(220, 400), RawInputModifiers.LeftMouseButton);
            Window.MouseMove(new Point(250, 550), RawInputModifiers.LeftMouseButton);
            Window.MouseUp(new Point(250, 550), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }

        // X translation of the topmost (current) slot - what the live drag moves.
        public double CurrentSlotOffset()
        {
            var top = Panel.Children.LastOrDefault();
            return top?.RenderTransform is TranslateTransform t ? t.X : 0;
        }

        // SelectedTab's setter is private - navigation goes through the same
        // commands the View binds to, which is also what pushes history.
        public void SelectTab(MobileTab tab)
        {
            Vm.SelectTabCommand.Execute(tab.ToString());
            Pump();
        }

        // Drives a navigation the same way tapping into an album does, so
        // there is a history entry to swipe back to.
        public void DrillIn()
        {
            SelectTab(MobileTab.Albums);
            Vm.SelectAlbumOrArtistCommand.Execute("Album 0");
            Pump();
        }

        // Waits out ScreenStackPanel's 280ms commit easing plus its final
        // navigation callback.
        public void LetEasingFinish() => Pump(600);

        public void Dispose() => Window.Close();
    }

    // ── Direction detection ───────────────────────────────────────────────────

    // A vertical/ambiguous drag is an ordinary scroll: tracking is abandoned so
    // release does nothing, and the pointer is never captured, leaving whatever
    // is underneath (a ScrollViewer/ListBox) to handle it normally.
    [AvaloniaFact]
    public void A_vertical_drag_is_not_a_swipe()
    {
        using var h = new Harness();
        h.DrillIn();
        Assert.True(h.Vm.CanGoBack);

        h.DragVertically();
        h.LetEasingFinish();

        Assert.Equal(MobileScreenKind.TrackList, h.Vm.CurrentFrame.ScreenKind);
        Assert.Equal(0, h.CurrentSlotOffset());
    }

    // Under EarlyCommitThreshold in both axes, nothing is decided at all.
    [AvaloniaFact]
    public void A_tiny_drag_never_becomes_a_gesture()
    {
        using var h = new Harness();
        h.DrillIn();

        h.Swipe(6, release: false);

        // Nothing may move before EarlyCommitThreshold is crossed - after
        // release a too-short drag would spring back to 0 either way, so this
        // has to be observed mid-gesture.
        Assert.Equal(0, h.CurrentSlotOffset());

        h.Release(206);
        h.LetEasingFinish();
        Assert.Equal(MobileScreenKind.TrackList, h.Vm.CurrentFrame.ScreenKind);
    }

    // ── Interactive (reveal) swipes ───────────────────────────────────────────

    // With something to reveal, the drag is live: the current screen tracks the
    // finger rather than waiting for release.
    [AvaloniaFact]
    public void Dragging_right_with_history_moves_the_current_screen_live()
    {
        using var h = new Harness();
        h.DrillIn();

        h.Swipe(PastThreshold, release: false);

        Assert.True(h.CurrentSlotOffset() > 0, "the current screen did not follow the finger");
    }

    // Clamped so the reveal never goes past fully uncovering what is underneath,
    // and a gesture that wobbles back past its own start just holds at 0 rather
    // than sliding the wrong way.
    [AvaloniaFact]
    public void The_live_drag_is_clamped_to_the_panel_width_and_never_inverts()
    {
        using var h = new Harness();
        h.DrillIn();

        h.Press(200);
        h.Move(200 + 40);
        h.Move(200 + 4000);
        Assert.True(h.CurrentSlotOffset() <= PanelWidth, "the reveal went past the screen underneath");

        h.Move(200 - 4000);
        Assert.Equal(0, h.CurrentSlotOffset());

        h.Release(200);
        h.LetEasingFinish();
    }

    // Past SwipeThreshold on release: commit. The navigation only happens once
    // the outgoing screen has eased fully off-screen.
    [AvaloniaFact]
    public void A_committed_right_swipe_navigates_back()
    {
        using var h = new Harness();
        h.DrillIn();
        Assert.Equal(MobileScreenKind.TrackList, h.Vm.CurrentFrame.ScreenKind);

        h.Swipe(PastThreshold);
        h.LetEasingFinish();

        Assert.Equal(MobileScreenKind.AlbumGrid, h.Vm.CurrentFrame.ScreenKind);
        Assert.True(h.Vm.CanGoForward, "a completed back swipe should fill the redo stack");
    }

    // The navigation is deliberately deferred until the outgoing screen has
    // eased fully off-screen, so the state change and the resync it triggers
    // never race the animation still in flight.
    [AvaloniaFact]
    public void A_committed_swipe_does_not_navigate_until_the_easing_finishes()
    {
        using var h = new Harness();
        h.DrillIn();

        h.Swipe(PastThreshold);
        Harness.Pump(40); // well inside the 280ms easing

        Assert.Equal(MobileScreenKind.TrackList, h.Vm.CurrentFrame.ScreenKind);

        h.LetEasingFinish();
        Assert.Equal(MobileScreenKind.AlbumGrid, h.Vm.CurrentFrame.ScreenKind);
    }

    // Released under SwipeThreshold: cancel, and spring the current screen back
    // to where it started rather than leaving it stranded mid-drag.
    [AvaloniaFact]
    public void An_abandoned_right_swipe_cancels_and_springs_back()
    {
        using var h = new Harness();
        h.DrillIn();

        h.Swipe(UnderThreshold);
        h.LetEasingFinish();

        Assert.Equal(MobileScreenKind.TrackList, h.Vm.CurrentFrame.ScreenKind);
        Assert.Equal(0, h.CurrentSlotOffset(), 1);
    }

    // The browser-style redo counterpart: after going back, a left swipe goes
    // forward again.
    [AvaloniaFact]
    public void A_committed_left_swipe_navigates_forward_again()
    {
        using var h = new Harness();
        h.DrillIn();
        h.Swipe(PastThreshold);
        h.LetEasingFinish();
        Assert.True(h.Vm.CanGoForward);
        Assert.Equal(MobileScreenKind.AlbumGrid, h.Vm.CurrentFrame.ScreenKind);

        h.Swipe(-PastThreshold);
        h.LetEasingFinish();

        Assert.False(h.Vm.CanGoForward);
        Assert.Equal(MobileScreenKind.TrackList, h.Vm.CurrentFrame.ScreenKind);
    }

    [AvaloniaFact]
    public void Dragging_left_with_a_redo_entry_moves_the_current_screen_the_other_way()
    {
        using var h = new Harness();
        h.DrillIn();
        h.Swipe(PastThreshold);
        h.LetEasingFinish();

        h.Swipe(-PastThreshold, release: false);

        Assert.True(h.CurrentSlotOffset() < 0);

        h.Release(200 - PastThreshold);
        h.LetEasingFinish();
    }

    // ── Discrete (tab-paging) swipes ──────────────────────────────────────────

    // Nothing to reveal in that direction, so the gesture stays discrete: no
    // live drag at all, and the decision is made purely on release.
    [AvaloniaFact]
    public void With_no_history_a_left_swipe_pages_to_the_next_tab_without_dragging()
    {
        using var h = new Harness();
        h.SelectTab(MobileTab.Songs);
        Assert.False(h.Vm.CanGoForward);

        h.Swipe(-PastThreshold, release: false);
        Assert.Equal(0, h.CurrentSlotOffset());

        h.Release(200 - PastThreshold);
        Harness.Pump();

        Assert.Equal(MobileTab.Albums, h.Vm.SelectedTab);
    }

    // Switching tabs pushes a history entry of its own, so a right swipe after
    // one retraces that jump rather than paging to the tab sitting to its left
    // - the whole point of the history stack over the old algorithmic unwind.
    [AvaloniaFact]
    public void After_a_tab_switch_a_right_swipe_retraces_it_rather_than_paging()
    {
        using var h = new Harness();
        h.SelectTab(MobileTab.Albums);
        Assert.True(h.Vm.CanGoBack);

        h.Swipe(PastThreshold);
        h.LetEasingFinish();

        Assert.Equal(MobileTab.RecentlyAdded, h.Vm.SelectedTab);
    }

    // Recognised as horizontal, but not far enough on release to page.
    [AvaloniaFact]
    public void A_short_discrete_swipe_does_not_page()
    {
        using var h = new Harness();
        h.SelectTab(MobileTab.Songs);

        h.Swipe(-UnderThreshold);
        Harness.Pump();

        Assert.Equal(MobileTab.Songs, h.Vm.SelectedTab);
    }

    // Tab paging stops at the ends rather than wrapping around.
    [AvaloniaFact]
    public void Tab_paging_stops_at_the_first_and_last_tab()
    {
        using var h = new Harness();
        h.SelectTab(MobileTab.RecentlyAdded);

        h.Swipe(PastThreshold);
        Harness.Pump();
        Assert.Equal(MobileTab.RecentlyAdded, h.Vm.SelectedTab);

        h.SelectTab(MobileTab.Search);

        h.Swipe(-PastThreshold);
        Harness.Pump();
        Assert.Equal(MobileTab.Search, h.Vm.SelectedTab);
    }

    // ── The fixed back button ─────────────────────────────────────────────────

    // AnimateGoBack reuses the interactive commit path so the overlay back
    // button plays the same slide-off as a swipe, rather than cutting straight
    // to the destination.
    [AvaloniaFact]
    public void AnimateGoBack_navigates_back_through_the_same_commit_path()
    {
        using var h = new Harness();
        h.DrillIn();

        h.Panel.AnimateGoBack();
        h.LetEasingFinish();

        Assert.Equal(MobileScreenKind.AlbumGrid, h.Vm.CurrentFrame.ScreenKind);
        Assert.True(h.Vm.CanGoForward);
    }

    [AvaloniaFact]
    public void AnimateGoBack_does_nothing_with_no_history()
    {
        using var h = new Harness();
        var tab = h.Vm.SelectedTab;

        h.Panel.AnimateGoBack();
        h.LetEasingFinish();

        Assert.Equal(tab, h.Vm.SelectedTab);
        Assert.False(h.Vm.CanGoBack);
        // Nothing was animated either: without the CanGoBack gate the current
        // screen eases off to the right and, with no navigation to follow and
        // reset its transform, is simply left stranded off-screen.
        Assert.Equal(0, h.CurrentSlotOffset());
    }
}
