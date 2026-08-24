using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Threading;

using Microsoft.Extensions.Logging;

using Flower.Persistence;

namespace Flower.Services;

// The single source of truth for "is the Client's currently-paired Server
// reachable right now" - replaces what used to be independently computed in
// MainViewModel.IsPairedServerReachable (a live KnownDevices query),
// SidebarItem.IsReachable (imperatively pushed from four different
// MainViewModel methods), MainViewModel.FindDeviceByFingerprint (a third,
// slightly different definition), MainViewModel.ForceSyncNow's own
// KnownDevices lookup, and PeerTrackResolver's own FindByFingerprint call -
// all five kept in sync only by convention, which is exactly how a
// SearchSongResults-shaped row list ended up permanently stuck on a stale
// default (see git history around this class's introduction).
//
// Owns its own NetworkDiscoveryService.DeviceDiscovered/DeviceLost
// subscription (one, for the whole app, instead of every consumer
// subscribing separately) and marshals Changed onto the UI thread itself,
// since NetworkDiscoveryService's events fire off-thread - so every consumer
// gets an already UI-safe signal rather than each doing its own
// Dispatcher.UIThread.Post.
//
// It is also where a paired server stops being something we can only *find*
// and becomes something we can *reach*. This used to resolve the server purely
// out of KnownDevices, which mDNS alone populates - so reachability was
// literally defined as "discovered on this link right now", and a paired
// server vanished the moment its client left the house, with no address
// remembered to fall back on. Now the server reports its own addresses in the
// /info handshake, this class persists them for the one server we paired with,
// and probes them when discovery comes up empty. See
// docs/REMOTE-ACCESS-PLAN.md.
public class PairedServerReachability : IDisposable
{
    private readonly NetworkDiscoveryService _networkDiscovery;
    private readonly AppSettings _appSettings;
    private readonly AppSettingsStore _appSettingsStore;
    private readonly ILogger<PairedServerReachability> _logger;

    public bool IsReachable { get; private set; }
    public DiscoveredDevice? PairedServerDevice { get; private set; }
    public string? PairedServerFingerprint => _appSettings.PairedServerFingerprint;

    // How the paired server is currently being reached, for the UI to show. A
    // silent fallback from the LAN to a possibly-relayed tailnet path is
    // otherwise indistinguishable from a fast one, which makes "why has this
    // got slow" a question with no answer anywhere in the app.
    public ServerRoute Route { get; private set; } = ServerRoute.Unreachable;

    public event EventHandler? Changed;

    public PairedServerReachability(
        NetworkDiscoveryService networkDiscovery,
        AppSettings appSettings,
        AppSettingsStore appSettingsStore,
        ILogger<PairedServerReachability> logger)
    {
        _networkDiscovery = networkDiscovery;
        _appSettings = appSettings;
        _appSettingsStore = appSettingsStore;
        _logger = logger;
        _subscriptions.Add<EventHandler<DiscoveredDevice>>((_, e) => OnDeviceChanged(e),
            h => networkDiscovery.DeviceDiscovered += h, h => networkDiscovery.DeviceDiscovered -= h);
        _subscriptions.Add<EventHandler<string>>((_, _) => Recompute(),
            h => networkDiscovery.DeviceLost += h, h => networkDiscovery.DeviceLost -= h);
    }

    // Both events this class attaches to, paired with their teardown - see
    // SubscriptionBag, and docs/ARCHITECTURE-REVIEW.md Tier 2.3.
    private readonly SubscriptionBag _subscriptions = new();

    public void Dispose() => _subscriptions.Dispose();

    // Every address worth trying for the paired server: the ones it reported
    // for itself, plus any the user typed. Deduped, since a server on the LAN
    // will report the same address the user may have typed by hand.
    private IEnumerable<string> Candidates() =>
        _appSettings.PairedServerAddresses
            .Concat(_appSettings.ManualServerAddresses)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    // Registers every remembered address as a peer, so the ordinary poll loop
    // starts probing them. Call once at startup, after AppSettings has loaded -
    // this is what gives a client that has never seen an mDNS announcement in
    // this session anything at all to talk to.
    public async Task RestoreRememberedAsync()
    {
        foreach (var address in Candidates().ToList())
            await _networkDiscovery.AddRememberedAsync(address);

        Recompute();
    }

    // Learns from a handshake. Only the paired server's addresses are ever
    // persisted or probed, and only ones carried by a peer whose fingerprint
    // is the one we paired with - without that, an unauthenticated /info from
    // any peer on the network could aim this client's probes at hosts of its
    // choosing.
    private void OnDeviceChanged(DiscoveredDevice device)
    {
        if (SyncRolePolicy.MayRequestFrom(_appSettings.PairedServerFingerprint, device.Fingerprint)
            && device.IsResponding
            && device.Addresses.Count > 0)
        {
            RememberAddresses(device.Addresses);
        }

        Recompute();
    }

    private void RememberAddresses(IReadOnlyList<string> addresses)
    {
        // Replaced, not merged: an address the server has stopped reporting is
        // one it no longer has, and merging would leave us probing it forever.
        if (addresses.SequenceEqual(_appSettings.PairedServerAddresses, StringComparer.OrdinalIgnoreCase))
            return;

        _logger.LogInformation("Paired server reports {Count} address(es): {Addresses}",
            addresses.Count, string.Join(", ", addresses));

        // Dropped addresses are unregistered, not merely forgotten. A
        // remembered peer is exempt from the ordinary staleness pruning (see
        // DiscoveredDevice.IsRemembered), so replacing the persisted list
        // without this leaves one dead entry per address the server has ever
        // held, for the rest of the session - and each shows up as its own
        // never-resolving row, since an entry with no fingerprint cannot be
        // deduped against anything (see NetworkDiscoveryService.KnownDevices).
        // That is not hypothetical: a machine with a transient interface whose
        // ULA prefix is regenerated on every attach - an iPhone over USB, a VM
        // bridge - grows two rows every time it comes and goes.
        //
        // Addresses the user typed are left alone. They are not this server's
        // to withdraw, they are removed by the user via
        // PeerSyncCoordinator.RemoveManualServer, and they overlap with the
        // reported set often enough (a LAN address that is also bookmarked)
        // that ignoring the distinction would delete one out from under them.
        var dropped = _appSettings.PairedServerAddresses
            .Except(addresses, StringComparer.OrdinalIgnoreCase)
            .Except(_appSettings.ManualServerAddresses, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _appSettings.PairedServerAddresses = [.. addresses];
        _ = _appSettingsStore.SaveAsync(_appSettings);

        foreach (var address in dropped)
        {
            _logger.LogInformation("Paired server no longer reports {Address}; forgetting it", address);
            _networkDiscovery.RemoveRemembered(address);
        }

        // Newly-reported addresses are registered immediately rather than at
        // the next restart. This is the self-healing case: the server joins a
        // tailnet after we paired with it, and the very next handshake at home
        // teaches us the address we will need when we leave.
        foreach (var address in addresses)
            _ = _networkDiscovery.AddRememberedAsync(address);
    }

    // Call after any AppSettings mutation that can change PairedServerFingerprint
    // or IsServer (pairing, unpairing, a role flip) - AppSettings itself isn't
    // observable, so this is the one place callers must nudge explicitly rather
    // than relying on the next unrelated DeviceDiscovered/DeviceLost to notice.
    public void Recompute()
    {
        // KnownDevices already picks the best entry per peer - answering first,
        // then by route rank, so a live sighting wins over the LAN address,
        // which wins over the tailnet. See NetworkDiscoveryService.ReachRank.
        var device = _networkDiscovery.KnownDevices
            .FirstOrDefault(d => SyncRolePolicy.MayRequestFrom(_appSettings.PairedServerFingerprint, d.Fingerprint)
                                 && d.IsResponding);
        var reachable = device != null;
        var route = RouteOf(device);
        if (device == PairedServerDevice && reachable == IsReachable && route == Route)
            return;

        PairedServerDevice = device;
        IsReachable = reachable;
        Route = route;
        Dispatcher.UIThread.Post(() => Changed?.Invoke(this, EventArgs.Empty));
    }

    private static ServerRoute RouteOf(DiscoveredDevice? device) => device switch
    {
        null => ServerRoute.Unreachable,
        _ => NetworkDiscoveryService.ReachRank(device) switch
        {
            0 or 1 => ServerRoute.LocalNetwork,
            2 => ServerRoute.Tailnet,
            _ => ServerRoute.Remote,
        },
    };
}

// How the paired server is currently being reached. Ordered from best to
// worst, matching NetworkDiscoveryService.ReachRank.
public enum ServerRoute
{
    // Found on this link, or at one of its private-network addresses.
    LocalNetwork,

    // A 100.64.0.0/10 address - a tailnet. Works from anywhere, but may be
    // carried by a relay rather than a direct path, so it is worth telling the
    // user apart from the LAN.
    Tailnet,

    // Some other address it reported or the user typed.
    Remote,

    Unreachable,
}
