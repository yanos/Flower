using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;

using CommunityToolkit.Mvvm.Input;

using Flower.Services;

namespace Flower.Controls;

// A "Copy" menu for a name shown as words, opened by a right click or, on a
// phone, a long press. For links, where selectable text would take the
// click, and for phones, where SelectableTextBlock takes the finger a scroll
// needs.
public static class CopyText
{
    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Text", typeof(CopyText));

    private static readonly AttachedProperty<bool> IsAttachedProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsAttached", typeof(CopyText));

    private static readonly AttachedProperty<ContextMenu?> OpenMenuProperty =
        AvaloniaProperty.RegisterAttached<Control, ContextMenu?>("OpenMenu", typeof(CopyText));

    public static string? GetText(Control control) => control.GetValue(TextProperty);

    // For tests: the menu showing, if any.
    internal static ContextMenu? OpenMenu(Control control) =>
        control.GetValue(OpenMenuProperty) is { IsOpen: true } menu ? menu : null;
    public static void SetText(Control control, string? value) => control.SetValue(TextProperty, value);

    static CopyText()
    {
        TextProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.NewValue is not null)
                Attach(control);
        });
    }

    // Once per control. An existing long press is kept.
    private static void Attach(Control control)
    {
        if (control.GetValue(IsAttachedProperty))
            return;
        control.SetValue(IsAttachedProperty, true);

        control.ContextRequested += (_, e) =>
        {
            if (Open(control))
                e.Handled = true;
        };
        if (LongPress.GetCommand(control) is null)
            LongPress.SetCommand(control, new RelayCommand(() => Open(control)));
    }

    // Once, though Avalonia's hold may ask before LongPress does.
    public static bool Open(Control control)
    {
        if (GetText(control) is not { Length: > 0 } text)
            return false;
        if (control.GetValue(OpenMenuProperty) is { IsOpen: true })
            return true;

        var copy = new MenuItem { Header = "Copy" };
        copy.Click += (_, _) => _ = TopLevel.GetTopLevel(control)?.Clipboard?.SetTextAsync(text);
        var menu = new ContextMenu { Items = { copy } };
        menu.Closed += (_, _) => control.ClearValue(OpenMenuProperty);
        control.SetValue(OpenMenuProperty, menu);
        PlatformContextMenu.Open(menu, control);
        return true;
    }
}
