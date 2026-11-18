using System;
using System.Collections.Generic;

using CoreFoundation;

using Microsoft.Extensions.Logging;

using Network;

using Flower.Logging;
using Flower.Services;

namespace Flower.iOS;

// Network changes on iOS, from NWPathMonitor - Apple's own answer to "has the
// route out of this device changed", and the one that sees Wi-Fi giving way to
// cellular, or a VPN such as Tailscale coming up, as it happens. See
// INetworkChangeSource for what the app does with it.
//
// The monitor also re-reports a path that has not changed - on cellular every
// few seconds, for hours. Every report is passed on; NetworkChangeFilter is
// what tells a move from a repeat, by comparing DescribeNetwork before and
// after, and what logs how many repeats there were.
public sealed class AppleNetworkChange : INetworkChangeSource
{
    // Built before the container exists (AppDelegate wires this before
    // Avalonia starts), so it cannot be injected; the same as
    // BonjourMdnsBackend's.
    private static readonly ILogger Logger =
        AppLogging.CreateLogger(typeof(AppleNetworkChange).FullName!);

    private readonly NWPathMonitor _monitor = new();
    private bool _seenFirst;

    // The path the monitor last reported, as DescribeNetwork answers it.
    // Written on the monitor's own queue, read from wherever the filter runs.
    private volatile string? _path;

    public event Action? Changed;

    public AppleNetworkChange()
    {
        _monitor.SnapshotHandler = path =>
        {
            _path = Describe(path);

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

    public string? DescribeNetwork() => _path;

    private static string? Describe(NWPath path)
    {
        try
        {
            return Summarize(path);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Could not describe the network path");
            return null;
        }
    }

    // NWPath says which interfaces carry the route and through which
    // gateways, not which addresses they hold - so this is iOS's own line
    // rather than NetworkDescription.Of's. It reads the same for as long as
    // the path is the same, which is all the filter asks of it.
    private static string Summarize(NWPath path)
    {
        var interfaces = new List<string>();
        path.EnumerateInterfaces(nwInterface =>
        {
            interfaces.Add($"{nwInterface.InterfaceType}:{nwInterface.Name}");
            return true;
        });

        // The gateways, because moving from one Wi-Fi network to another keeps
        // the interface (Wifi:en0) and changes only the router behind it -
        // which is the move that takes a LAN address out of reach.
        var gateways = new List<string>();
        path.EnumerateGateways(gateway =>
        {
            gateways.Add($"{gateway.Hostname}:{gateway.Port}");
            return true;
        });
        gateways.Sort(StringComparer.Ordinal);

        var families = (path.HasIPV4 ? "v4" : "") + (path.HasIPV6 ? "v6" : "");
        return $"Status={path.Status} Interfaces=[{string.Join(", ", interfaces)}] Gateways=[{string.Join(", ", gateways)}] "
            + $"Expensive={path.IsExpensive} IP={(families.Length == 0 ? "none" : families)} Dns={path.HasDns}";
    }
}
