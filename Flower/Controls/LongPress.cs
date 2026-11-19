using System;
using System.Windows.Input;

using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Flower.Services;

namespace Flower.Controls;

// Runs a command when a finger rests on an element (touch and pen only; a
// mouse has a right button for this), with the platform's haptic tick as it
// does. The track rows use it to open the same menu their "..." button does.
//
// Timed here rather than taken from Avalonia's Holding gesture, which gives up
// once the finger drifts 4px - see LongPressTracker, which decides whether the
// touch still counts. The wait is the platform's own (HoldWaitDuration).
//
// The element is usually a Button that already acts on a tap, and the hold
// fires while the finger is still down - so lifting it afterwards would also
// click, playing the track whose menu just opened. Releasing the pointer
// capture the press took is what stops that: a Button that loses capture stops
// being pressed, and a release it is not pressed for is not a click.
public static class LongPress
{
    private static readonly TimeSpan FallbackWait = TimeSpan.FromMilliseconds(500);

    public static readonly AttachedProperty<ICommand?> CommandProperty =
        AvaloniaProperty.RegisterAttached<InputElement, ICommand?>("Command", typeof(LongPress));

    public static readonly AttachedProperty<object?> CommandParameterProperty =
        AvaloniaProperty.RegisterAttached<InputElement, object?>("CommandParameter", typeof(LongPress));

    private static readonly AttachedProperty<bool> IsAttachedProperty =
        AvaloniaProperty.RegisterAttached<InputElement, bool>("IsAttached", typeof(LongPress));

    public static ICommand? GetCommand(InputElement element) => element.GetValue(CommandProperty);
    public static void SetCommand(InputElement element, ICommand? value) => element.SetValue(CommandProperty, value);

    public static object? GetCommandParameter(InputElement element) => element.GetValue(CommandParameterProperty);
    public static void SetCommandParameter(InputElement element, object? value) => element.SetValue(CommandParameterProperty, value);

    static LongPress()
    {
        CommandProperty.Changed.AddClassHandler<InputElement>((element, e) =>
        {
            if (e.NewValue is not null)
                Attach(element);
        });
    }

    // Once per element however often its command is rebound - a recycled row
    // gets a new binding value each time it is reused.
    private static void Attach(InputElement element)
    {
        if (element.GetValue(IsAttachedProperty))
            return;
        element.SetValue(IsAttachedProperty, true);

        var tracker = new LongPressTracker();
        IDisposable? wait = null;

        void Cancel(IPointer pointer)
        {
            tracker.End(pointer);
            if (tracker.IsPending)
                return;
            wait?.Dispose();
            wait = null;
        }

        // Tunnel and handledEventsToo throughout: the Button handles its own
        // press and release, and FlingStopGuard marks a release handled on
        // the way down - this has to see them regardless.
        element.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            wait?.Dispose();
            wait = null;
            if (e.Pointer.Type is not (PointerType.Touch or PointerType.Pen))
                return;

            var pointer = e.Pointer;
            tracker.Press(pointer, e.GetPosition(null));
            var duration = element.GetPlatformSettings()?.HoldWaitDuration ?? FallbackWait;
            wait = DispatcherTimer.RunOnce(() => Fire(element, tracker, pointer), duration);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        element.AddHandler(InputElement.PointerMovedEvent,
            (_, e) => tracker.Move(e.Pointer, e.GetPosition(null)),
            RoutingStrategies.Tunnel, handledEventsToo: true);

        element.AddHandler(InputElement.PointerReleasedEvent,
            (_, e) => Cancel(e.Pointer),
            RoutingStrategies.Tunnel, handledEventsToo: true);

        element.DetachedFromVisualTree += (_, _) =>
        {
            wait?.Dispose();
            wait = null;
        };
    }

    private static void Fire(InputElement element, LongPressTracker tracker, IPointer pointer)
    {
        if (!tracker.Fire(pointer))
            return;
        // Something else took the touch while it waited - the list it is in,
        // scrolling it.
        if (pointer.Captured is Visual captured
            && !ReferenceEquals(captured, element) && !element.IsVisualAncestorOf(captured))
            return;
        // A finger resting where it landed to stop a glide - see
        // FlingStopGuard.
        if (FlingStopGuard.IsStoppingGlide(pointer))
            return;
        var command = GetCommand(element);
        var parameter = GetCommandParameter(element);
        if (command == null || !command.CanExecute(parameter))
            return;

        pointer.Capture(null);
        command.Execute(parameter);
        PlatformHaptics.Current?.LongPress();
    }
}
