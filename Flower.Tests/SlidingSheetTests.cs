using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

using Flower.Controls;
using Flower.Services;

using Xunit;

namespace Flower.Tests;

// Mobile's sheets arrive and leave by sliding, rather than blinking in and out
// of existence - see SlidingSheet, and MobileMainView.axaml where every sheet is
// wrapped in one. Two shapes, animated differently: a full-bleed sheet slides
// whole, while a card over a dimmed backdrop slides only the card (its own
// height, not the screen's) and fades the backdrop in behind it.
//
// Driven by a hand-stepped AnimationClock rather than by parking the dispatcher
// for the length of a real 280ms animation - the headless session has one
// dispatcher thread and every other test waits behind whoever holds it, so a
// suite of sheet animations run in real time is several wall-clock seconds
// charged to tests that have nothing to do with sheets.
public class SlidingSheetTests
{
    private const double Width = 400;
    private const double Height = 700;
    private const double CardHeight = 200;

    private sealed class Host
    {
        public required Window Window { get; init; }
        public required SlidingSheet Sheet { get; init; }
        public Control? Card { get; init; }

        public double SheetOffsetX => Offset(Sheet.RenderTransform).X;
        public double SheetOffsetY => Offset(Sheet.RenderTransform).Y;
        public double CardOffsetY => Offset(Card?.RenderTransform).Y;

        public required Action Advance { get; init; }

        // Time passing with no dispatcher job allowed to run in between.
        public required Action Tick { get; init; }

        // Lets the layout pass and the Loaded-priority job SlidingSheet.Open
        // posts for a card actually run - both are ordinary dispatcher jobs.
        public static void Settle() => Dispatcher.UIThread.RunJobs();

        private static (double X, double Y) Offset(ITransform? transform) =>
            transform is TranslateTransform t ? (t.X, t.Y) : (0, 0);
    }

    // A host the sheet fills, so its slide distance comes from a real arranged
    // width/height - the sheet's own Bounds are still zero at the moment it
    // opens, being hidden until then.
    private static Host Show(SheetEntrance entrance = SheetEntrance.FromRight, bool withCard = false)
    {
        Control? card = null;
        Control content;
        if (withCard)
        {
            card = new Border
            {
                Height = CardHeight,
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = Brushes.Black,
            };
            SlidingSheet.SetIsCard(card, true);
            content = new Grid
            {
                // The dimmed backdrop the real card sheets paint, which must
                // stay put while the card slides over it.
                Children = { new Border { Background = Brushes.Gray }, card },
            };
        }
        else
        {
            content = new Border { Background = Brushes.Black };
        }

        var now = TimeSpan.Zero;
        var clock = new AnimationClock(() => now);
        var sheet = new SlidingSheet { Entrance = entrance, Content = content, ClockOverride = clock };
        var window = new Window { Width = Width, Height = Height, Content = new Panel { Children = { sheet } } };
        window.Show();
        Host.Settle();

        return new Host
        {
            Window = window,
            Sheet = sheet,
            Card = card,
            // One step past the 280ms easing, so a single tick lands every
            // animation on its final frame - these tests are about where a
            // slide starts and ends, not how it is interpolated in between
            // (AnimationClockTests covers the tween itself).
            Advance = () =>
            {
                Host.Settle();
                now += TimeSpan.FromMilliseconds(400);
                clock.TickForTest();
                Host.Settle();
            },
            Tick = () =>
            {
                now += TimeSpan.FromMilliseconds(400);
                clock.TickForTest();
            },
        };
    }

    [AvaloniaFact]
    public void A_closed_sheet_is_not_shown_at_all()
    {
        var host = Show();
        try
        {
            Assert.False(host.Sheet.IsVisible);
        }
        finally
        {
            host.Window.Close();
        }
    }

    [AvaloniaFact]
    public void Opening_starts_off_the_right_edge_and_eases_home()
    {
        var host = Show();
        try
        {
            host.Sheet.IsOpen = true;

            Assert.True(host.Sheet.IsVisible);
            Assert.Equal(Width, host.SheetOffsetX);

            host.Advance();
            Assert.Equal(0, host.SheetOffsetX);
            Assert.True(host.Sheet.IsVisible);
        }
        finally
        {
            host.Window.Close();
        }
    }

    // The entrance waits for the layout that making the sheet visible triggers.
    // That pass is the most expensive frame a sheet ever has (it has never been
    // arranged before, being hidden until now) and it outranks the clock's own
    // timer, so an easing subscribed ahead of it gets no tick until it is over -
    // and the first tick it does get is already past the end of the animation,
    // which is the sheet appearing outright. That was Now Playing, whose album
    // art decodes in exactly that pass.
    [AvaloniaFact]
    public void An_entrance_does_not_start_before_the_layout_it_triggers()
    {
        var host = Show();
        try
        {
            host.Sheet.IsOpen = true;

            // Time passing before the deferred start has landed must not eat
            // any of the animation.
            host.Tick();
            Assert.Equal(Width, host.SheetOffsetX);

            host.Advance();
            Assert.Equal(0, host.SheetOffsetX);
        }
        finally
        {
            host.Window.Close();
        }
    }

    // The direction every sheet with a ChevronDown dismiss is already promising.
    [AvaloniaFact]
    public void A_bottom_sheet_starts_below_the_screen()
    {
        var host = Show(SheetEntrance.FromBottom);
        try
        {
            host.Sheet.IsOpen = true;

            Assert.Equal(Height, host.SheetOffsetY);
            Assert.Equal(0, host.SheetOffsetX);

            host.Advance();
            Assert.Equal(0, host.SheetOffsetY);
        }
        finally
        {
            host.Window.Close();
        }
    }

    // The card moves, the sheet does not - sliding the whole thing would drag
    // the dimmed backdrop up the screen with it, leaving the top undimmed
    // halfway through. The backdrop fades in instead.
    [AvaloniaFact]
    public void A_card_sheet_moves_its_card_and_fades_its_backdrop()
    {
        var host = Show(SheetEntrance.FromBottom, withCard: true);
        try
        {
            host.Sheet.IsOpen = true;

            // Transparent until the card has been laid out and can be parked
            // below the bottom edge - see SlidingSheet.Open.
            Assert.Equal(0, host.Sheet.Opacity);
            Assert.Equal(0, host.SheetOffsetY);

            host.Advance();
            Assert.Equal(1, host.Sheet.Opacity);
            Assert.Equal(0, host.CardOffsetY);
            Assert.Equal(0, host.SheetOffsetY);
        }
        finally
        {
            host.Window.Close();
        }
    }

    // Its own height, not the screen's: a 200px card parked 700px down would
    // spend most of the animation still below the bottom edge and then appear
    // all at once near the end.
    [AvaloniaFact]
    public void A_card_travels_its_own_height()
    {
        var host = Show(SheetEntrance.FromBottom, withCard: true);
        try
        {
            host.Sheet.IsOpen = true;
            host.Advance();

            host.Sheet.IsOpen = false;
            host.Advance();

            Assert.False(host.Sheet.IsVisible);
            Assert.Equal(CardHeight, host.CardOffsetY);
        }
        finally
        {
            host.Window.Close();
        }
    }

    // The whole reason IsVisible is this control's own state rather than a
    // binding: it has to lag the ViewModel by one animation, or the sheet
    // vanishes before the slide-out is ever seen.
    [AvaloniaFact]
    public void Closing_stays_on_screen_until_the_slide_finishes()
    {
        var host = Show();
        try
        {
            host.Sheet.IsOpen = true;
            host.Advance();

            host.Sheet.IsOpen = false;
            Assert.True(host.Sheet.IsVisible);

            host.Advance();
            Assert.False(host.Sheet.IsVisible);
            Assert.Equal(Width, host.SheetOffsetX);
        }
        finally
        {
            host.Window.Close();
        }
    }

    // The mirror of that forward exit, on the way back: a sheet restored by
    // Back returns from the edge the push shoved it out of - see
    // SlidingSheet.EntersBackward and MobileNavigationFrame.Sheet, which is
    // what puts a sheet back on screen at all.
    [AvaloniaFact]
    public void A_backward_entrance_arrives_from_the_opposite_edge()
    {
        var host = Show();
        try
        {
            host.Sheet.EntersBackward = true;
            host.Sheet.IsOpen = true;

            Assert.Equal(-Width, host.SheetOffsetX);

            host.Advance();
            Assert.Equal(0, host.SheetOffsetX);
            Assert.True(host.Sheet.IsVisible);
        }
        finally
        {
            host.Window.Close();
        }
    }

    // A dismissal that is itself a forward navigation leaves by the opposite
    // edge, so the sheet and the screen arriving from the right travel together
    // as one push - see SlidingSheet.ExitsForward, and
    // MobileMainViewModel.NowPlayingExitsForward for the one case that sets it.
    [AvaloniaFact]
    public void A_forward_exit_leaves_by_the_opposite_edge()
    {
        var host = Show();
        try
        {
            host.Sheet.IsOpen = true;
            host.Advance();

            host.Sheet.ExitsForward = true;
            host.Sheet.IsOpen = false;
            host.Advance();

            Assert.False(host.Sheet.IsVisible);
            Assert.Equal(-Width, host.SheetOffsetX);
        }
        finally
        {
            host.Window.Close();
        }
    }

    // Reopening mid-close must cancel the close outright - the pending
    // "hide it now" is the one thing that could take a sheet the user just
    // asked for straight back off screen.
    [AvaloniaFact]
    public void Reopening_mid_close_cancels_the_hide()
    {
        var host = Show();
        try
        {
            host.Sheet.IsOpen = true;
            host.Advance();
            host.Sheet.IsOpen = false;

            // Deliberately no Advance between these two: the close is still in
            // flight, which is the case under test.
            host.Sheet.IsOpen = true;
            host.Advance();

            Assert.True(host.Sheet.IsVisible);
            Assert.Equal(0, host.SheetOffsetX);
        }
        finally
        {
            host.Window.Close();
        }
    }

    // Same, for a card sheet - whose close leaves opacity at 0, so a reopen
    // that failed to reset it would show nothing at all.
    [AvaloniaFact]
    public void Reopening_a_card_sheet_after_a_full_close_shows_it_again()
    {
        var host = Show(SheetEntrance.FromBottom, withCard: true);
        try
        {
            host.Sheet.IsOpen = true;
            host.Advance();
            host.Sheet.IsOpen = false;
            host.Advance();

            host.Sheet.IsOpen = true;
            host.Advance();

            Assert.True(host.Sheet.IsVisible);
            Assert.Equal(1, host.Sheet.Opacity);
            Assert.Equal(0, host.CardOffsetY);
        }
        finally
        {
            host.Window.Close();
        }
    }
}
