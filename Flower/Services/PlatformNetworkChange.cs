using System;
using System.Net.NetworkInformation;

namespace Flower.Services
{
    // "The network this device is on just changed" - Wi-Fi dropped and the
    // phone fell back to cellular, a VPN came up, the laptop was docked.
    //
    // Without it the only way to learn the network had moved was for a request
    // to the old address to fail, and then for it to fail twice more before
    // discovery gave up on it (NetworkDiscoveryService's
    // MaxConsecutiveResolveFailures): a server that was reachable the whole
    // time over the tailnet sat unused for 15-25s while a track went silent.
    // The OS knows the moment it happens; this is how it gets told.
    //
    // Raised on whatever thread the platform reports on, possibly several times
    // for one change (an interface going down, then routes, then DNS) -
    // consumers coalesce rather than assume one event per change.
    //
    // A report is a hint, not a verdict: every platform's signal has been seen
    // to fire for changes that move nothing, so what is acted on is whether
    // DescribeNetwork reads differently afterwards (NetworkChangeFilter).
    public interface INetworkChangeSource
    {
        event Action? Changed;

        // The network as it is now, as one line that reads the same for as
        // long as the network is the same (NetworkDescription writes it, where
        // the platform can list its addresses); null when it cannot be told,
        // which counts as a change.
        string? DescribeNetwork();
    }

    // Set by a platform entry point before Avalonia starts, the same way as
    // PlatformMulticastLock: iOS (NWPathMonitor) and Android
    // (ConnectivityManager) each have a first-party signal, and .NET's own
    // NetworkChange is not one to trust on either - Android restricts the
    // netlink socket it listens on, and iOS has none of the SystemConfiguration
    // store it uses on macOS. Left null on the desktops, which get
    // DotNetNetworkChangeSource below.
    public static class PlatformNetworkChange
    {
        public static INetworkChangeSource? Current { get; set; }
    }

    // The desktops' answer, where .NET's own listener is backed by the OS
    // properly: SystemConfiguration on macOS, netlink on Linux,
    // NotifyAddrChange on Windows.
    public sealed class DotNetNetworkChangeSource : INetworkChangeSource
    {
        public event Action? Changed;

        public DotNetNetworkChangeSource()
        {
            NetworkChange.NetworkAddressChanged += (_, _) => Changed?.Invoke();
            NetworkChange.NetworkAvailabilityChanged += (_, _) => Changed?.Invoke();
        }

        public string? DescribeNetwork() => NetworkDescription.OfThisMachine();
    }
}
