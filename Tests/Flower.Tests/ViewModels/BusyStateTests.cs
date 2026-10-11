using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;

using Flower.Models;
using Flower.Services;
using Flower.ViewModels;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Flower.Tests.ViewModels;

// One of the collaborators split out of MainViewModel (docs/ARCHITECTURE-
// REVIEW.md Tier 4.2), constructed directly rather than through that class's
// constructor and the whole service graph behind it.
public class BusyStateTests
{
    [AvaloniaFact]
    public void Nested_scopes_keep_the_indicator_up_until_the_outermost_closes()
    {
        var busy = new BusyState();
        var changes = 0;
        busy.Changed += (_, _) => changes++;

        Assert.False(busy.IsBusy);

        var outer = busy.BeginScope("outer");
        Assert.True(busy.IsBusy);
        Assert.Equal("outer", busy.Message);

        var inner = busy.BeginScope("inner");
        // The innermost scope wins the message while both are open.
        Assert.Equal("inner", busy.Message);

        inner.Dispose();
        // Still busy - and deliberately still showing the inner message, since
        // nothing re-states the outer one on the way out.
        Assert.True(busy.IsBusy);

        outer.Dispose();
        Assert.False(busy.IsBusy);
        Assert.Null(busy.Message);
        // Two opens and the final close - closing the inner scope changes
        // neither IsBusy nor Message, so it raises nothing.
        Assert.Equal(3, changes);
    }

    [AvaloniaFact]
    public void A_scope_opened_off_the_ui_thread_still_reports_busy_immediately()
    {
        var busy = new BusyState();

        // The count is bumped synchronously regardless of thread - only the
        // notification is marshalled. This is the invariant that made
        // overlapping scopes from App.axaml.cs's background rescan work.
        Task.Run(() => busy.BeginScope("background")).Wait();

        Assert.True(busy.IsBusy);
    }
}
