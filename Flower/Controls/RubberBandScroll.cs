using System;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Flower.Services;

using Microsoft.Extensions.Logging;

using Flower.Logging;

namespace Flower.Controls;

// Adds iOS/Android-style rubber-band overscroll feedback to a scrollable
// mobile control - a ScrollViewer directly, or a ListBox/ItemsControl with
// one in its template (see IsEnabled, applied throughout MobileMainView.axaml).
// Desktop has no equivalent usage - this is a touch-drag convention mouse+
// scrollbar users don't expect.
//
// Avalonia's own ScrollViewer has no built-in equivalent (confirmed:
// https://github.com/AvaloniaUI/Avalonia/issues/18648, open and unimplemented
// as of writing). Its ScrollGestureRecognizer does, however, raise a live,
// in-progress InputElement.ScrollGestureEvent for every increment of a touch-drag
// scroll (Delta), not just InputElement.ScrollGestureEndedEvent once it finishes -
// this tracks its own "logical" offset from those live deltas, independent of
// ScrollViewer.Offset itself (which silently clamps to [0, Extent-Viewport],
// giving no residual amount to react to once already pinned at a bound), and
// once that logical offset would go negative or past the max, applies the
// excess - damped, so it gets harder to pull further, not linear - straight
// to RenderTransform.Y live, so the content visibly drags along with the
// finger while still touching, springing back only once the gesture actually
// ends (InputElement.ScrollGestureEndedEvent).
//
// That covers a scroll that runs into a bound, but not the commonest pull of
// all: a finger landing on a list already at its top and dragging down. Avalonia
// 12's ScrollGestureRecognizer refuses to start a gesture in a direction the
// content cannot scroll (PointerMoved returns early while Offset.Y is 0 and
// the drag is downward), so that pull raises no ScrollGestureEvent at all -
// the list sat dead under the finger, and the filter box it is meant to reveal
// (PulledDownEvent) could never be reached from the top, which is the only
// place it lives. So that one case is tracked here from the pointer directly
// (OnPointerPressed/OnPointerMoved): once a drag from the top is clearly
// downward, the pointer is captured - which also takes it away from whatever
// row it landed on, so letting go does not tap it - and the same damped
// transform follows the finger.
//
// The spring-back (and the live drag itself) is driven by the shared 60Hz
// AnimationClock, stepping RenderTransform.Y frame by frame (the same
// manual-tween approach MainView.axaml.cs's status-bar spinner uses), NOT
// Avalonia's Animation/
// RunAsync machinery: running an Animation whose keyframes target
// TranslateTransform.YProperty routes through TransformAnimator, which
// expects a TransformOperations-shaped RenderTransform and throws
// InvalidCastException against a plain TranslateTransform (confirmed on real
// iOS hardware).
public static class RubberBandScroll
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsEnabled", typeof(RubberBandScroll));

    public static bool GetIsEnabled(Control control) => control.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(Control control, bool value) => control.SetValue(IsEnabledProperty, value);

    // Raised on the scrolling control when a finger lets go of a list it had
    // pulled down past its top by at least PullDownThreshold - the gesture a
    // phone uses to reveal a list's search box. Bubbles, so the ScreenSlot
    // around the pulled screen hears it and opens that screen's filter oval.
    //
    // Only the finger's own pull counts, not the bounce a fling carries up
    // past the top on its own: a flick back to the top of a long list is not
    // a request for anything, and would otherwise open the box every time.
    public static readonly RoutedEvent<RoutedEventArgs> PulledDownEvent =
        RoutedEvent.Register<RoutedEventArgs>("PulledDown", RoutingStrategies.Bubble, typeof(RubberBandScroll));

    // Raised on the scrolling control as a finger's pull down past the top
    // stretches the content, and again as that stretch springs back: how far
    // it is towards a release that would raise PulledDownEvent, 0 to 1. The
    // ScreenSlot around it fades the filter oval in by it, so the pull shows
    // what it is about to open, and letting go early fades it out again.
    // Measured on the stretch rather than the finger, so the spring back
    // carries it down smoothly - and never raised for the bounce a fling
    // carries past the top on its own, for PulledDownEvent's reason.
    public static readonly RoutedEvent<PullingDownEventArgs> PullingDownEvent =
        RoutedEvent.Register<PullingDownEventArgs>("PullingDown", RoutingStrategies.Bubble, typeof(RubberBandScroll));

    // In raw finger travel past the top, before damping - about the distance
    // the content visibly stretches by the time it starts to resist in earnest.
    private const double PullDownThreshold = 60;

    // How far a finger at the top travels before its drag is taken for a pull,
    // and how much more downward than sideways it has to be - a sideways
    // swipe belongs to ScreenStackPanel, which decides at 18px, and taking
    // the pointer first would steal it.
    private const double PullStartDistance = 10;
    private const double PullDirectionRatio = 1.5;

    // How much of each pixel of overscroll actually reaches the transform -
    // resistance that grows sharper the further past the bound you pull
    // (see ApplyOverscroll), matching the "gets harder to pull, snaps back
    // eagerly" feel of iOS's own UIScrollView bounce rather than a loose,
    // linear one. MaxOverscroll caps how far a determined pull can stretch
    // the content off-screen regardless of how much further the finger goes.
    private const double Resistance = 0.4;
    private const double MaxOverscroll = 90;

    // A plain static utility hooked up via an attached XAML property has no
    // constructor for DI to inject into (see the project's own general
    // preference for constructor-injected loggers) - this is the sanctioned
    // fallback for exactly that case.
    private static readonly ILogger Logger = AppLogging.CreateLogger(typeof(RubberBandScroll).FullName!);

    static RubberBandScroll()
    {
        IsEnabledProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.NewValue is true)
                Attach(control);
        });
    }

    private sealed class State(Control owner)
    {
        public readonly Control Owner = owner;
        public ScrollViewer? ScrollViewer;
        public readonly TranslateTransform Transform = new();
        public IDisposable? Animation;

        // The offset the current gesture would have reached if ScrollViewer
        // didn't clamp it - null between gestures. Re-seeded from the real,
        // clamped Offset at the start of each gesture (see OnScrollGesture),
        // so this only ever needs to track the delta from there, not
        // reconstruct the whole scroll history.
        public double? LogicalOffset;

        // How far past the top the finger itself has pulled during this
        // gesture, and whether the gesture has turned into a fling - after
        // which nothing it does counts towards PulledDownEvent.
        public double PeakPull;
        public bool IsGliding;

        // Whether the stretch the content is in came from a finger pulling it
        // down, and so is reported through PullingDownEvent - and what was
        // last reported, so a still frame raises nothing.
        public bool StretchIsPull;
        public double ReportedProgress;

        // A finger that landed on the list, while it might still turn into a
        // pull from the top, and where it landed. IsPulling once it has.
        public IPointer? PullPointer;
        public Point PullStart;
        public bool IsPulling;
    }

    private static void Attach(Control control)
    {
        try
        {
            var state = new State(control);
            control.RenderTransform = state.Transform;

            void TryBindScrollViewer()
            {
                state.ScrollViewer ??= control as ScrollViewer ?? control.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            }

            // The ScrollViewer isn't necessarily realized yet for a templated
            // control (ListBox) at the point IsEnabled is first set - retry on
            // every plausible "the template's actually built now" signal rather
            // than relying on just one, since a control seeing none of these
            // fire before its first real drag would silently never bounce at all.
            control.AttachedToVisualTree += (_, _) => TryBindScrollViewer();
            control.Loaded += (_, _) => TryBindScrollViewer();
            if (control is TemplatedControl templated)
                templated.TemplateApplied += (_, _) => TryBindScrollViewer();
            TryBindScrollViewer();

            control.AddHandler(InputElement.ScrollGestureEvent, (_, e) => OnScrollGesture(state, e));
            control.AddHandler(InputElement.ScrollGestureEndedEvent, (_, _) => OnScrollGestureEnded(state));
            control.AddHandler(InputElement.ScrollGestureInertiaStartingEvent, (_, _) => state.IsGliding = true,
                RoutingStrategies.Bubble, handledEventsToo: true);

            // Tunnel and handledEventsToo: a row's Button handles its own
            // press, and the pull has to be seen whatever it landed on.
            control.AddHandler(InputElement.PointerPressedEvent, (_, e) => OnPointerPressed(state, e),
                RoutingStrategies.Tunnel, handledEventsToo: true);
            control.AddHandler(InputElement.PointerMovedEvent, (_, e) => OnPointerMoved(state, e),
                RoutingStrategies.Tunnel, handledEventsToo: true);
            control.AddHandler(InputElement.PointerReleasedEvent, (_, e) => EndPull(state, e.Pointer),
                RoutingStrategies.Tunnel, handledEventsToo: true);
            control.AddHandler(InputElement.PointerCaptureLostEvent, (_, e) => EndPull(state, e.Pointer),
                RoutingStrategies.Tunnel, handledEventsToo: true);
        }
        catch (Exception ex)
        {
            // Never let a cosmetic bounce effect take the whole app down over
            // it - see OnScrollGesture's identical reasoning.
            Logger.LogWarning(ex, "Failed to attach rubber-band scroll behavior to {Control}", control.GetType().Name);
        }
    }

    private static void OnScrollGesture(State state, ScrollGestureEventArgs e)
    {
        try
        {
            if (state.ScrollViewer == null)
                return;

            var sv = state.ScrollViewer;
            var max = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);

            // First tick of a new gesture - start tracking from wherever the
            // real, already-clamped offset currently is.
            state.LogicalOffset ??= sv.Offset.Y;
            state.LogicalOffset += e.Delta.Y;

            if (state.LogicalOffset < 0)
            {
                if (!state.IsGliding)
                    state.PeakPull = Math.Max(state.PeakPull, -state.LogicalOffset.Value);
                state.StretchIsPull = !state.IsGliding;
                state.Animation?.Dispose();
                state.Animation = null;
                SetStretch(state, Damp(-state.LogicalOffset.Value));
            }
            else if (state.LogicalOffset > max)
            {
                state.StretchIsPull = false;
                state.Animation?.Dispose();
                state.Animation = null;
                SetStretch(state, -Damp(state.LogicalOffset.Value - max));
            }
            else if (state.Transform.Y != 0)
            {
                SetStretch(state, 0);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Rubber-band live drag failed, skipping");
        }
    }

    // Touch and pen only, the two the scroll recognizer itself answers to - a
    // mouse drag on a list is a selection, not a scroll.
    private static void OnPointerPressed(State state, PointerPressedEventArgs e)
    {
        state.PullPointer = e.Pointer.Type is PointerType.Touch or PointerType.Pen ? e.Pointer : null;
        state.PullStart = e.GetPosition(state.Owner);
        state.IsPulling = false;
    }

    private static void OnPointerMoved(State state, PointerEventArgs e)
    {
        try
        {
            // A drag the recognizer did start is a scroll, and OnScrollGesture
            // already stretches it past whichever bound it reaches.
            if (e.Pointer != state.PullPointer || state.LogicalOffset != null || state.ScrollViewer is not { } sv)
                return;

            var position = e.GetPosition(state.Owner);
            var dx = position.X - state.PullStart.X;
            var dy = position.Y - state.PullStart.Y;

            if (!state.IsPulling)
            {
                if (Math.Abs(dx) < PullStartDistance && Math.Abs(dy) < PullStartDistance)
                    return;
                // Anything but a clearly downward drag from the very top is
                // someone else's - an ordinary scroll, or a sideways swipe.
                if (sv.Offset.Y > 0.5 || dy < PullStartDistance || dy < Math.Abs(dx) * PullDirectionRatio)
                {
                    state.PullPointer = null;
                    return;
                }

                state.IsPulling = true;
                state.StretchIsPull = true;
                state.PeakPull = 0;
                state.Animation?.Dispose();
                state.Animation = null;
                // Measured from here rather than from where the finger landed,
                // so the content does not jump by the distance it took to be
                // sure this was a pull.
                state.PullStart = position;
                e.Pointer.Capture(state.Owner);
                return;
            }

            var pull = Math.Max(0, dy);
            state.PeakPull = Math.Max(state.PeakPull, pull);
            SetStretch(state, Damp(pull));
            e.Handled = true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Rubber-band pull from the top failed, skipping");
        }
    }

    // The finger letting go (or losing the pointer some other way) ends a
    // pull the same way the end of a scroll gesture ends an overscroll.
    private static void EndPull(State state, IPointer pointer)
    {
        if (pointer != state.PullPointer)
            return;
        var wasPulling = state.IsPulling;
        state.PullPointer = null;
        state.IsPulling = false;
        if (!wasPulling)
            return;
        if (pointer.Captured == state.Owner)
            pointer.Capture(null);
        OnScrollGestureEnded(state);
    }

    // Diminishing returns the further past the bound the finger already is -
    // sqrt rather than a flat multiplier so the first few pixels of pull
    // still feel responsive while a long, determined drag tapers off well
    // short of MaxOverscroll instead of reaching it abruptly.
    private static double Damp(double rawOverscroll) =>
        Math.Min(MaxOverscroll, Math.Sqrt(rawOverscroll) * Resistance * 10);

    // Every change to the stretch goes through here, so PullingDownEvent
    // follows it exactly - the live drag and the spring back alike. Full at
    // the stretch a PullDownThreshold pull reaches: all the way in is the
    // point at which letting go opens it.
    private static void SetStretch(State state, double y)
    {
        state.Transform.Y = y;
        var progress = state.StretchIsPull ? Math.Clamp(y / Damp(PullDownThreshold), 0, 1) : 0;
        if (progress == state.ReportedProgress)
            return;
        state.ReportedProgress = progress;
        state.Owner.RaiseEvent(new PullingDownEventArgs(progress));
    }

    private static void OnScrollGestureEnded(State state)
    {
        try
        {
            state.LogicalOffset = null;
            var pulledDown = state.PeakPull >= PullDownThreshold;
            state.PeakPull = 0;
            state.IsGliding = false;
            if (pulledDown)
                state.Owner.RaiseEvent(new RoutedEventArgs(PulledDownEvent));
            if (state.Transform.Y != 0)
                SpringBackToZero(state);
            else
                state.StretchIsPull = false;
        }
        catch (Exception ex)
        {
            // Purely cosmetic - a failed spring-back must never disrupt scrolling.
            Logger.LogWarning(ex, "Rubber-band spring-back failed, skipping");
        }
    }

    // Eases whatever the live drag left RenderTransform.Y at back down to 0 -
    // driven by the shared AnimationClock (restarted, not stacked, if another
    // gesture ends mid-animation) rather than Avalonia's Animation machinery,
    // see this class's own doc comment for why.
    private static void SpringBackToZero(State state)
    {
        state.Animation?.Dispose();

        var from = state.Transform.Y;
        var duration = TimeSpan.FromMilliseconds(280);
        IDisposable? animation = null;
        animation = AnimationClock.Current.Subscribe(elapsed =>
        {
            var t = Math.Min(1.0, elapsed.TotalMilliseconds / duration.TotalMilliseconds);
            SetStretch(state, from * (1 - EaseOut(t)));

            if (t >= 1.0)
            {
                SetStretch(state, 0);
                state.StretchIsPull = false;
                animation!.Dispose();
                if (ReferenceEquals(state.Animation, animation))
                    state.Animation = null;
            }
        });
        state.Animation = animation;
    }

    private static double EaseOut(double t) => 1 - Math.Pow(1 - t, 3);
}

// How far a pull down past the top has gone towards opening what it opens -
// see RubberBandScroll.PullingDownEvent.
public sealed class PullingDownEventArgs(double progress) : RoutedEventArgs(RubberBandScroll.PullingDownEvent)
{
    public double Progress { get; } = progress;
}
