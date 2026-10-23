using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Media;
using Avalonia.VisualTree;

using Flower.Services;

namespace Flower.Controls;

/// <summary>
/// One spell of typing in a <see cref="TextInput"/> through the platform's own
/// field (<see cref="PlatformNativeTextField"/>): from the box taking the
/// focus to its losing it.
/// </summary>
/// <remarks>
/// The box keeps the focus throughout, as far as the app can tell - its
/// GotFocus and LostFocus, IsFocused, KeyDown and Text all behave as they
/// would for Avalonia's own editing, so nothing that listens to a box has to
/// know. What changes is underneath: the box asks Avalonia for no keyboard
/// (TextInput turns its input method off), and the platform's field, laid
/// exactly over the box's text, takes the keyboard instead. Its text is the
/// box's, both ways, as it is typed; its Return key is the box's Enter; and
/// its losing the keyboard is the box's losing the focus. The box's own text,
/// caret and placeholder are hidden meanwhile (TextBox.nativeEditing,
/// App.axaml), so there is one copy of the words on screen.
///
/// Placed every frame rather than on layout, because what moves a box is as
/// often a render transform - a sheet sliding up, a screen sliding over, a
/// list pulled past its end - as a layout pass, and a frame is the only thing
/// both raise. Cut to whatever clips the box, so a field scrolled half out of
/// its list shows only the half still in it.
/// </remarks>
internal sealed class NativeTextSession
{
    private readonly TextInput _box;
    private readonly TopLevel _topLevel;
    private readonly INativeTextField _field;
    private bool _ended;
    private bool _fromField;
    private Rect _placedField;
    private Rect _placedVisible;

    private NativeTextSession(TextInput box, TopLevel topLevel, INativeTextField field)
    {
        _box = box;
        _topLevel = topLevel;
        _field = field;
    }

    /// <summary>
    /// Opens the platform's field over the box, with its text and selection,
    /// and hands it the keyboard. Null where there is no platform field.
    /// </summary>
    public static NativeTextSession? Start(TextInput box)
    {
        if (PlatformNativeTextField.Factory is not { } factory
            || TopLevel.GetTopLevel(box) is not { } topLevel
            || factory.Create(topLevel, LookOf(box)) is not { } field)
            return null;

        var session = new NativeTextSession(box, topLevel, field);
        var text = box.Text ?? "";
        field.Text = text;
        // A selection the box was given - a name selected whole to be typed
        // over - is the field's. A bare caret is not: nothing types in the box
        // itself, so its caret is wherever text put in by a binding left it,
        // which is the start, and a box tapped back into began there.
        if (box.SelectionStart != box.SelectionEnd)
            field.Select(box.SelectionStart, box.SelectionEnd);
        else
            field.Select(text.Length, text.Length);
        field.TextChanged += session.OnFieldTextChanged;
        field.ReturnPressed += session.OnFieldReturn;
        field.EditingEnded += session.OnFieldEditingEnded;
        session.Place();
        field.BeginEditing();
        topLevel.RequestAnimationFrame(session.OnFrame);
        return session;
    }

    /// <summary>
    /// Stops following the box; the field goes once the frame's work is done
    /// (<see cref="Dispose"/>), so a box taking the focus from this one can
    /// take the keyboard from this field first and it never drops in between.
    /// </summary>
    public void End()
    {
        if (_ended)
            return;
        _ended = true;
        _field.TextChanged -= OnFieldTextChanged;
        _field.ReturnPressed -= OnFieldReturn;
        _field.EditingEnded -= OnFieldEditingEnded;
    }

    public void Dispose()
    {
        End();
        _field.Dispose();
    }

    /// <summary>The box's text changed - from the field, or from anything else.</summary>
    public void BoxTextChanged()
    {
        if (_ended)
            return;
        var text = _box.Text ?? "";
        // From the field, the box may still have changed it on the way in -
        // a newline in a one-line box, a length over its maximum - and the
        // field has to show what the box kept, not what was typed.
        if (_fromField && _field.Text == text)
            return;
        _field.Text = text;
        _field.Select(text.Length, text.Length);
    }

    private void OnFieldTextChanged(string text)
    {
        _fromField = true;
        try
        {
            _box.Text = text;
        }
        finally
        {
            _fromField = false;
        }
    }

    // The platform's Return is the box's Enter, so everything listening for
    // that - a filter closing, a name committing - hears it. A one-line
    // field's Return is "done" on a phone, so one nothing claimed puts the
    // keyboard away, by taking the focus off the box as every Enter handler
    // here does itself.
    private void OnFieldReturn()
    {
        var enter = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Enter,
            Source = _box,
        };
        _box.RaiseEvent(enter);
        if (!enter.Handled && _box.IsFocused)
            _topLevel.FocusManager?.Focus(null);
    }

    // The platform took the keyboard away from the field on its own: the box
    // loses the focus, so whatever happens on its LostFocus - a rename
    // committing - happens.
    private void OnFieldEditingEnded()
    {
        if (_box.IsFocused)
            _topLevel.FocusManager?.Focus(null);
    }

    private void OnFrame(TimeSpan _)
    {
        if (_ended)
            return;
        Place();
        _topLevel.RequestAnimationFrame(OnFrame);
    }

    private void Place()
    {
        var (field, visible) = Where();
        if (field == _placedField && visible == _placedVisible)
            return;
        _placedField = field;
        _placedVisible = visible;
        _field.Place(field, visible);
    }

    // The box's text area - its scroller, which is the text's own viewport,
    // inside the border, the padding and the clear button - in the top
    // level's coordinates, and how much of it shows: cut by every ancestor
    // that clips, and nothing at all while anything above it is hidden.
    private (Rect Field, Rect Visible) Where()
    {
        if (_box.TextArea is not { } area
            || !_box.IsEffectivelyVisible
            || area.TransformToVisual(_topLevel) is not { } toTop)
            return (default, default);

        var field = new Rect(area.Bounds.Size).TransformToAABB(toTop);
        var visible = field;
        for (var v = area.GetVisualParent(); v != null && !ReferenceEquals(v, _topLevel); v = v.GetVisualParent())
        {
            if (v.Opacity <= 0)
                return (field, default);
            if (!v.ClipToBounds)
                continue;
            if (v.TransformToVisual(_topLevel) is not { } clipToTop)
                return (field, default);
            visible = visible.Intersect(new Rect(v.Bounds.Size).TransformToAABB(clipToTop));
        }
        return (field, visible.Width > 0 && visible.Height > 0 ? visible : default);
    }

    private static NativeTextFieldLook LookOf(TextInput box)
    {
        var options = TextInputOptions.FromStyledElement(box);
        return new NativeTextFieldLook(
            FontSize: box.FontSize,
            FontWeight: box.FontWeight,
            Foreground: ColorOf(box.Foreground, Colors.Black),
            PlaceholderForeground: ColorOf(box.PlaceholderForeground, ColorOf(box.Foreground, Colors.Gray), 0.5),
            Caret: ColorOf(box.CaretBrush, ColorOf(box.Foreground, Colors.Black)),
            Placeholder: box.PlaceholderText,
            Alignment: box.TextAlignment,
            Multiline: box.AcceptsReturn,
            MaxLength: box.MaxLength,
            ContentType: options.ContentType,
            ReturnKeyType: options.ReturnKeyType,
            AutoCapitalization: options.AutoCapitalization,
            ShowSuggestions: options.ShowSuggestions,
            IsSensitive: options.IsSensitive);
    }

    // A brush's colour at the strength it is drawn - a SolidColorBrush's
    // opacity folded into the colour's alpha, since the platform takes a
    // colour, not a brush.
    private static Color ColorOf(IBrush? brush, Color fallback, double fadeFallback = 1)
    {
        if (brush is not ISolidColorBrush solid)
            return Color.FromArgb((byte)(fallback.A * fadeFallback), fallback.R, fallback.G, fallback.B);
        var c = solid.Color;
        return Color.FromArgb((byte)Math.Round(c.A * solid.Opacity), c.R, c.G, c.B);
    }
}
