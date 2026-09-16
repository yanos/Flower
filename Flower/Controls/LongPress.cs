using System.Windows.Input;

using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Flower.Controls;

// Runs a command when a finger rests on an element, off Avalonia's own Holding
// gesture (touch and pen only; a mouse has a right button for this). The track
// rows use it to open the same menu their "..." button does.
//
// The element is usually a Button that already acts on a tap, and the hold
// fires while the finger is still down - so lifting it afterwards would also
// click, playing the track whose menu just opened. Releasing the pointer
// capture the press took is what stops that: a Button that loses capture stops
// being pressed, and a release it is not pressed for is not a click. Holding's
// event args do not expose the pointer, so the press is remembered here.
public static class LongPress
{
    public static readonly AttachedProperty<ICommand?> CommandProperty =
        AvaloniaProperty.RegisterAttached<InputElement, ICommand?>("Command", typeof(LongPress));

    public static readonly AttachedProperty<object?> CommandParameterProperty =
        AvaloniaProperty.RegisterAttached<InputElement, object?>("CommandParameter", typeof(LongPress));

    private static readonly AttachedProperty<bool> IsAttachedProperty =
        AvaloniaProperty.RegisterAttached<InputElement, bool>("IsAttached", typeof(LongPress));

    private static readonly AttachedProperty<IPointer?> PressedPointerProperty =
        AvaloniaProperty.RegisterAttached<InputElement, IPointer?>("PressedPointer", typeof(LongPress));

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

        // Tunnel and handledEventsToo: the Button handles its own press, and
        // this has to see the press regardless.
        element.AddHandler(InputElement.PointerPressedEvent,
            (_, e) => element.SetValue(PressedPointerProperty, e.Pointer),
            RoutingStrategies.Tunnel, handledEventsToo: true);

        element.AddHandler(InputElement.HoldingEvent, (_, e) =>
        {
            if (e.HoldingState != HoldingState.Started)
                return;
            // A finger resting where it landed to stop a glide - see
            // FlingStopGuard.
            if (FlingStopGuard.IsStoppingGlide(element.GetValue(PressedPointerProperty)))
                return;
            var command = GetCommand(element);
            var parameter = GetCommandParameter(element);
            if (command == null || !command.CanExecute(parameter))
                return;

            element.GetValue(PressedPointerProperty)?.Capture(null);
            e.Handled = true;
            command.Execute(parameter);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }
}
