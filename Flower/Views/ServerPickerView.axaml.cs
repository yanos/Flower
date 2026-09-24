using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
//
// A live object keyed by Fingerprint rather than a snapshot rebuilt on every
// change, which is what it used to be. Refresh() runs off mDNS discovery, the
// ~5s peer poll and every sync edge, so a snapshot row was replaced several
// times a minute - and replacing the row replaces the controls bound to it.
// That cost two things that live on a control instance rather than in the
// ViewModel: a half-typed pairing code, which Refresh had to carry across by
// hand, and ConnectionStatusIcon's minimum-length spinner hold, which it could
// not. A sync short enough to need the hold ends by setting LastSyncedAt, so
// the rebuild that discarded the holder arrived on the same edge that started
// it, and the spinner blinked out in the settings list while the sidebar - whose
// rows are not rebuilt - held it for the full second. One server, two answers.
public sealed class ServerRow : ViewModelBase
{
    // The identity. Everything else about a row is a fact that can change
    // under it while it stays the same row.
    public required string Fingerprint { get; init; }

    public required string Alias
    {
        get => _alias;
        set => SetProperty(ref _alias, value);
    }
    private string _alias = "";

    public required bool IsPaired
    {
        get => _isPaired;
        set
        {
            if (!SetProperty(ref _isPaired, value))
                return;
            OnPropertyChanged(nameof(IsPairingCodeRequired));
            NotifyActionState();
        }
    }
    private bool _isPaired;

    // What the user typed into this row's code box. Per-row rather than
    // per-view: the list can show several servers, and a code is only valid
    // for the one it was issued by.
    public string PairingCode
    {
        get => _pairingCode;
        set => SetProperty(ref _pairingCode, value);
    }
    private string _pairingCode = "";

    public bool IsPairingCodeRequired => !IsPaired;

    // True only for the paired row while MainViewModel.IsSyncing is set.
    public required bool IsSyncing
    {
        get => _isSyncing;
        set
        {
            if (SetProperty(ref _isSyncing, value))
                OnPropertyChanged(nameof(IsBusy));
        }
    }
    private bool _isSyncing;

    // True only for the paired row, once it has actually approved this
    // device - see MainViewModel.IsPairedServerTrustConfirmed. Meaningless
    // (always false) for any other row.
    public required bool IsTrustConfirmed
    {
        get => _isTrustConfirmed;
        set
        {
            if (SetProperty(ref _isTrustConfirmed, value))
                NotifyActionState();
        }
    }
    private bool _isTrustConfirmed;

    // "Sync Now" is only ever shown on the paired row, and only enabled while
    // that server is actually currently discovered - see
    // MainViewModel.CanForceSync/ForceSyncNow.
    public required bool CanForceSync
    {
        get => _canForceSync;
        set => SetProperty(ref _canForceSync, value);
    }
    private bool _canForceSync;

    // Set to the currently-paired server's alias only when a DIFFERENT
    // server is paired - null otherwise (nothing paired, or this row itself
    // is the paired one).
    public required string? BlockedByAlias
    {
        get => _blockedByAlias;
        set
        {
            if (!SetProperty(ref _blockedByAlias, value))
                return;
            OnPropertyChanged(nameof(IsActionEnabled));
            OnPropertyChanged(nameof(HintText));
        }
    }
    private string? _blockedByAlias;

    // Shown under the name only when the name alone does not identify the
    // row: two servers calling themselves the same thing. A row is otherwise
    // deliberately just a name - an origin is how this device happens to be
    // reaching the server right now, which is both changeable and none of the
    // user's business when there is nothing to tell apart. See Refresh.
    public string? Detail
    {
        get => _detail;
        set => SetProperty(ref _detail, value);
    }
    private string? _detail;

    // When this device last successfully pulled from this server, already
    // phrased - see MainViewModel.LastSyncedDisplay, which mobile's settings
    // screen shows too. Only ever set on the paired row: there is nothing to
    // have synced with any other. Null until the first sync of a pairing
    // completes, which is exactly the window in which the row is still saying
    // "Waiting for server...".
    public string? LastSyncedDisplay
    {
        get => _lastSyncedDisplay;
        set => SetProperty(ref _lastSyncedDisplay, value);
    }
    private string? _lastSyncedDisplay;

    public string ActionLabel =>
        !IsPaired ? "Pair" :
        IsTrustConfirmed ? "Unpair" :
        "Waiting for server...";
    public bool IsAwaitingApproval => IsPaired && !IsTrustConfirmed;

    // Waiting for approval and syncing are one state to the status glyph, which
    // shows a single icon - see ConnectionStatusIcon, which exists because this
    // row used to show a lit check and a spinner side by side.
    public bool IsBusy => IsAwaitingApproval || IsSyncing;
    public bool IsActionEnabled => IsPaired || BlockedByAlias == null;
    public string? HintText => !IsPaired && BlockedByAlias != null ? $"Unpair from {BlockedByAlias} first" : null;

    private void NotifyActionState()
    {
        OnPropertyChanged(nameof(ActionLabel));
        OnPropertyChanged(nameof(IsAwaitingApproval));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsActionEnabled));
        OnPropertyChanged(nameof(HintText));
    }
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
        ServersList.ItemsSource = _rows;
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
            {
                Dispatcher.UIThread.Post(Refresh);
            }
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

    // The rows on screen, kept and updated rather than replaced - see
    // ServerRow's own remarks for what a replacement costs. Handed to the
    // ListBox once, so a Refresh never swaps the ItemsSource out from under it.
    private readonly ObservableCollection<ServerRow> _rows = new();

    private void Refresh()
    {
        var pairedFingerprint = _mainViewModel.PairedServerFingerprint;
        var pairedAlias = _mainViewModel.PairedServerAlias;

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

        var wanted = servers
            .Select(d => (
                Fingerprint: d.Fingerprint,
                Alias: string.IsNullOrWhiteSpace(d.Alias) ? d.Origin : d.Alias,
                Detail: duplicateAliases.Contains(d.Alias) ? d.Origin : null))
            .ToList();

        // Pin the currently-paired server at the top even if it isn't
        // currently discovered (e.g. temporarily offline) - the display-only
        // cache on MainViewModel.PairedServerAlias exists for exactly this.
        if (pairedFingerprint != null && wanted.All(r => r.Fingerprint != pairedFingerprint))
        {
            wanted.Insert(0, (
                Fingerprint: pairedFingerprint,
                Alias: pairedAlias ?? pairedFingerprint,
                Detail: (string?)null));
        }

        // Reconcile by fingerprint: a server that was already listed keeps the
        // row object it had, and with it the control instances bound to it.
        for (var i = 0; i < wanted.Count; i++)
        {
            var want = wanted[i];
            var existing = _rows.FirstOrDefault(r => r.Fingerprint == want.Fingerprint);
            if (existing == null)
            {
                existing = new ServerRow
                {
                    Fingerprint = want.Fingerprint,
                    Alias = want.Alias,
                    IsPaired = false,
                    IsSyncing = false,
                    IsTrustConfirmed = false,
                    CanForceSync = false,
                    BlockedByAlias = null,
                };
                _rows.Insert(Math.Min(i, _rows.Count), existing);
            }
            else if (_rows.IndexOf(existing) != i)
            {
                _rows.Move(_rows.IndexOf(existing), i);
            }

            var isPaired = want.Fingerprint == pairedFingerprint;
            existing.Alias = want.Alias;
            existing.Detail = want.Detail;
            existing.IsPaired = isPaired;
            existing.IsSyncing = isPaired && _mainViewModel.IsSyncing;
            existing.IsTrustConfirmed = isPaired && _mainViewModel.IsPairedServerTrustConfirmed;
            existing.CanForceSync = isPaired && _mainViewModel.CanForceSync;
            existing.BlockedByAlias = !isPaired && pairedFingerprint != null ? pairedAlias : null;
            existing.LastSyncedDisplay = isPaired ? _mainViewModel.LastSyncedDisplay : null;
        }

        for (var i = _rows.Count - 1; i >= wanted.Count; i--)
        {
            _rows.RemoveAt(i);
        }

        EmptyStateText.IsVisible = _rows.Count == 0;
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
                MainViewModel.UnpairConsequences(row.Alias),
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
