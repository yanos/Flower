using System;

using CoreFoundation;

using Network;

using Flower.Services;

namespace Flower.iOS;

// Network changes on iOS, from NWPathMonitor - Apple's own answer to "has the
// route out of this device changed", and the one that sees Wi-Fi giving way to
// cellular, or a VPN such as Tailscale coming up, as it happens. See
// INetworkChangeSource for what the app does with it.
public sealed class AppleNetworkChange : INetworkChangeSource
{
    private readonly NWPathMonitor _monitor = new();
    private bool _seenFirst;

    public event Action? Changed;

    public AppleNetworkChange()
    {
        _monitor.SnapshotHandler = _ =>
        {
            // The monitor reports the current path the moment it starts. That
            // describes where the device already is, not a move, so it is not
            // passed on.
            if (!_seenFirst)
            {
                _seenFirst = true;
                return;
            }

            Changed?.Invoke();
        };
        _monitor.SetQueue(new DispatchQueue("flower.network-change"));
        _monitor.Start();
    }
}
