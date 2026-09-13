using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Headless.XUnit;

using Flower.Controls;

using Xunit;

namespace Flower.Tests;

using State = ConnectionStatusIcon.ConnectionState;

// One paired server's standing is one glyph, and these are what say so.
//
// The four places this is shown - the sidebar row, the device pane's header,
// desktop's server picker, mobile's settings sheet - had each assembled it by
// hand, and three of them put the check and the spinner in the same StackPanel
// with independent IsVisible bindings. So a server that was syncing, or paired
// and waiting to be approved, showed a lit green check with a spinner turning
// beside it: connected and connecting at once, which is not a state a server
// can be in.
//
// Independent visibility flags are what allowed it, so the fix is not to bind
// them more carefully at each site - it is to have one state resolved before
// anything is shown. What follows holds both halves of that: the precedence
// itself, and that the control really does show at most one icon.
public class ConnectionStatusIconTests
{
    // The defect, as a rule. Busy leads because it is the only one of the three
    // that is about right now; the others describe what was last found true.
    [Fact]
    public void Busy_replaces_connected_rather_than_joining_it()
    {
        Assert.Equal(State.Busy, ConnectionStatusIcon.Resolve(connected: true, busy: true, unreachable: false));
    }

    [Fact]
    public void Busy_also_leads_a_server_last_seen_unreachable()
    {
        Assert.Equal(State.Busy, ConnectionStatusIcon.Resolve(connected: false, busy: true, unreachable: true));
    }

    [Fact]
    public void An_unreachable_server_is_not_reported_as_connected()
    {
        Assert.Equal(State.Unreachable, ConnectionStatusIcon.Resolve(connected: true, busy: false, unreachable: true));
    }

    [Fact]
    public void A_settled_connection_reads_as_connected()
    {
        Assert.Equal(State.Connected, ConnectionStatusIcon.Resolve(connected: true, busy: false, unreachable: false));
    }

    [Fact]
    public void A_row_with_nothing_to_report_has_no_state()
    {
        Assert.Equal(State.None, ConnectionStatusIcon.Resolve(connected: false, busy: false, unreachable: false));
    }

    // The rule above is only worth having if the control obeys it. This is the
    // original defect in the visual tree: exactly one icon, never two.
    [AvaloniaFact]
    public void A_syncing_connected_server_shows_the_spinner_and_not_the_check()
    {
        var icon = new ConnectionStatusIcon { IsConnected = true, IsBusy = true };

        Assert.Equal(1, VisibleIcons(icon));
        Assert.Equal(State.Busy, icon.State);
        Assert.True(icon.IsVisible);
    }

    [AvaloniaFact]
    public void No_combination_of_inputs_shows_two_icons_at_once()
    {
        foreach (var connected in new[] { false, true })
        foreach (var busy in new[] { false, true })
        foreach (var unreachable in new[] { false, true })
        {
            var icon = new ConnectionStatusIcon
            {
                IsConnected = connected,
                IsBusy = busy,
                IsUnreachable = unreachable,
            };

            Assert.True(VisibleIcons(icon) <= 1);
        }
    }

    // Nothing to report takes no room, rather than leaving a gap where the
    // glyph would be - the sidebar shows this control on every Device row, and
    // only the paired server ever has anything to put in it.
    [AvaloniaFact]
    public void A_row_with_nothing_to_report_collapses_entirely()
    {
        var icon = new ConnectionStatusIcon();

        Assert.False(icon.IsVisible);
        Assert.Equal(0, VisibleIcons(icon));
    }

    // All three are sized together, which is what makes a state change a swap
    // rather than a nudge of everything to the right of it.
    [AvaloniaFact]
    public void Every_state_is_drawn_at_the_same_size()
    {
        var icon = new ConnectionStatusIcon { IconSize = 18 };

        foreach (var child in IconsOf(icon))
        {
            Assert.Equal(18, child.Width);
            Assert.Equal(18, child.Height);
        }
    }

    // A sync of a library nothing has changed is over in milliseconds, so the
    // spinner it raises would otherwise appear and vanish inside a frame or
    // two. That does not read as work happening; it reads as the display
    // glitching. The state is held for a minimum length instead.
    [AvaloniaFact]
    public void A_sync_shorter_than_the_hold_still_shows_its_spinner()
    {
        var releases = new List<Action>();
        var icon = new ConnectionStatusIcon
        {
            IsConnected = true,
            ScheduleBusyRelease = (_, release) => releases.Add(release),
        };

        icon.IsBusy = true;
        icon.IsBusy = false;

        Assert.Equal(State.Busy, icon.State);

        releases.Single()();

        Assert.Equal(State.Connected, icon.State);
    }

    // The hold is a floor, not a ceiling: a sync still running when it expires
    // keeps the spinner, because IsBusy is still true.
    [AvaloniaFact]
    public void A_sync_still_running_when_the_hold_expires_keeps_its_spinner()
    {
        var releases = new List<Action>();
        var icon = new ConnectionStatusIcon
        {
            IsConnected = true,
            ScheduleBusyRelease = (_, release) => releases.Add(release),
        };

        icon.IsBusy = true;
        releases.Single()();

        Assert.Equal(State.Busy, icon.State);
    }

    // Two syncs in quick succession are two holds. The first one's release is
    // still pending when the second begins, and must not end it early -
    // periodic syncs land close enough together for this to be the ordinary
    // case rather than a corner of one.
    [AvaloniaFact]
    public void A_second_sync_is_not_cut_short_by_the_first_holds_release()
    {
        var releases = new List<Action>();
        var icon = new ConnectionStatusIcon
        {
            IsConnected = true,
            ScheduleBusyRelease = (_, release) => releases.Add(release),
        };

        icon.IsBusy = true;
        icon.IsBusy = false;
        icon.IsBusy = true;
        icon.IsBusy = false;

        releases[0]();

        Assert.Equal(State.Busy, icon.State);

        releases[1]();

        Assert.Equal(State.Connected, icon.State);
    }

    // The other three drive the hold through an injected scheduler, which
    // proves the rule but not the wiring. This one uses the control's own
    // timer, on a control that is actually on screen - which is also the only
    // condition under which it starts one.
    //
    // It waits by awaiting rather than by blocking the dispatcher thread and
    // pumping it by hand, and the difference is not stylistic. A headless
    // DispatcherTimer only ticks once the dispatcher thread goes idle: the tick
    // is not sitting in the queue waiting to be drained, so a loop that blocks
    // that thread and calls RunJobs never finds one. Measured here, against
    // this suite's shared session, with a 50ms timer and three seconds to catch
    // it:
    //
    //   Thread.Sleep alone            never fired - nothing drains the queue
    //   RunJobs alone                 never fired, over 95 million spins
    //   Thread.Sleep + RunJobs        fired at 54ms, on the first pass
    //   await Task.Delay              fired at 56ms, on the fifth pass
    //
    // The third is what this test used to do, and it is the one that is a
    // coincidence: the sleep happens to leave the thread idle long enough for
    // the tick to be serviced, and the RunJobs that follows happens to find it.
    // That held on macOS and Linux and failed on a Windows CI runner, where the
    // hold was still unreleased five seconds and ~500 passes later. Awaiting
    // returns the thread to the session's own loop between passes, which is the
    // condition the timer actually needs - and is how the control's timer runs
    // in the app, where nothing pumps the dispatcher by hand either.
    [AvaloniaFact]
    public async Task An_on_screen_icon_releases_its_own_hold()
    {
        var icon = new ConnectionStatusIcon
        {
            IsConnected = true,
            MinimumBusyDuration = TimeSpan.FromMilliseconds(50),
        };
        var window = new Window { Content = icon };
        window.Show();

        icon.IsBusy = true;
        icon.IsBusy = false;

        Assert.Equal(State.Busy, icon.State);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (icon.State == State.Busy && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.Equal(State.Connected, icon.State);
        window.Close();
    }

    private static IEnumerable<Control> IconsOf(ConnectionStatusIcon icon) =>
        ((Grid)icon.Content!).Children.Cast<Control>();

    private static int VisibleIcons(ConnectionStatusIcon icon) =>
        IconsOf(icon).Count(child => child.IsVisible);
}
