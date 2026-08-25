using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia;

using Flower.Services;
using Flower.ViewModels;

namespace Flower.Views;

// One row in ServerPickerView's list of discovered Servers - see
// MainViewModel.AvailableServers/PairedServerFingerprint. ActionLabel/
// IsActionEnabled/HintText encode the states a row can be in: the paired
// server, either still waiting on its approval ("Waiting for server...") or
// confirmed ("Unpair" - see IsTrustConfirmed/MainViewModel.
// IsPairedServerTrustConfirmed), a different server already paired
// (disabled, with a hint to unpair first - decision: switching requires an
// explicit unpair-first step, no direct one-click switch), or nothing is
// paired yet ("Ask to pair").
public sealed class ServerRow : ViewModelBase
{
    public required string Fingerprint { get; init; }
    public required string Alias { get; init; }
    public required bool IsPaired { get; init; }

    // What the user typed into this row's code box. Per-row rather than
    // per-view: the list can show several servers, and a code is only valid
    // for the one it was issued by.
    public string PairingCode
    {
        get => _pairingCode;
        set
        {
            if (_pairingCode == value)
                return;
            _pairingCode = value;
            OnPropertyChanged();
        }
    }
    private string _pairingCode = "";

    public bool IsPairingCodeRequired => !IsPaired;

    // True only for the paired row while MainViewModel.IsSyncing is set - see
    // ServerPickerView's PropertyChanged subscription, which re-runs Refresh()
    // (rebuilding this snapshot) on every IsSyncing edge.
    public required bool IsSyncing { get; init; }

    // True only for the paired row, once it has actually approved this
    // device - see MainViewModel.IsPairedServerTrustConfirmed. Meaningless
    // (always false) for any other row.
    public required bool IsTrustConfirmed { get; init; }

    // "Sync Now" is only ever shown on the paired row, and only enabled while
    // that server is actually currently discovered - see
    // MainViewModel.CanForceSync/ForceSyncNow.
    public required bool CanForceSync { get; init; }

    // Set to the currently-paired server's alias only when a DIFFERENT
    // server is paired - null otherwise (nothing paired, or this row itself
    // is the paired one).
    public required string? BlockedByAlias { get; init; }

    // Shown under the name only when the name alone does not identify the
    // row: two servers calling themselves the same thing. A row is otherwise
    // deliberately just a name - an origin is how this device happens to be
    // reaching the server right now, which is both changeable and none of the
    // user's business when there is nothing to tell apart. See Refresh.
    public string? Detail { get; init; }

    // When this device last successfully pulled from this server, already
    // phrased - see MainViewModel.LastSyncedDisplay, which mobile's settings
    // screen shows too. Only ever set on the paired row: there is nothing to
    // have synced with any other. Null until the first sync of a pairing
    // completes, which is exactly the window in which the row is still saying
    // "Waiting for server...".
    public string? LastSyncedDisplay { get; init; }

    public string ActionLabel =>
        !IsPaired ? "Pair" :
        IsTrustConfirmed ? "Unpair" :
        "Waiting for server...";
    public bool IsAwaitingApproval => IsPaired && !IsTrustConfirmed;
    public bool IsActionEnabled => IsPaired || BlockedByAlias == null;
    public string? HintText => !IsPaired && BlockedByAlias != null ? $"Unpair from {BlockedByAlias} first" : null;
}

// Client-side counterpart to TrustedDevicesView (shown instead of it on
// SettingsWindow's Devices tab when this device is a Client, not a Server -
// see SettingsWindow.RefreshDevicesTab): lets the user pick which one
// discovered Server to bulk-sync with, mirroring TrustedDevicesView's own
// injected/embedded-control pattern.
//
// Unlike TrustedDevicesView this one listens to two app-lifetime sources
// (mDNS discovery, and the ViewModel's sync/pairing state) while being itself
// transient - a fresh instance every time Settings opens, or the Server
// checkbox is toggled. Those subscriptions used to be attached in the
// constructor and never detached, so each dead instance went on rebuilding
// its own detached row list on every discovery packet for the rest of the
// process. They are attached/detached with the visual tree now - see
// docs/ARCHITECTURE-REVIEW.md Tier 2.3/4.2.
public partial class ServerPickerView : UserControl
{
    private readonly MainViewModel _mainViewModel;
    private readonly NetworkDiscoveryService? _networkDiscovery;
    private readonly SubscriptionBag _subscriptions = new();

    // Satisfies Avalonia's runtime-XAML-loader/previewer check (AVLN3001) -
    // never called directly; the real constructor below is what's used. Same
    // shape (and same pragma) as SettingsWindow, which hosts this control.
#pragma warning disable CS8618
    public ServerPickerView() => InitializeComponent();
#pragma warning restore CS8618

    public ServerPickerView(MainViewModel mainViewModel)
    {
        InitializeComponent();
        _mainViewModel    = mainViewModel;
        _networkDiscovery = mainViewModel.NetworkDiscovery;
        Refresh();
    }

    // TabControl detaches the content of a tab the user switches away from and
    // re-attaches the same instance on the way back, so this is a subscribe/
    // unsubscribe pair rather than a one-way teardown - a Dispose-on-detach
    // would leave the control alive but deaf the second time it is shown.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (_networkDiscovery != null)
        {
            _subscriptions.Add<EventHandler<DiscoveredDevice>>((_, _) => Dispatcher.UIThread.Post(Refresh),
                h => _networkDiscovery.DeviceDiscovered += h, h => _networkDiscovery.DeviceDiscovered -= h);
            _subscriptions.Add<EventHandler<string>>((_, _) => Dispatcher.UIThread.Post(Refresh),
                h => _networkDiscovery.DeviceLost += h, h => _networkDiscovery.DeviceLost -= h);
        }

        _subscriptions.Add<PropertyChangedEventHandler>((_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.IsSyncing)
                || args.PropertyName == nameof(MainViewModel.IsPairedServerTrustConfirmed)
                || args.PropertyName == nameof(MainViewModel.LastSyncedAt))
                Dispatcher.UIThread.Post(Refresh);
            if (args.PropertyName == nameof(MainViewModel.LastForceSyncResult))
                Dispatcher.UIThread.Post(RefreshSyncResultText);
        },
            h => _mainViewModel.PropertyChanged += h, h => _mainViewModel.PropertyChanged -= h);

        // Anything that changed while this control was detached (or before it
        // was first shown) is picked up here rather than being missed.
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _subscriptions.Dispose();
        base.OnDetachedFromVisualTree(e);
    }

    private void RefreshSyncResultText()
    {
        SyncResultText.Text = _mainViewModel.LastForceSyncResult;
        SyncResultText.IsVisible = !string.IsNullOrEmpty(_mainViewModel.LastForceSyncResult);
    }

    private void Refresh()
    {
        var pairedFingerprint = _mainViewModel.PairedServerFingerprint;
        var pairedAlias = _mainViewModel.PairedServerAlias;

        // Refresh() rebuilds every row from scratch and runs off the ~5s peer
        // poll, so a half-typed pairing code would be wiped out from under the
        // user mid-keystroke. Carry it across by fingerprint - the row objects
        // are snapshots, but what the user typed into one is not.
        var typedCodes = (ServersList.ItemsSource as IEnumerable<ServerRow>)?
            .Where(r => !string.IsNullOrEmpty(r.PairingCode))
            .ToDictionary(r => r.Fingerprint, r => r.PairingCode)
            ?? [];

        // One row per server, named. AvailableServers is already one entry per
        // identified server - deduped by fingerprint, with the addresses that
        // never answered filtered out (see PeerSyncCoordinator.AvailableServers
        // and NetworkDiscoveryService.KnownDevices) - so the only way two rows
        // can look alike is two genuinely different servers choosing the same
        // alias, which is entirely possible since an alias defaults to the
        // machine name. Those, and only those, get their origin underneath to
        // tell them apart. A manual address that never resolved is not lost by
        // being absent here: the box that added it reports its own success or
        // failure inline (see AddManualServerButton_Click).
        var servers = _mainViewModel.AvailableServers.ToList();

        var duplicateAliases = servers
            .GroupBy(d => d.Alias, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var rows = servers
            .Select(d => new ServerRow
            {
                Fingerprint = d.Fingerprint,
                Alias = string.IsNullOrWhiteSpace(d.Alias) ? d.Origin : d.Alias,
                Detail = duplicateAliases.Contains(d.Alias) ? d.Origin : null,
                IsPaired = d.Fingerprint == pairedFingerprint,
                IsSyncing = d.Fingerprint == pairedFingerprint && _mainViewModel.IsSyncing,
                IsTrustConfirmed = d.Fingerprint == pairedFingerprint && _mainViewModel.IsPairedServerTrustConfirmed,
                CanForceSync = d.Fingerprint == pairedFingerprint && _mainViewModel.CanForceSync,
                BlockedByAlias = pairedFingerprint != null && d.Fingerprint != pairedFingerprint ? pairedAlias : null,
                LastSyncedDisplay = d.Fingerprint == pairedFingerprint ? _mainViewModel.LastSyncedDisplay : null,
                PairingCode = typedCodes.GetValueOrDefault(d.Fingerprint, ""),
            })
            .ToList();

        // Pin the currently-paired server at the top even if it isn't
        // currently discovered (e.g. temporarily offline) - the display-only
        // cache on MainViewModel.PairedServerAlias exists for exactly this.
        if (pairedFingerprint != null && rows.All(r => r.Fingerprint != pairedFingerprint))
        {
            rows.Insert(0, new ServerRow
            {
                Fingerprint = pairedFingerprint,
                Alias = pairedAlias ?? pairedFingerprint,
                IsPaired = true,
                IsSyncing = _mainViewModel.IsSyncing,
                IsTrustConfirmed = _mainViewModel.IsPairedServerTrustConfirmed,
                CanForceSync = _mainViewModel.CanForceSync,
                BlockedByAlias = null,
                LastSyncedDisplay = _mainViewModel.LastSyncedDisplay,
            });
        }

        ServersList.ItemsSource = rows;
        EmptyStateText.IsVisible = rows.Count == 0;
    }

    private async void ActionButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ServerRow row })
            return;

        if (row.IsPaired)
        {
            if (TopLevel.GetTopLevel(this) is not Window owner)
                return;

            var confirmed = await ConfirmDialogWindow.ShowAsync(
                owner,
                $"Unpair From \"{row.Alias}\"?",
                $"This device will stop getting music and playlists from \"{row.Alias}\". You can still browse and play from it.",
                "Unpair");
            if (!confirmed)
                return;

            _mainViewModel.UnpairServer();
        }
        else
        {
            var device = _mainViewModel.AvailableServers.FirstOrDefault(d => d.Fingerprint == row.Fingerprint);
            if (device == null)
                return;

            if (string.IsNullOrWhiteSpace(row.PairingCode))
                return;

            await PairWithAsync(device, row.Alias, row.PairingCode.Trim());
            return;
        }

        Refresh();
    }

    private void ForceSyncButton_Click(object? sender, RoutedEventArgs e) => _mainViewModel.ForceSyncNow();

    // The bootstrap path: a server this device has never shared a network with
    // cannot appear in the list above, so it cannot be paired with there. This
    // one button does both halves - resolve the typed address, then redeem the
    // typed code against what answered - because splitting them left the user
    // pressing "Add" and then hunting for the row that had just appeared.
    //
    // Adding an address is a DNS lookup plus an /info round trip, so the button
    // is disabled for the duration rather than left clickable - a second click
    // would otherwise queue a duplicate probe of the same host.
    private async void AddManualServerButton_Click(object? sender, RoutedEventArgs e)
    {
        var address = ManualAddressBox.Text?.Trim() ?? "";
        if (address.Length == 0)
            return;

        var code = ManualPairingCodeBox.Text?.Trim() ?? "";

        AddManualServerButton.IsEnabled = false;
        ManualAddressStatus.IsVisible = true;
        ManualAddressStatus.Text = "Looking for a server...";
        DiscoveredDevice? found;
        try
        {
            found = await _mainViewModel.AddManualServerAsync(address);
        }
        finally
        {
            AddManualServerButton.IsEnabled = true;
        }

        // The address is kept either way: a server that merely happens to be
        // switched off right now is still the server the user meant. Saying
        // which happened is the useful part, since a typo is by far the likelier
        // of the two and is worth catching here rather than from a coffee shop.
        if (found == null)
        {
            ManualAddressStatus.Text =
                $"Nothing answered at {address}. It is saved anyway - check the address, and that both ends are on the tailnet.";
            Refresh();
            return;
        }

        ManualAddressBox.Text = "";

        // No code typed: the server is now in the list above, where its own row
        // has a code box of its own. Nothing was lost, so say what to do next
        // rather than reporting a failure.
        if (code.Length == 0)
        {
            ManualAddressStatus.Text = $"Found {found.Alias}. Enter its pairing code on its row above.";
            Refresh();
            return;
        }

        ManualAddressStatus.Text = "";
        ManualAddressStatus.IsVisible = false;
        ManualPairingCodeBox.Text = "";
        Refresh();

        await PairWithAsync(found, found.Alias, code);
    }

    // The pair confirmation, shared by the list's own Pair button and the
    // address box below it - the warning is about what pairing does to this
    // device's library view, which is the same either way.
    //
    // Nothing is being asked of the server here: the admin-issued code the user
    // just typed *is* the authorization, so the copy says so rather than
    // promising an approval that will never be prompted for.
    private async Task PairWithAsync(DiscoveredDevice device, string alias, string pairingCode)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return;

        var confirmed = await ConfirmDialogWindow.ShowAsync(
            owner,
            $"Pair With \"{alias}\"?",
            $"Your Songs and Albums will show \"{alias}\"'s music instead of this device's own. Nothing on this device gets deleted. "
            + "The code you typed lets this device in straight away - there is nothing to approve.",
            "Pair");
        if (!confirmed)
            return;

        _mainViewModel.PairWithServer(device, pairingCode);
        Refresh();
    }
}
