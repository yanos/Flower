using System;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

using Flower.Logging;
using Flower.Services;

using Microsoft.Extensions.Logging;

namespace Flower.Controls;

// Axis-locked trackpad scrolling: a two-finger gesture that starts out mostly
// vertical stays vertical for its whole length, instead of sliding sideways
// whenever the fingers drift off true. Without this, a scroller with both axes
// live (the track list, once its columns are wider than the viewport) wanders
// horizontally during ordinary vertical scrolling, which no native macOS app
// does.
//
// AppKit has exactly this, as NSScrollView.usesPredominantAxisScrolling - on by
// default, which is why every Cocoa app behaves identically. It is a property
// of NSScrollView rather than a reusable function, though, and the heuristic
// itself is private to it, so nothing can call it directly. Avalonia draws into
// a single plain NSView with Skia and has no NSScrollView anywhere in the
// process, so the behaviour has to be reproduced rather than switched on. Apps
// that scroll their own content (Chrome, Electron, Figma) are all in the same
// position and all approximate it slightly differently.
//
// The approximation here is coarser than AppKit's in one specific way. AppKit
// keys its lock off NSEvent.phase/momentumPhase, which say exactly when a
// gesture begins and when the fingers lift. Avalonia's macOS backend reads only
// scrollingDeltaX/Y and hasPreciseScrollingDeltas and drops the phases before
// the managed layer sees anything (confirmed against libAvaloniaNative.dylib's
// own symbols), so PointerWheelChanged arrives as a bare vector with no gesture
// boundaries attached. Gestures are therefore inferred from the gap between
// events instead - see AxisLock.GestureGapMs.
//
// Applies on every platform rather than being fenced off to macOS: Windows
// precision touchpads have the same two-axis drift, and a discrete mouse wheel
// only ever reports one axis, so the lock costs it nothing.
//
// This half is only the plumbing - the decision itself lives in AxisLock, which
// is pure and unit-tested on its own.
public static class PredominantAxisScroll
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsEnabled", typeof(PredominantAxisScroll));

    public static bool GetIsEnabled(Control control) => control.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(Control control, bool value) => control.SetValue(IsEnabledProperty, value);

    // Avalonia's own wheel-to-pixel scale, which this has to match exactly or
    // scrolling would change speed the moment the behaviour is attached: its
    // ScrollContentPresenter moves Offset by -Delta * 50 on both axes, linearly,
    // with no line-count or snap-point step in between. Measured against a real
    // templated ScrollViewer rather than read off the source, since it is an
    // internal detail of the presenter and not exposed anywhere.
    //
    // That measurement holds for a pixel-scrolling ScrollViewer, which is the
    // only kind this is used on. A content panel implementing ILogicalScrollable
    // would take a different path inside the presenter and scroll by lines, and
    // this would then move it at the wrong rate.
    private const double PixelsPerDelta = 50;

    // See RubberBandScroll's identical note: an attached property has no
    // constructor for DI to inject a logger through.
    private static readonly ILogger Logger = AppLogging.CreateLogger(typeof(PredominantAxisScroll).FullName!);

    static PredominantAxisScroll()
    {
        IsEnabledProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.NewValue is true)
                Attach(control);
        });
    }

    private static void Attach(Control control)
    {
        try
        {
            ScrollViewer? scrollViewer = null;
            var axisLock = new AxisLock();

            void TryBindScrollViewer()
            {
                scrollViewer ??= control as ScrollViewer ?? control.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            }

            control.AttachedToVisualTree += (_, _) => TryBindScrollViewer();
            control.Loaded += (_, _) => TryBindScrollViewer();
            if (control is TemplatedControl templated)
                templated.TemplateApplied += (_, _) => TryBindScrollViewer();
            TryBindScrollViewer();

            // Tunnelling, so this runs before the event reaches the
            // ScrollContentPresenter underneath and that presenter's own
            // handling never sees it. Intercepting rather than adjusting is
            // forced: PointerWheelEventArgs.Delta is set at construction and
            // read-only afterwards, so the cross-axis component cannot be
            // trimmed off in passing - the choice is to handle the wheel
            // entirely or not at all.
            control.AddHandler(
                InputElement.PointerWheelChangedEvent,
                (_, e) =>
                {
                    TryBindScrollViewer();
                    if (scrollViewer != null)
                        OnWheel(scrollViewer, axisLock, e);
                },
                RoutingStrategies.Tunnel);
        }
        catch (Exception ex)
        {
            // Scrolling still works untouched if this never attaches - the
            // presenter's own wheel handling is what it falls back to.
            Logger.LogWarning(ex, "Failed to attach predominant-axis scrolling to {Control}", control.GetType().Name);
        }
    }

    private static void OnWheel(ScrollViewer scrollViewer, AxisLock axisLock, PointerWheelEventArgs e)
    {
        try
        {
            // Negated because a wheel delta points the way the content moves,
            // while Offset counts the other way - the same sign convention the
            // presenter uses.
            var requested = new Vector(-e.Delta.X * PixelsPerDelta, -e.Delta.Y * PixelsPerDelta);
            var allowed = axisLock.Allow(requested, Environment.TickCount64);

            var maxX = Math.Max(0, scrollViewer.Extent.Width - scrollViewer.Viewport.Width);
            var maxY = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
            scrollViewer.Offset = new Vector(
                Math.Clamp(scrollViewer.Offset.X + allowed.X, 0, maxX),
                Math.Clamp(scrollViewer.Offset.Y + allowed.Y, 0, maxY));

            // Handled even when the offset didn't actually move (already pinned
            // at a bound), so that a locked-out cross axis stays locked out
            // rather than being handed to the presenter to apply. That rules out
            // scroll chaining to an outer scroller, which is why this belongs on
            // a top-level scroller and not a nested one.
            e.Handled = true;
        }
        catch (Exception ex)
        {
            // Leaving it unhandled falls back to the presenter's own scrolling
            // for this event rather than dropping the user's input entirely.
            Logger.LogWarning(ex, "Predominant-axis scroll failed, falling back to default scrolling");
        }
    }
}

// The axis-lock decision on its own, in pixels, with no Avalonia visual tree
// behind it - one instance per attached scroller, fed every wheel event in
// order. Split out from PredominantAxisScroll (see its comment for why this
// exists at all) because the interesting part is a small state machine over a
// stream of deltas, and that is worth testing directly rather than through a
// headless window.
internal sealed class AxisLock
{
    // How long a lull between wheel events ends the gesture and frees the axis.
    // This is the part standing in for NSEvent.phase: a trackpad delivers events
    // continuously at 60-120Hz while the fingers are down and through the whole
    // momentum tail, so anything longer than a frame or two of silence means the
    // gesture is over. Momentum keeping the lock is correct, not a side effect -
    // a flick that coasts stays on the axis it was thrown along, as it does
    // natively.
    internal const double GestureGapMs = 200;

    // How far a gesture travels before its axis stops being reconsidered.
    //
    // An axis is held from the very first event - there is no opening window in
    // which both axes move. There used to be, on the reasoning that the first
    // event or two are too small and noisy to decide on and a few pixels of
    // drift would be invisible. It was not: a short scroll is *entirely* first
    // events, so it spent all of its life unlocked, and every new flick after a
    // pause re-entered that window. Short scrolls were the ones still sliding
    // sideways.
    //
    // So the early lock is provisional instead. It is re-chosen from the running
    // totals on every event until they pass this distance, which corrects a
    // first-event guess that turns out wrong while never letting the cross axis
    // through in the meantime. Past this point the gesture is committed and only
    // UnlockPressure can move it.
    internal const double DecisionDistance = 8;

    // How much more a gesture has to travel horizontally than vertically before
    // horizontal wins the choice above. Ties, and anything close to one, go to
    // vertical: every scroller this is attached to is a list far taller than it
    // is wide, so vertical is the overwhelmingly more likely intent, and the
    // failure being fixed is one-directional - sideways happening when it was
    // not wanted. A gesture that really is horizontal clears this easily, since
    // a deliberate sideways swipe is not a near-tie.
    //
    // This is the dial to turn if sideways still triggers too easily (raise it)
    // or horizontal scrolling has become awkward to start (lower it, to 1 for a
    // symmetric contest).
    internal const double HorizontalBias = 1.5;

    // How hard the other axis has to push before an already-locked gesture will
    // switch to it. Cross-axis movement is measured against how much the locked
    // axis is moving at the same time and only the excess counts (see Allow), so
    // drifting sideways while still scrolling down never accumulates - only
    // genuinely changing direction does. Deliberately large: the whole point is
    // that sideways movement is hard to trigger by accident, and a gesture that
    // really is horizontal gets there in well under a centimetre of travel.
    internal const double UnlockPressure = 50;

    // Vertical first, so a freshly reset gesture defaults to it before it has
    // seen a single delta.
    internal enum Axis
    {
        Vertical,
        Horizontal,
    }

    internal Axis Locked { get; private set; }

    // Whether the axis above is settled or still being reconsidered from the
    // running totals - see DecisionDistance.
    internal bool Committed { get; private set; }

    // Distance travelled on each axis since the gesture started. Drives the
    // provisional choice, and passing DecisionDistance is what commits it.
    private double _travelX;
    private double _travelY;

    // How much the cross axis has out-moved the locked one so far, floored at
    // zero so the gesture has to change direction rather than merely wobble.
    // Reset on every switch.
    private double _crossPressure;

    private long _lastEventTicks = long.MinValue;

    // Takes the scroll this event asked for and returns the part of it the lock
    // permits - the same vector while undecided, one axis zeroed once committed.
    internal Vector Allow(Vector requested, long nowTicks)
    {
        if (nowTicks - _lastEventTicks > GestureGapMs)
        {
            Locked = Axis.Vertical;
            Committed = false;
            _travelX = 0;
            _travelY = 0;
            _crossPressure = 0;
        }
        _lastEventTicks = nowTicks;

        if (!Committed)
        {
            _travelX += Math.Abs(requested.X);
            _travelY += Math.Abs(requested.Y);

            // Re-chosen every event, from the whole gesture so far rather than
            // this event alone, so one noisy delta cannot swing it. The _travelX
            // > 0 guard keeps a pair of zero deltas from reading as a horizontal
            // gesture through the comparison below.
            Locked = _travelX > 0 && _travelX >= _travelY * HorizontalBias
                ? Axis.Horizontal
                : Axis.Vertical;

            Committed = Math.Max(_travelX, _travelY) >= DecisionDistance;
        }
        else
        {
            var alongLock = Locked == Axis.Vertical ? Math.Abs(requested.Y) : Math.Abs(requested.X);
            var acrossLock = Locked == Axis.Vertical ? Math.Abs(requested.X) : Math.Abs(requested.Y);

            _crossPressure = Math.Max(0, _crossPressure + acrossLock - alongLock);
            if (_crossPressure >= UnlockPressure)
            {
                Locked = Locked == Axis.Vertical ? Axis.Horizontal : Axis.Vertical;
                _crossPressure = 0;
            }
        }

        return Locked == Axis.Vertical
            ? new Vector(0, requested.Y)
            : new Vector(requested.X, 0);
    }
}
