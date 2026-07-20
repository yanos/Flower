using System;
using System.Runtime.CompilerServices;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Flower.Controls;

// Continuously rotates a control's RenderTransform while IsSpinning is true -
// e.g. the download-in-progress "Sync" icon on a mobile placeholder row (see
// MobileMainView.axaml's TrackRowTemplate). A plain looping rotation via
// Avalonia's own Animation/KeyFrame system routes through TransformAnimator,
// which throws InvalidCastException against a plain RotateTransform on iOS -
// the identical crash RubberBandScroll hit with TranslateTransform (see that
// class's own doc comment) - so this uses the same manual DispatcherTimer-
// driven tween that class and MainView.axaml.cs's status-bar spinner both
// already rely on instead.
public static class SpinAnimation
{
    public static readonly AttachedProperty<bool> IsSpinningProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsSpinning", typeof(SpinAnimation));

    public static bool GetIsSpinning(Control control) => control.GetValue(IsSpinningProperty);
    public static void SetIsSpinning(Control control, bool value) => control.SetValue(IsSpinningProperty, value);

    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(16);
    private const double DegreesPerTick = 6; // ~60 ticks/rev at 16ms - about one revolution/second.

    static SpinAnimation()
    {
        IsSpinningProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.NewValue is true)
                Start(control);
            else
                Stop(control);
        });
    }

    private sealed class State
    {
        public readonly RotateTransform Transform = new();
        public DispatcherTimer? Timer;
    }

    // Keyed off the control itself rather than a field on it - an attached
    // property has nowhere else to stash per-instance state, and this way a
    // recycled/virtualized row control (MobileMainView's TrackListBox pools
    // and reuses row containers) just gets a fresh State the next time this
    // property changes on it, no explicit cleanup needed for the old one.
    private static readonly ConditionalWeakTable<Control, State> States = new();

    private static void Start(Control control)
    {
        var state = States.GetOrCreateValue(control);
        control.RenderTransform = state.Transform;
        control.RenderTransformOrigin = RelativePoint.Center;

        state.Timer?.Stop();
        var timer = new DispatcherTimer { Interval = Interval };
        state.Timer = timer;
        timer.Tick += (_, _) => state.Transform.Angle = (state.Transform.Angle + DegreesPerTick) % 360;
        timer.Start();
    }

    private static void Stop(Control control)
    {
        if (!States.TryGetValue(control, out var state))
            return;
        state.Timer?.Stop();
        state.Timer = null;
        state.Transform.Angle = 0;
    }
}
