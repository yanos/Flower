using System;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Flower.Persistence;
using Flower.Services;
using Flower.ViewModels;

namespace Flower.Views;

// Extends ViewModelBase (not a plain record) - Alias and IsEditing both need
// settable, change-notifying properties: Alias so the row's TextBox can bind
// it, IsEditing so the row can toggle between its plain-text display and the
// pencil-clicked edit state - see EditAliasButton_Click.
public sealed class TrustedPeerRow : ViewModelBase
{
    public required string Fingerprint { get; init; }

    private string _alias = "";
    public string Alias
    {
        get => _alias;
        set => SetProperty(ref _alias, value);
    }

    public required DateTimeOffset ApprovedAt { get; init; }
    public string ApprovedAtDisplay => $"Approved {ApprovedAt.LocalDateTime:g}";

    private bool _isEditing;
    public bool IsEditing
    {
        get => _isEditing;
        set => SetProperty(ref _isEditing, value);
    }
}

// Lists peers approved via the trust gate (see SyncHttpServer.AuthorizeAsync,
// SYNC-PLAN.md Phase 3) and lets the user revoke one - the "forget this
// device" action the plan calls for. Opened from the app menu
// (MainWindow.axaml), alongside Rebuild Database/Open App Data Location -
// infrequent/administrative actions kept out of the already-decluttered
// Settings dialog rather than added back into it.
public partial class TrustedDevicesWindow : Window
{
    private readonly TrustedPeerStore _store = new();
    private readonly MainViewModel _mainViewModel;

    public TrustedDevicesWindow(MainViewModel mainViewModel)
    {
        InitializeComponent();
        _mainViewModel = mainViewModel;
        Refresh();
        NativeMenuHelper.InheritFromMainWindow(this);
    }

    private void Refresh()
    {
        var nicknames = new DeviceNicknameStore();
        var rows = _store.Load()
            .OrderByDescending(p => p.ApprovedAt)
            // A local nickname (see DeviceNicknameStore - also editable from the
            // sidebar's "Rename Device" context menu) wins over the alias the
            // peer reported when it was first approved.
            .Select(p => new TrustedPeerRow
            {
                Fingerprint = p.Fingerprint,
                Alias = nicknames.Get(p.Fingerprint) ?? p.Alias,
                ApprovedAt = p.ApprovedAt,
            })
            .ToList();

        DevicesList.ItemsSource = rows;
        EmptyStateText.IsVisible = rows.Count == 0;
    }

    // Pencil icon click: not-yet-editing starts an edit (mirrors MainView.axaml.cs's
    // BeginRename - an already-realized row's IsVisible flip doesn't refire
    // Loaded, so the textbox needs focusing manually, via ContainerFromItem
    // rather than an index since these rows aren't otherwise tracked by
    // position); already-editing (now showing a checkmark instead) confirms.
    private void EditAliasButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TrustedPeerRow row })
            return;

        if (row.IsEditing)
        {
            _ = CommitAliasEdit(row);
            return;
        }

        row.IsEditing = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (DevicesList.ContainerFromItem(row) is Control container &&
                container.FindDescendantOfType<TextBox>() is { } tb)
            {
                tb.Focus();
                tb.SelectAll();
            }
        });
    }

    private void AliasTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        if (sender is not TextBox { DataContext: TrustedPeerRow row })
            return;

        e.Handled = true;
        _ = CommitAliasEdit(row);
    }

    // Also commits on LostFocus, not just Enter/the checkmark click - this is a
    // standalone dialog a user can plausibly edit-then-immediately-close
    // (native red traffic-light button, Cmd+W, etc.) without either of those
    // firing first, which is exactly what silently discarded a rename before
    // (looked applied for the rest of the session, but was never actually
    // written to disk, so it reverted to the old name on next launch).
    private void AliasTextBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: TrustedPeerRow row })
            _ = CommitAliasEdit(row);
    }

    private async Task CommitAliasEdit(TrustedPeerRow row)
    {
        if (!row.IsEditing)
            return;

        row.IsEditing = false;
        await new DeviceNicknameStore().SetAsync(row.Fingerprint, row.Alias);

        // Re-derives the displayed value from scratch - in particular, an
        // emptied-out field falls back to the peer's originally-approved
        // alias (see DeviceNicknameStore.SetAsync clearing the override on a
        // blank/whitespace name) rather than being left showing blank text.
        Refresh();

        // Without this, a rename made here only ever reaches the sidebar (and
        // the device-detail pane, which shares the same SidebarItem) once that
        // device happens to be mDNS-rediscovered again - which might not
        // happen again all session if it stays continuously connected. This
        // is the same single ResolveDeviceDisplayName-backed refresh
        // MainView.axaml.cs's own "Rename Device" context menu calls, so both
        // rename paths converge on one source of truth for the display name.
        _mainViewModel.RefreshDeviceDisplayNames();
    }

    private async void ForgetButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TrustedPeerRow row })
            return;

        var confirmed = await ConfirmDialogWindow.ShowAsync(
            this,
            "Forget This Device?",
            $"\"{row.Alias}\" will need to be approved again before it can sync with this device.",
            "Forget");
        if (!confirmed)
            return;

        await _store.RevokeAsync(row.Fingerprint);
        Refresh();
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();
}
