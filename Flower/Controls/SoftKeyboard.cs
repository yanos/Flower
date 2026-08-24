using System.Runtime.CompilerServices;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Flower.Controls;

// Two soft-keyboard manners a phone needs and a desktop does not. Both are
// attached properties rather than a custom TextBox subclass so that any view
// can opt in with one attribute, and both are inert where the platform has no
// input pane (TopLevel.InputPane is null on desktop), so setting them on a
// shared view costs nothing there.
//
//   AvoidOcclusion - iOS/Android slide the keyboard up over the bottom of the
//   window, which is exactly where every mobile sheet here is anchored, so the
//   box being typed into is the first thing it covers. Lifts the control it is
//   set on by the covered height for as long as the keyboard is up.
//
//   ReopenOnTap - the keyboard's own Return/done key hides it but leaves the
//   TextBox focused, and focus is the only thing that raises it, so a second
//   tap on the box does nothing at all and the text becomes uneditable. Bounces
//   focus off the box and back so the platform raises it again.
public static class SoftKeyboard
{
    public static readonly AttachedProperty<bool> AvoidOcclusionProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("AvoidOcclusion", typeof(SoftKeyboard));

    public static void SetAvoidOcclusion(Control control, bool value) => control.SetValue(AvoidOcclusionProperty, value);
    public static bool GetAvoidOcclusion(Control control) => control.GetValue(AvoidOcclusionProperty);

    public static readonly AttachedProperty<bool> ReopenOnTapProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("ReopenOnTap", typeof(SoftKeyboard));

    public static void SetReopenOnTap(TextBox box, bool value) => box.SetValue(ReopenOnTapProperty, value);
    public static bool GetReopenOnTap(TextBox box) => box.GetValue(ReopenOnTapProperty);

    private static readonly ConditionalWeakTable<Control, KeyboardInset> Insets = new();

    static SoftKeyboard()
    {
        AvoidOcclusionProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (Insets.TryGetValue(control, out var existing))
            {
                existing.Detach();
                Insets.Remove(control);
            }

            if (e.GetNewValue<bool>())
            {
                var inset = new KeyboardInset(control);
                Insets.Add(control, inset);
                inset.Attach();
            }
        });

        ReopenOnTapProperty.Changed.AddClassHandler<TextBox>((box, e) =>
        {
            // Tapped rather than PointerPressed, and handledEventsToo, because
            // TextBox handles the press itself to move the caret.
            if (e.GetNewValue<bool>())
                box.AddHandler(InputElement.TappedEvent, OnTapped, RoutingStrategies.Bubble, handledEventsToo: true);
            else
                box.RemoveHandler(InputElement.TappedEvent, OnTapped);
        });
    }

    private static void OnTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not TextBox box || !box.IsFocused)
            return;

        var topLevel = TopLevel.GetTopLevel(box);
        // Only when the keyboard is actually down: bouncing focus while it is
        // up would flicker it and drop the caret for no reason.
        if (topLevel?.InputPane is not { State: InputPaneState.Closed } || topLevel.FocusManager is not { } focus)
            return;

        focus.Focus(null);
        Dispatcher.UIThread.Post(() => box.Focus(NavigationMethod.Pointer), DispatcherPriority.Input);
    }

    // One control's subscription to the input pane. Kept per-control (rather
    // than a single static listener) because the margin it restores is the
    // control's own.
    private sealed class KeyboardInset
    {
        private readonly Control _control;
        private IInputPane? _pane;
        private Thickness? _baseMargin;

        internal KeyboardInset(Control control) => _control = control;

        internal void Attach()
        {
            _control.AttachedToVisualTree += OnAttachedToVisualTree;
            _control.DetachedFromVisualTree += OnDetachedFromVisualTree;
            if (TopLevel.GetTopLevel(_control) != null)
                Hook();
        }

        internal void Detach()
        {
            _control.AttachedToVisualTree -= OnAttachedToVisualTree;
            _control.DetachedFromVisualTree -= OnDetachedFromVisualTree;
            Unhook();
            Apply(0);
        }

        private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e) => Hook();

        private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        {
            Unhook();
            // A sheet closed while the keyboard was up comes back offset by a
            // keyboard that is no longer there otherwise.
            Apply(0);
        }

        private void Hook()
        {
            if (_pane != null)
                return;
            _pane = TopLevel.GetTopLevel(_control)?.InputPane;
            if (_pane == null)
                return;
            _pane.StateChanged += OnStateChanged;
            Apply(_pane.State == InputPaneState.Open ? _pane.OccludedRect.Height : 0);
        }

        private void Unhook()
        {
            if (_pane == null)
                return;
            _pane.StateChanged -= OnStateChanged;
            _pane = null;
        }

        private void OnStateChanged(object? sender, InputPaneStateEventArgs e) =>
            Apply(e.NewState == InputPaneState.Open ? e.EndRect.Height : 0);

        private void Apply(double inset)
        {
            // Captured on first use rather than in the constructor: the
            // attached property is set while the XAML is still being loaded,
            // so Margin may not have been assigned yet.
            var margin = _baseMargin ??= _control.Margin;
            _control.Margin = new Thickness(margin.Left, margin.Top, margin.Right, margin.Bottom + inset);
        }
    }
}
