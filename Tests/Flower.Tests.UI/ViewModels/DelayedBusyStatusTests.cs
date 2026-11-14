using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia.Headless.XUnit;

using Flower.ViewModels;

using Xunit;

namespace Flower.Tests.ViewModels;

// The phone's status pill: up only for work that goes on a while, and kept a
// moment after it ends so its last word can be read.
public class DelayedBusyStatusTests
{
    // Timers run by hand: Elapse runs whatever is still scheduled.
    private sealed class HandTimers
    {
        private readonly List<Timer> _timers = [];

        public IDisposable Schedule(TimeSpan delay, Action action)
        {
            var timer = new Timer(action);
            _timers.Add(timer);
            return timer;
        }

        public void Elapse()
        {
            foreach (var timer in _timers.ToList())
            {
                _timers.Remove(timer);
                if (!timer.Cancelled)
                    timer.Action();
            }
        }

        public sealed class Timer(Action action) : IDisposable
        {
            public Action Action { get; } = action;
            public bool Cancelled { get; private set; }
            public void Dispose() => Cancelled = true;
        }
    }

    [AvaloniaFact]
    public void Work_that_ends_before_the_delay_never_shows()
    {
        var busy = new BusyState();
        var timers = new HandTimers();
        using var status = new DelayedBusyStatus(busy, timers.Schedule);

        busy.BeginScope("Refreshing library").Dispose();
        timers.Elapse();

        Assert.False(status.IsShown);
    }

    [AvaloniaFact]
    public void Work_that_goes_on_shows_and_says_its_latest()
    {
        var busy = new BusyState();
        var timers = new HandTimers();
        using var status = new DelayedBusyStatus(busy, timers.Schedule);

        using var scope = busy.BeginScope("Refreshing library");
        timers.Elapse();
        scope.Say("Refreshing library: 120 of 300 songs");

        Assert.True(status.IsShown);
        Assert.Equal("Refreshing library: 120 of 300 songs", status.Text);
    }

    // Down a moment after the work ends, not at once - and still saying its
    // last word meanwhile, though BusyState's own message has gone to null.
    [AvaloniaFact]
    public void Ended_work_lingers_with_its_last_word_then_goes()
    {
        var busy = new BusyState();
        var timers = new HandTimers();
        using var status = new DelayedBusyStatus(busy, timers.Schedule);
        var scope = busy.BeginScope("Downloading: 3 of 3 songs");
        timers.Elapse();

        scope.Dispose();

        Assert.True(status.IsShown);
        Assert.Equal("Downloading: 3 of 3 songs", status.Text);
        timers.Elapse();
        Assert.False(status.IsShown);
    }

    // A download right after a scan: one pill throughout, not one going down
    // as the next comes up.
    [AvaloniaFact]
    public void Work_starting_again_while_it_lingers_keeps_it_up()
    {
        var busy = new BusyState();
        var timers = new HandTimers();
        using var status = new DelayedBusyStatus(busy, timers.Schedule);
        var scan = busy.BeginScope("Refreshing library");
        timers.Elapse();
        scan.Dispose();

        using var download = busy.BeginScope("Downloading 3 songs…");
        timers.Elapse();

        Assert.True(status.IsShown);
        Assert.Equal("Downloading 3 songs…", status.Text);
    }
}
