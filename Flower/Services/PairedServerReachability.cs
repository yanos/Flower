using System;
using System.Linq;

using Avalonia.Threading;

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
public class PairedServerReachability
{
    private readonly NetworkDiscoveryService _networkDiscovery;
    private readonly AppSettings _appSettings;

    public bool IsReachable { get; private set; }
    public DiscoveredDevice? PairedServerDevice { get; private set; }
    public string? PairedServerFingerprint => _appSettings.PairedServerFingerprint;

    public event EventHandler? Changed;

    public PairedServerReachability(NetworkDiscoveryService networkDiscovery, AppSettings appSettings)
    {
        _networkDiscovery = networkDiscovery;
        _appSettings = appSettings;
        networkDiscovery.DeviceDiscovered += (_, _) => Recompute();
        networkDiscovery.DeviceLost += (_, _) => Recompute();
    }

    // Call after any AppSettings mutation that can change PairedServerFingerprint
    // or IsServer (pairing, unpairing, a role flip) - AppSettings itself isn't
    // observable, so this is the one place callers must nudge explicitly rather
    // than relying on the next unrelated DeviceDiscovered/DeviceLost to notice.
    public void Recompute()
    {
        var device = _networkDiscovery.KnownDevices
            .FirstOrDefault(d => SyncRolePolicy.MayRequestFrom(_appSettings.PairedServerFingerprint, d.Fingerprint));
        var reachable = device != null;
        if (device == PairedServerDevice && reachable == IsReachable)
            return;
        PairedServerDevice = device;
        IsReachable = reachable;
        Dispatcher.UIThread.Post(() => Changed?.Invoke(this, EventArgs.Empty));
    }
}
