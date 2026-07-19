using System.Linq;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

using CommunityToolkit.Mvvm.DependencyInjection;

using Flower.Services;
using Flower.ViewModels;

namespace Flower.Views;

// One row in ServerPickerView's list of discovered Servers - see
// MainViewModel.AvailableServers/PairedServerFingerprint. ActionLabel/
// IsActionEnabled/HintText encode the three states a row can be in: this is
// the paired server ("Unpair"), a different server is already paired
// (disabled, with a hint to unpair first - decision: switching requires an
// explicit unpair-first step, no direct one-click switch), or nothing is
// paired yet ("Pair").
public sealed class ServerRow : ViewModelBase
{
    public required string Fingerprint { get; init; }
    public required string Alias { get; init; }
    public required bool IsPaired { get; init; }

    // Set to the currently-paired server's alias only when a DIFFERENT
    // server is paired - null otherwise (nothing paired, or this row itself
    // is the paired one).
    public required string? BlockedByAlias { get; init; }

    public string ActionLabel => IsPaired ? "Unpair" : "Pair";
    public bool IsActionEnabled => IsPaired || BlockedByAlias == null;
    public string? HintText => !IsPaired && BlockedByAlias != null ? $"Unpair from {BlockedByAlias} first" : null;
}

// Client-side counterpart to TrustedDevicesView (shown instead of it on
// SettingsWindow's Devices tab when this device is a Client, not a Server -
// see SettingsWindow.RefreshDevicesTab): lets the user pick which one
// discovered Server to bulk-sync with, mirroring TrustedDevicesView's own
// service-locator/embedded-control pattern.
public partial class ServerPickerView : UserControl
{
    private readonly MainViewModel _mainViewModel = Ioc.Default.GetService<MainViewModel>()!;
    private readonly NetworkDiscoveryService _networkDiscovery = Ioc.Default.GetService<NetworkDiscoveryService>()!;

    public ServerPickerView()
    {
        InitializeComponent();
        Refresh();
        _networkDiscovery.DeviceDiscovered += (_, _) => Dispatcher.UIThread.Post(Refresh);
        _networkDiscovery.DeviceLost += (_, _) => Dispatcher.UIThread.Post(Refresh);
    }

    private void Refresh()
    {
        var pairedFingerprint = _mainViewModel.PairedServerFingerprint;
        var pairedAlias = _mainViewModel.PairedServerAlias;

        var rows = _mainViewModel.AvailableServers
            .Select(d => new ServerRow
            {
                Fingerprint = d.Fingerprint,
                Alias = d.Alias,
                IsPaired = d.Fingerprint == pairedFingerprint,
                BlockedByAlias = pairedFingerprint != null && d.Fingerprint != pairedFingerprint ? pairedAlias : null,
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
                BlockedByAlias = null,
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
                $"This device will no longer bulk-sync library/playlist data with \"{row.Alias}\". Browsing and streaming will still work.",
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

            if (TopLevel.GetTopLevel(this) is not Window owner)
                return;

            // Pairing switches this device's own Songs/Albums view over to
            // what it syncs in from the server (see SettingsWindow's
            // CanManageLocalLibrary - the Library tab disables once paired) -
            // worth a clear warning before it happens, and an explicit
            // reassurance that this is only about the synced *view*, not
            // about deleting anything already on disk.
            var confirmed = await ConfirmDialogWindow.ShowAsync(
                owner,
                $"Pair With \"{row.Alias}\"?",
                $"This device's library view will be replaced by \"{row.Alias}\"'s - your Songs/Albums list will show its library instead of managing its own. Your existing music files on this device will not be deleted.",
                "Pair");
            if (!confirmed)
                return;

            _mainViewModel.PairWithServer(device);
        }

        Refresh();
    }
}
