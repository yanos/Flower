using System.Linq;

using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Flower.Services;

namespace Flower.Views.Mobile;

public partial class SettingsView : UserControl
{
    private IInputPane? _pane;

    public SettingsView()
    {
        InitializeComponent();

        VersionTextBlock.Text = $"Version {AppVersion.Display}";

        AttachedToVisualTree += (_, _) => Hook();
        DetachedFromVisualTree += (_, _) => Unhook();
    }

    // SoftKeyboard.AvoidOcclusion on the scroller ends the viewport above the
    // keyboard, but nothing then moves the box being typed into up into what
    // is left of it - the server address field sits near the bottom of a long
    // sheet, so it stays exactly where the keyboard now is. Scrolling it into
    // view is this half.
    private void Hook()
    {
        if (_pane != null)
            return;

        _pane = TopLevel.GetTopLevel(this)?.InputPane;
        if (_pane != null)
            _pane.StateChanged += OnInputPaneStateChanged;
    }

    private void Unhook()
    {
        if (_pane == null)
            return;

        _pane.StateChanged -= OnInputPaneStateChanged;
        _pane = null;
    }

    private void OnInputPaneStateChanged(object? sender, InputPaneStateEventArgs e)
    {
        if (e.NewState != InputPaneState.Open)
            return;

        // The scroller's own inset is applied from this same event, so the
        // viewport is still full height right now. Loaded priority runs after
        // the layout pass that shrinks it.
        Dispatcher.UIThread.Post(() =>
        {
            if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not Control focused)
                return;
            if (!focused.GetVisualAncestors().Contains(ContentScroller))
                return;

            // A rect taller than the box itself, so it lands clear of the
            // keyboard's edge rather than flush against it - the hint line
            // under the field is part of what is being read while typing.
            focused.BringIntoView(new(0, 0, focused.Bounds.Width, focused.Bounds.Height + 48));
        }, DispatcherPriority.Loaded);
    }

    // Copying is how a code reaches the device being paired - see the button's
    // own comment in the XAML.
    private void CopyPairingCodeButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.Mobile.MobileMainViewModel vm || vm.Main.IssuedPairingCode is not { Length: > 0 } code)
            return;

        _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(code);
    }

    private void CopyPairingInviteButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ViewModels.Mobile.MobileMainViewModel vm || vm.Main.IssuedPairingInvite is not { Length: > 0 } invite)
            return;

        _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(invite);
    }
}
