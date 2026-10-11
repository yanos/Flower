using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Flower.Services;

// One clock for every animation the app drives from code, ticking once per
// frame the display actually shows.
//
// Four separate animations each ran their own 16ms DispatcherTimer: the status
// bar's busy spinner, the swipe/rubber-band easings, and - worst of the four -
// one *per downloading row*, so batch-downloading an album on a phone woke the
// dispatcher 60 times a second per track. Each of those wakeups is a full
// dispatcher loop iteration and a layout/render pass regardless of how little
// it changed, which is exactly the wrong shape on a battery-powered device.
// They now share this one clock, which runs only while something is actually
// subscribed. See docs/ARCHITECTURE-REVIEW.md Tier 1.5.
//
// The ticks come from Avalonia's own frame callback (TopLevel.RequestAnimationFrame)
// wherever there is a TopLevel to ask (TopLevelProvider, set by the composition
// root). It used to be a 16ms DispatcherTimer, which was two things wrong on a
// 120Hz phone: half the frames had nothing new to show, and a timer that does
// not know when frames are drawn lands at 62.5Hz against a 120Hz display, so the
// distance moved between one shown frame and the next alternated - an easing
// that judders. A frame callback runs at the start of the frame it changes,
// before layout, at whatever rate the display is running. The timer stays as
// the fallback for a clock with no TopLevel behind it (the XAML previewer, a
// test that never asked for one).
//
// Each subscriber is handed the time elapsed since *its own* subscription
// rather than a raw tick, because that is what both kinds of consumer want: a
// spinner derives its angle from elapsed time (so a dropped frame no longer
// makes it visibly lag, the way a fixed +6 degrees per tick did), and a finite
// easing derives its progress from elapsed/duration and unsubscribes at 1.
// Every subscriber in a frame is measured off one clock reading, so animations
// that start together stay together.
//
// UI thread only - frame callbacks and the DispatcherTimer both arrive there,
// and every subscriber writes to bound properties from the callback.
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

    // Whether a frame callback is outstanding - so a subscription arriving
    // while one is already asked for does not ask again and tick twice a frame -
    // and which request is the live one. A request abandoned as stale (see
    // Subscribe) can still be answered later, and must then do nothing, or two
    // chains of requests would each tick every frame.
    private bool _frameRequested;
    private int _frameGeneration;

    // A running clock ticks every frame, so a gap this long means its timer or
    // its frame request is not attached to a live dispatcher anymore - see
    // Subscribe.
    private static readonly TimeSpan StaleTimerThreshold = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Where to ask for frames: any live TopLevel will do, since every one of
    /// them forwards to the same per-process frame clock. Null, or returning
    /// null, falls back to a 16ms timer.
    /// </summary>
    public Func<TopLevel?>? TopLevelProvider { get; set; }

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

    // Whether the clock is on its fallback timer rather than on frames.
    internal bool HasTimer => _timer != null;

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
        if (_useTimer && (_timer != null || _frameRequested) && _now() - _lastTick > StaleTimerThreshold)
        {
            StopTimer();
            _frameRequested = false;
            _frameGeneration++;
        }

        _isRunning = true;
        if (_useTimer && _timer == null && !_frameRequested)
        {
            _lastTick = _now();
            if (TopLevelProvider?.Invoke() is { } topLevel)
                RequestFrame(topLevel);
            else
                StartTimer();
        }

        return subscription;
    }

    // Stops the timer once the last animation ends - the point of the whole
    // class is that an idle app has no per-frame wakeup at all, not that it has
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

    private void StartTimer()
    {
        _timer = new DispatcherTimer { Interval = FrameInterval };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private void RequestFrame(TopLevel topLevel)
    {
        _frameRequested = true;
        var generation = ++_frameGeneration;
        topLevel.RequestAnimationFrame(_ =>
        {
            if (generation == _frameGeneration)
                OnFrame(topLevel);
        });
    }

    // Asks for the next frame from inside this one for as long as anything is
    // subscribed; the request lapses on the first frame that finds the clock
    // idle, which is how it stops.
    private void OnFrame(TopLevel topLevel)
    {
        _frameRequested = false;
        if (!_isRunning)
            return;
        Tick();
        if (_isRunning && !_frameRequested && _timer == null)
            RequestFrame(topLevel);
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
