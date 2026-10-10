using System.Runtime.CompilerServices;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

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
//   set on by the covered height for as long as the keyboard is up - and,
//   since a scroller lifted that way is only shorter, not scrolled, then
//   scrolls the box being typed in back into what is left of it: when the
//   keyboard comes up, and each time another box takes the focus under it.
//
//   DismissOnTapOutside - a phone keyboard has no key that puts it away, so a
//   tap anywhere in the control it is set on that is not on a text box does.
//   A tap, not a press: a press is also the start of a scroll, and scrolling
//   to look at the next field should not take the keyboard away from this
//   one. The suggestion chips under a box (non-focusable buttons) are part of
//   typing in it, so they keep it too.
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

    public static readonly AttachedProperty<bool> DismissOnTapOutsideProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("DismissOnTapOutside", typeof(SoftKeyboard));

    public static void SetDismissOnTapOutside(Control control, bool value) => control.SetValue(DismissOnTapOutsideProperty, value);
    public static bool GetDismissOnTapOutside(Control control) => control.GetValue(DismissOnTapOutsideProperty);

    // How much room to leave below a box brought into view: a row of the
    // suggestion chips that open under some boxes, and a little air.
    private const double RoomBelowBox = 64;
    private const double RoomAboveBox = 16;

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

        DismissOnTapOutsideProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            // handledEventsToo: a button handles its own release, and a tap
            // on one still puts the keyboard away.
            if (e.GetNewValue<bool>())
                control.AddHandler(InputElement.TappedEvent, OnTappedInside, RoutingStrategies.Bubble, handledEventsToo: true);
            else
                control.RemoveHandler(InputElement.TappedEvent, OnTappedInside);
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

    private static void OnTappedInside(object? sender, TappedEventArgs e)
    {
        if (sender is not Control control
            || TopLevel.GetTopLevel(control)?.FocusManager is not { } focus
            || focus.GetFocusedElement() is not TextBox box
            || !control.IsVisualAncestorOf(box))
            return;

        for (var v = e.Source as Visual; v != null && !ReferenceEquals(v, control); v = v.GetVisualParent())
        {
            if (v is TextBox || v is Button { Focusable: false })
                return;
        }

        // Avalonia has no call to hide the keyboard; taking the focus off the
        // box is what drops it, on both phones.
        focus.Focus(null);
    }

    // The box with the focus, if it is inside the control, scrolled into the
    // part of it the keyboard leaves - after the layout the keyboard's inset
    // has just caused, or there is nothing to scroll into yet.
    private static void BringFocusedBoxIntoView(Control control) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (TopLevel.GetTopLevel(control) is not { InputPane.State: InputPaneState.Open, FocusManager: { } focus }
                || focus.GetFocusedElement() is not TextBox box
                || !control.IsVisualAncestorOf(box))
                return;
            box.BringIntoView(new Rect(0, -RoomAboveBox, box.Bounds.Width, box.Bounds.Height + RoomAboveBox + RoomBelowBox));
        }, DispatcherPriority.Loaded);

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
            _control.AddHandler(InputElement.GotFocusEvent, OnGotFocus, RoutingStrategies.Bubble);
            _control.AttachedToVisualTree += OnAttachedToVisualTree;
            _control.DetachedFromVisualTree += OnDetachedFromVisualTree;
            if (TopLevel.GetTopLevel(_control) != null)
                Hook();
        }

        internal void Detach()
        {
            _control.RemoveHandler(InputElement.GotFocusEvent, OnGotFocus);
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

        private void OnStateChanged(object? sender, InputPaneStateEventArgs e)
        {
            Apply(e.NewState == InputPaneState.Open ? e.EndRect.Height : 0);
            if (e.NewState == InputPaneState.Open)
                BringFocusedBoxIntoView(_control);
        }

        // Another box taking the focus with the keyboard already up raises no
        // state change of the pane's, and may be one the keyboard covers.
        private void OnGotFocus(object? sender, FocusChangedEventArgs e)
        {
            if (e.Source is TextBox)
                BringFocusedBoxIntoView(_control);
        }

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
