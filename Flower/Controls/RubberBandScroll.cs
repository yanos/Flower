using System;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

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
// in-progress Gestures.ScrollGestureEvent for every increment of a touch-drag
// scroll (Delta), not just Gestures.ScrollGestureEndedEvent once it finishes -
// this tracks its own "logical" offset from those live deltas, independent of
// ScrollViewer.Offset itself (which silently clamps to [0, Extent-Viewport],
// giving no residual amount to react to once already pinned at a bound), and
// once that logical offset would go negative or past the max, applies the
// excess - damped, so it gets harder to pull further, not linear - straight
// to RenderTransform.Y live, so the content visibly drags along with the
// finger while still touching, springing back only once the gesture actually
// ends (Gestures.ScrollGestureEndedEvent).
//
// The spring-back (and the live drag itself) is driven by a DispatcherTimer
// stepping RenderTransform.Y frame by frame (the same manual-tween approach
// MainView.axaml.cs's status-bar spinner uses), NOT Avalonia's Animation/
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

    private sealed class State
    {
        public ScrollViewer? ScrollViewer;
        public readonly TranslateTransform Transform = new();
        public DispatcherTimer? Timer;

        // The offset the current gesture would have reached if ScrollViewer
        // didn't clamp it - null between gestures. Re-seeded from the real,
        // clamped Offset at the start of each gesture (see OnScrollGesture),
        // so this only ever needs to track the delta from there, not
        // reconstruct the whole scroll history.
        public double? LogicalOffset;
    }

    private static void Attach(Control control)
    {
        try
        {
            var state = new State();
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

            Gestures.AddScrollGestureHandler(control, (_, e) =>
            {
                if (e is ScrollGestureEventArgs args)
                    OnScrollGesture(state, args);
            });
            Gestures.AddScrollGestureEndedHandler(control, (_, _) => OnScrollGestureEnded(state));
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
                state.Timer?.Stop();
                state.Transform.Y = Damp(-state.LogicalOffset.Value);
            }
            else if (state.LogicalOffset > max)
            {
                state.Timer?.Stop();
                state.Transform.Y = -Damp(state.LogicalOffset.Value - max);
            }
            else if (state.Transform.Y != 0)
            {
                state.Transform.Y = 0;
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Rubber-band live drag failed, skipping");
        }
    }

    // Diminishing returns the further past the bound the finger already is -
    // sqrt rather than a flat multiplier so the first few pixels of pull
    // still feel responsive while a long, determined drag tapers off well
    // short of MaxOverscroll instead of reaching it abruptly.
    private static double Damp(double rawOverscroll) =>
        Math.Min(MaxOverscroll, Math.Sqrt(rawOverscroll) * Resistance * 10);

    private static void OnScrollGestureEnded(State state)
    {
        try
        {
            state.LogicalOffset = null;
            if (state.Transform.Y != 0)
                SpringBackToZero(state);
        }
        catch (Exception ex)
        {
            // Purely cosmetic - a failed spring-back must never disrupt scrolling.
            Logger.LogWarning(ex, "Rubber-band spring-back failed, skipping");
        }
    }

    // Eases whatever the live drag left RenderTransform.Y at back down to 0 -
    // driven by a per-control DispatcherTimer (restarted, not stacked, if
    // another gesture ends mid-animation) rather than Avalonia's Animation
    // machinery, see this class's own doc comment for why.
    private static void SpringBackToZero(State state)
    {
        state.Timer?.Stop();

        var from = state.Transform.Y;
        var start = DateTime.UtcNow;
        var duration = TimeSpan.FromMilliseconds(280);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        state.Timer = timer;
        timer.Tick += (_, _) =>
        {
            var t = Math.Min(1.0, (DateTime.UtcNow - start).TotalMilliseconds / duration.TotalMilliseconds);
            state.Transform.Y = from * (1 - EaseOut(t));

            if (t >= 1.0)
            {
                state.Transform.Y = 0;
                timer.Stop();
                if (ReferenceEquals(state.Timer, timer))
                    state.Timer = null;
            }
        };
        timer.Start();
    }

    private static double EaseOut(double t) => 1 - Math.Pow(1 - t, 3);
}
