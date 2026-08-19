using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia.Threading;

namespace Flower.Services;

// One 60 Hz dispatcher timer for the whole app.
//
// Four separate animations each ran their own 16ms DispatcherTimer: the status
// bar's busy spinner, the swipe/rubber-band easings, and - worst of the four -
// one *per downloading row*, so batch-downloading an album on a phone woke the
// dispatcher 60 times a second per track. Each of those wakeups is a full
// dispatcher loop iteration and a layout/render pass regardless of how little
// it changed, which is exactly the wrong shape on a battery-powered device.
// They now share this one timer, which runs only while something is actually
// subscribed. See docs/ARCHITECTURE-REVIEW.md Tier 1.5.
//
// Each subscriber is handed the time elapsed since *its own* subscription
// rather than a raw tick, because that is what both kinds of consumer want: a
// spinner derives its angle from elapsed time (so a dropped frame no longer
// makes it visibly lag, the way a fixed +6 degrees per tick did), and a finite
// easing derives its progress from elapsed/duration and unsubscribes at 1.
// Every subscriber in a frame is measured off one clock reading, so animations
// that start together stay together.
//
// UI thread only - the underlying DispatcherTimer requires it, and every
// subscriber writes to bound properties from the callback.
public sealed class AnimationClock
{
    public static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(16);

    // Set once from the composition root in production; the default instance is
    // here so a view or view-model constructed outside the container (the XAML
    // previewer, a test) still animates rather than null-referencing.
    public static AnimationClock Current { get; set; } = new();

    private readonly List<Subscription> _subscribers = new();
    private readonly Func<TimeSpan> _now;
    private readonly bool _useTimer;
    private DispatcherTimer? _timer;
    private TimeSpan _lastTick;
    private bool _isRunning;

    // A running clock ticks every 16ms, so a gap this long means the timer is
    // not actually attached to a live dispatcher anymore - see Subscribe.
    private static readonly TimeSpan StaleTimerThreshold = TimeSpan.FromSeconds(1);

    // Guards against a subscriber unsubscribing (or subscribing) from inside
    // its own callback, which the easings do on their final frame: the list
    // must not be mutated while it is being walked.
    private Subscription[] _tickBuffer = Array.Empty<Subscription>();

    public AnimationClock()
    {
        var elapsed = Stopwatch.StartNew();
        _now = () => elapsed.Elapsed;
        _useTimer = true;
    }

    // Drives the clock from TickForTest against a caller-controlled time
    // instead of a real dispatcher timer, so a test can advance frames
    // deterministically rather than sleeping for real ones.
    internal AnimationClock(Func<TimeSpan> now)
    {
        _now = now;
        _useTimer = false;
    }

    public int SubscriberCount => _subscribers.Count;

    public bool IsRunning => _isRunning;

    // The returned handle stops this animation when disposed. Disposing twice
    // is safe; so is disposing from inside the callback.
    public IDisposable Subscribe(Action<TimeSpan> onTick)
    {
        var subscription = new Subscription(this, onTick, _now());
        _subscribers.Add(subscription);

        // A fresh timer each time the clock goes from idle to running, rather
        // than one cached for the process: a DispatcherTimer belongs to the
        // Dispatcher it was created on, and the headless test platform stands
        // up a new one per session while this static instance outlives all of
        // them - a cached timer then silently never ticks again.
        //
        // The staleness check covers the case where the count never reached
        // zero to trigger that: one animation left running (a row abandoned
        // mid-download, say) would otherwise pin a dead timer in place and
        // take every later animation down with it.
        if (_useTimer && _timer != null && _now() - _lastTick > StaleTimerThreshold)
            StopTimer();

        _isRunning = true;
        if (_useTimer && _timer == null)
        {
            _lastTick = _now();
            _timer = new DispatcherTimer { Interval = FrameInterval };
            _timer.Tick += (_, _) => Tick();
            _timer.Start();
        }

        return subscription;
    }

    // Stops the timer once the last animation ends - the point of the whole
    // class is that an idle app has no 60 Hz wakeup at all, not that it has
    // exactly one.
    private void Remove(Subscription subscription)
    {
        if (!_subscribers.Remove(subscription))
            return;
        if (_subscribers.Count == 0)
        {
            _isRunning = false;
            StopTimer();
        }
    }

    private void StopTimer()
    {
        _timer?.Stop();
        _timer = null;
    }

    internal void TickForTest() => Tick();

    private void Tick()
    {
        var now = _now();
        _lastTick = now;

        if (_tickBuffer.Length < _subscribers.Count)
            _tickBuffer = new Subscription[Math.Max(4, _subscribers.Count * 2)];
        _subscribers.CopyTo(_tickBuffer);
        var count = _subscribers.Count;

        for (var i = 0; i < count; i++)
        {
            var subscription = _tickBuffer[i];
            _tickBuffer[i] = null!;
            if (subscription.IsActive)
                subscription.OnTick(now - subscription.Start);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly AnimationClock _clock;

        public Subscription(AnimationClock clock, Action<TimeSpan> onTick, TimeSpan start)
        {
            _clock  = clock;
            OnTick  = onTick;
            Start   = start;
        }

        public Action<TimeSpan> OnTick { get; }
        public TimeSpan Start { get; }
        public bool IsActive { get; private set; } = true;

        public void Dispose()
        {
            if (!IsActive)
                return;
            IsActive = false;
            _clock.Remove(this);
        }
    }
}
