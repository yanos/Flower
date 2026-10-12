using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

using Flower.Services;

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
///
/// Where the platform has a text field of its own (PlatformNativeTextField -
/// the phones), typing happens in that instead: it is laid over the box for as
/// long as the box has the focus (NativeTextSession), and the box asks
/// Avalonia for no keyboard of its own meanwhile.
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
        if (TypesNatively)
            InputMethod.SetIsInputMethodEnabled(this, false);
    }

    // ── Typing in the platform's own field ──────────────────────────────

    // Decided once, at startup, by the platform head (before Avalonia runs).
    private static bool TypesNatively => PlatformNativeTextField.Factory != null;

    private NativeTextSession? _session;

    /// <summary>
    /// The part of the box the text is drawn in - inside its border, padding
    /// and clear button - which is where the platform's field goes.
    /// </summary>
    internal Control? TextArea { get; private set; }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        TextArea = e.NameScope.Find<Control>("PART_ScrollViewer");
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        if (TypesNatively)
            HideAvaloniaSelectionHandles();
        base.OnGotFocus(e);
        if (!TypesNatively || IsReadOnly || _session != null)
            return;
        _session = NativeTextSession.Start(this);
        // The box's own copy of the words, its caret and its placeholder
        // make way for the field's (App.axaml).
        Classes.Set("nativeEditing", _session != null);
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        EndNativeEditing();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        EndNativeEditing();
    }

    private static bool _handlesHidden;

    // Avalonia's touch handles for the caret and a selection, which it puts up
    // as a box takes the focus: the platform's field draws its own. In code
    // rather than App.axaml, and only where the platform types: an
    // {OnPlatform} setter for this resolved to the string "0" on iOS and
    // threw on the first box focused. Opacity and hit-testing rather than
    // IsVisible, which the handles set themselves.
    private static void HideAvaloniaSelectionHandles()
    {
        if (_handlesHidden || Application.Current is not { } app)
            return;
        _handlesHidden = true;
        app.Styles.Add(new Style(x => x.OfType<TextSelectionHandle>())
        {
            Setters =
            {
                new Setter(OpacityProperty, 0d),
                new Setter(IsHitTestVisibleProperty, false),
            },
        });
    }

    // The field goes after whatever took the focus has started its own - see
    // NativeTextSession.End.
    private void EndNativeEditing()
    {
        if (_session is not { } session)
            return;
        _session = null;
        session.End();
        Classes.Set("nativeEditing", false);
        Dispatcher.UIThread.Post(session.Dispose, DispatcherPriority.Background);
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
        if (change.Property == TextProperty)
            _session?.BoxTextChanged();
        // Kept off for good where the platform types: anything turning
        // Avalonia's keyboard back on for this box would raise it alongside
        // the platform's.
        else if (change.Property == InputMethod.IsInputMethodEnabledProperty && TypesNatively && change.GetNewValue<bool>())
            InputMethod.SetIsInputMethodEnabled(this, false);
    }

    // Hidden rather than removed, so the box's text does not shift sideways as
    // the first character is typed: a collapsed child of the inner presenter
    // takes no width either way.
    private void UpdateClearButton() =>
        _clearButton.IsVisible = ShowsClearButton && !string.IsNullOrEmpty(Text);
}
