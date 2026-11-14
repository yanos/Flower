using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Controls;

using Xunit;

namespace Flower.Tests.Controls;

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
// anything is shown. These tests cover that precedence.
// ConnectionStatusIconHoldTests covers the control's busy-state hold.
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
}
