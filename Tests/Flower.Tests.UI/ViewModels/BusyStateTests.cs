using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Flower.Importer;
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
    // A thread of its own, not Task.Run(...).Wait(): Wait may run a task not
    // yet started inline on the waiting thread - here the UI thread - and the
    // code under test would never be off it at all.
    private static void OffTheUiThread(Action action)
    {
        var thread = new Thread(() => action());
        thread.Start();
        thread.Join();
    }

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

    // A scan's count, reported off the UI thread after every file: the UI
    // thread hears the latest once it gets round to it, not every number in
    // between - thousands of them a second off a fast disk.
    [AvaloniaFact]
    public void A_count_reported_off_the_ui_thread_lands_as_its_latest()
    {
        var busy = new BusyState();
        using var scope = busy.BeginScope("Refreshing library");
        var changes = 0;
        busy.Changed += (_, _) => changes++;

        var counting = scope.CountingSongs("Refreshing library");
        OffTheUiThread(() =>
        {
            for (var done = 0; done <= 500; done++)
                counting.Report(new SongProgress(done, 500));
        });
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Refreshing library: 500 of 500 songs", busy.Message);
        Assert.Equal(1, changes);
    }

    // Nothing to count says nothing, rather than "0 of 0 songs".
    [AvaloniaFact]
    public void A_count_of_no_songs_leaves_the_message_alone()
    {
        var busy = new BusyState();
        using var scope = busy.BeginScope("Refreshing library");

        scope.CountingSongs("Refreshing library").Report(new SongProgress(0, 0));

        Assert.Equal("Refreshing library", busy.Message);
    }

    // A rescan reports its last count from the scanning thread and closes its
    // scope back on the UI thread, which can be before that count's post has
    // run. Landing after the close, it must not put a message back up with
    // nothing left to take it down.
    [AvaloniaFact]
    public void A_count_landing_after_its_scope_closed_says_nothing()
    {
        var busy = new BusyState();
        var scope = busy.BeginScope("Refreshing library");

        OffTheUiThread(() => scope.Say("Refreshing library: 3 of 3 songs"));
        scope.Dispose();
        Dispatcher.UIThread.RunJobs();

        Assert.False(busy.IsBusy);
        Assert.Null(busy.Message);
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
