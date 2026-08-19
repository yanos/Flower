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

// A fingerprint this device explicitly denied (or let time out unanswered -
// see SyncHttpServer.RequestApprovalAsync) rather than approved - lets the
// user see who got turned away and forget that refusal, so a since-legitimate
// device isn't left permanently unable to re-request (denials aren't
// re-prompted from scratch, but nothing here blocks a fresh pair-request
// either way - this list is purely visibility/cleanup, see
// TrustedPeerStore.DenyAsync's own doc comment). No rename affordance - a
// denied peer has no ongoing relationship to nickname.
public sealed class DeniedPeerRow
{
    public required string Fingerprint { get; init; }
    public required string Alias { get; init; }
    public required DateTimeOffset DeniedAt { get; init; }
    public string DeniedAtDisplay => $"Denied {DeniedAt.LocalDateTime:g}";
}

// Lists peers approved via the trust gate (see SyncHttpServer.AuthorizeAsync,
// SYNC-PLAN.md Phase 3) and lets the user revoke one - the "forget this
// device" action the plan calls for. Embedded directly in Settings' Devices
// tab (SettingsWindow.axaml) rather than its own separate window, and built in
// code (SettingsWindow.RefreshDevicesTab) rather than declared in XAML - which
// is what lets it take what it needs as a constructor parameter instead of
// reaching into Ioc.Default for each one, as it used to. It asks for the one
// MainViewModel and reads the rest off that; see docs/ARCHITECTURE-REVIEW.md
// Tier 2.3.
//
// Forgetting a device here doesn't need to actively notify it (compare an
// earlier version of this method, which POSTed to the forgotten device
// directly) - the forgotten device finds out the same way regardless of
// whether it's currently on the network or not, via NetworkDiscoveryService's
// own periodic /info re-poll of every known peer noticing this device no
// longer trusts it - see DiscoveredDevice.TrustsUs.
public partial class TrustedDevicesView : UserControl
{
    private readonly TrustedPeerStore _store;
    private readonly DeviceNicknameStore _nicknames;
    private readonly MainViewModel _mainViewModel;
    private readonly PeerUnpairNotifier? _unpairNotifier;

    // Satisfies Avalonia's runtime-XAML-loader/previewer check (AVLN3001) -
    // never called directly; the real constructor below is what's used. Same
    // shape (and same pragma) as SettingsWindow, which hosts this control.
#pragma warning disable CS8618
    public TrustedDevicesView() => InitializeComponent();
#pragma warning restore CS8618

    public TrustedDevicesView(MainViewModel mainViewModel)
    {
        InitializeComponent();
        _mainViewModel  = mainViewModel;
        _store          = mainViewModel.TrustedPeers;
        _nicknames      = mainViewModel.DeviceNicknames;
        _unpairNotifier = mainViewModel.PeerUnpair;
        Refresh();
    }

    private void Refresh()
    {
        var rows = _store.Load()
            .OrderByDescending(p => p.ApprovedAt)
            // A local nickname (see DeviceNicknameStore - also editable from the
            // sidebar's "Rename Device" context menu) wins over the alias the
            // peer reported when it was first approved.
            .Select(p => new TrustedPeerRow
            {
                Fingerprint = p.Fingerprint,
                Alias = _nicknames.Get(p.Fingerprint) ?? p.Alias,
                ApprovedAt = p.ApprovedAt,
            })
            .ToList();

        DevicesList.ItemsSource = rows;
        EmptyStateText.IsVisible = rows.Count == 0;

        var deniedRows = _store.LoadDenied()
            .OrderByDescending(p => p.DeniedAt)
            .Select(p => new DeniedPeerRow { Fingerprint = p.Fingerprint, Alias = p.Alias, DeniedAt = p.DeniedAt })
            .ToList();

        DeniedDevicesList.ItemsSource = deniedRows;
        DeniedDevicesList.IsVisible = deniedRows.Count > 0;
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

    // Also commits on LostFocus, not just Enter/the checkmark click - this
    // control can plausibly be edited then immediately dismissed (closing
    // Settings via Cancel/OK, Cmd+W, the native red traffic-light button)
    // without either of those firing first, which is exactly what silently
    // discarded a rename before (looked applied for the rest of the session,
    // but was never actually written to disk, so it reverted to the old name
    // on next launch).
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
        await _nicknames.SetAsync(row.Fingerprint, row.Alias);

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
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return;

        var confirmed = await ConfirmDialogWindow.ShowAsync(
            owner,
            "Forget This Device?",
            $"\"{row.Alias}\" will need to be approved again before it can sync with this device.",
            "Forget");
        if (!confirmed)
            return;

        await _store.RevokeAsync(row.Fingerprint);
        // Best-effort - lets the peer clear its own stale pairing proactively
        // if it's currently reachable; harmless no-op otherwise, since it
        // falls back to discovering the revoke passively either way (see
        // PeerUnpairNotifier's own doc comment).
        _unpairNotifier?.NotifyFireAndForget(row.Fingerprint);
        Refresh();
    }

    private async void ForgetRefusalButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DeniedPeerRow row })
            return;

        await _store.ForgetDenialAsync(row.Fingerprint);
        Refresh();
    }
}
