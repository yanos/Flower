using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

using Material.Icons;
using Material.Icons.Avalonia;

namespace Flower.Controls;

/// <summary>
/// The app's one text box: the primary-coloured border every field now draws
/// (App.axaml, which states it for anything that is a TextBox so this inherits
/// it), and a clear button inside the right-hand end that appears the moment
/// there is something to clear.
/// </summary>
/// <remarks>
/// A TextBox rather than a control wrapping one. Every field that swaps to it
/// keeps Text, PlaceholderText, KeyDown, LostFocus, focus and the attached
/// properties it already carried (SoftKeyboard.ReopenOnTap), and a binding
/// written against a TextBox keeps working unchanged - which is what makes
/// "use it everywhere" a rename rather than a rewrite. It has no ControlTheme
/// of its own for the same reason: StyleKeyOverride sends it to FluentTheme's
/// TextBox one.
/// </remarks>
public class TextInput : TextBox
{
    protected override Type StyleKeyOverride => typeof(TextBox);

    public static readonly StyledProperty<bool> ShowsClearButtonProperty =
        AvaloniaProperty.Register<TextInput, bool>(nameof(ShowsClearButton), defaultValue: true);

    /// <summary>
    /// Whether the little x appears at the right-hand end. On by default; off
    /// for a field where clearing is not a thing the user would want to do in
    /// one tap.
    /// </summary>
    public bool ShowsClearButton
    {
        get => GetValue(ShowsClearButtonProperty);
        set => SetValue(ShowsClearButtonProperty, value);
    }

    private readonly Button _clearButton;

    public TextInput()
    {
        _clearButton = BuildClearButton();
        // TextBox's own slot for something at the end of the line - the
        // presenter for it sits inside the border, beside the text, so the
        // button is in the box rather than next to it.
        InnerRightContent = _clearButton;
        UpdateClearButton();
    }

    private Button BuildClearButton()
    {
        var button = new Button
        {
            Background = Brushes.Transparent,
            BorderThickness = default,
            Padding = new Thickness(4),
            // Nothing about this button is a place to type, and taking focus
            // would be a real change of behaviour rather than a cosmetic one:
            // a box that commits what it holds when it loses focus (the new
            // playlist's name box) would commit on the way to being cleared.
            Focusable = false,
            Content = new MaterialIcon { Kind = MaterialIconKind.CloseCircle, Width = 16, Height = 16, Opacity = 0.6 },
        };
        button.Click += (_, e) =>
        {
            Clear();
            Focus();
            e.Handled = true;
        };
        return button;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty || change.Property == ShowsClearButtonProperty)
            UpdateClearButton();
    }

    // Hidden rather than removed, so the box's text does not shift sideways as
    // the first character is typed: a collapsed child of the inner presenter
    // takes no width either way.
    private void UpdateClearButton() =>
        _clearButton.IsVisible = ShowsClearButton && !string.IsNullOrEmpty(Text);
}
