using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

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
// Only a press on the list that is gliding stops it - the recognizer ending
// the glide is that list's own - so only that press is judged. A tap on the
// tab bar, the mini player or a header button while a list behind them is
// still gliding is a tap, and clicks; swallowing it was the button that lit
// up under the finger and did nothing. And a list that leaves the screen
// mid-glide - a tab switched away from, a screen torn down - says that its
// glide ended to itself alone, out of the tree where this cannot hear it, so
// a glide whose list is gone is no glide: kept, it swallowed every tap after
// it until the next flick.
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

        // What the glide is scrolling: the recognizer's target, which raised
        // its start.
        private Visual? _gliding;

        public void Attach(InputElement element)
        {
            element.AddHandler(InputElement.ScrollGestureInertiaStartingEvent, (_, e) =>
            {
                _glidingGestureId = e.Id;
                _gliding = e.Source as Visual;
            }, RoutingStrategies.Bubble, handledEventsToo: true);

            element.AddHandler(InputElement.ScrollGestureEndedEvent, (_, e) =>
            {
                if (e.Id == _glidingGestureId)
                    EndGlide();
            }, RoutingStrategies.Bubble, handledEventsToo: true);

            element.AddHandler(InputElement.PointerPressedEvent,
                (_, e) => s_stoppingPointer = StopsGlide(e) ? e.Pointer : null,
                RoutingStrategies.Tunnel, handledEventsToo: true);

            element.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
            {
                if (!IsStoppingGlide(e.Pointer))
                    return;
                s_stoppingPointer = null;
                e.Handled = true;
            }, RoutingStrategies.Tunnel, handledEventsToo: true);
        }

        private bool StopsGlide(PointerPressedEventArgs e)
        {
            if (_glidingGestureId == 0 || _gliding is not { } gliding)
                return false;
            if (!gliding.IsAttachedToVisualTree())
            {
                EndGlide();
                return false;
            }
            return e.Source is Visual pressed
                && (ReferenceEquals(pressed, gliding) || gliding.IsVisualAncestorOf(pressed));
        }

        private void EndGlide()
        {
            _glidingGestureId = 0;
            _gliding = null;
        }
    }
}
