using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Flower.Controls;

// A finger that lands on a list while it is still gliding from a flick stops
// the glide, and that is all it means - lifting it again must not also tap
// whatever row it happened to land on. Every phone treats it that way, and
// Avalonia does not: ScrollGestureRecognizer ends the inertia on the press, but
// the row's Button is pressed by the same event, and a release with no movement
// in between is a click. So a flick, a touch to stop it, and a lift played
// whichever song had scrolled under the finger.
//
// Set on the root of a view, this watches every scroller inside it. A press
// that arrives while any of them is gliding is remembered, and its release is
// marked handled on the way down (tunnel), before it reaches the Button - whose
// OnPointerReleased only runs for an unhandled release, so no click. The Button
// is left pressed only until the touch ends and the pointer's capture goes with
// it. Everything that watches releases with handledEventsToo - the scroll
// recognizer, ScreenStackPanel's swipe - still sees this one.
//
// The press has to be judged on the tunnel too: the recognizer runs from a
// class handler on the bubble pass, and by then it has already ended the glide.
//
// Leaving the finger down after stopping a glide means the same thing - it
// was a stop, not a long press - but Holding is not something a release can
// be kept from, since it fires while the finger is still down. So the press is
// also published (IsStoppingGlide), and LongPress asks before acting on one.
public static class FlingStopGuard
{
    // One at a time app-wide, like touches on the one screen a phone has.
    private static IPointer? s_stoppingPointer;

    public static bool IsStoppingGlide(IPointer? pointer) =>
        pointer is not null && ReferenceEquals(pointer, s_stoppingPointer);

    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<InputElement, bool>("IsEnabled", typeof(FlingStopGuard));

    public static bool GetIsEnabled(InputElement element) => element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(InputElement element, bool value) => element.SetValue(IsEnabledProperty, value);

    static FlingStopGuard()
    {
        IsEnabledProperty.Changed.AddClassHandler<InputElement>((element, e) =>
        {
            if (e.GetNewValue<bool>())
                new State().Attach(element);
        });
    }

    private sealed class State
    {
        // The gesture whose glide is running, 0 when none is. By id rather
        // than a flag, so the end of some other gesture cannot clear it.
        private int _glidingGestureId;

        public void Attach(InputElement element)
        {
            element.AddHandler(InputElement.ScrollGestureInertiaStartingEvent,
                (_, e) => _glidingGestureId = e.Id,
                RoutingStrategies.Bubble, handledEventsToo: true);

            element.AddHandler(InputElement.ScrollGestureEndedEvent, (_, e) =>
            {
                if (e.Id == _glidingGestureId)
                    _glidingGestureId = 0;
            }, RoutingStrategies.Bubble, handledEventsToo: true);

            element.AddHandler(InputElement.PointerPressedEvent,
                (_, e) => s_stoppingPointer = _glidingGestureId != 0 ? e.Pointer : null,
                RoutingStrategies.Tunnel, handledEventsToo: true);

            element.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
            {
                if (!IsStoppingGlide(e.Pointer))
                    return;
                s_stoppingPointer = null;
                e.Handled = true;
            }, RoutingStrategies.Tunnel, handledEventsToo: true);
        }
    }
}
