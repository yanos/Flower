using System;
using System.Linq;
using System.Net;

using Android.Content;
using Android.Net;

using Flower.Services;

namespace Flower.Android;

// Network changes on Android, from ConnectivityManager's default-network
// callback: the network the app's traffic goes out on changed (Wi-Fi to
// cellular, a VPN such as Tailscale coming up) or what it can reach did. See
// INetworkChangeSource for what the app does with it.
//
// Not .NET's own NetworkChange, which listens on a netlink socket Android
// stops apps from binding from API 30.
//
// Link-property changes come often on a phone network - a DNS server, a
// rotated temporary address, a carrier's P-CSCF - and are passed on all the
// same; NetworkChangeFilter compares DescribeNetwork before and after and
// drops the ones that moved nothing.
public sealed class AndroidNetworkChange : INetworkChangeSource
{
    private readonly ConnectivityManager _connectivity;

    public event Action? Changed;

    public AndroidNetworkChange(Context context)
    {
        _connectivity = (ConnectivityManager)context.ApplicationContext!.GetSystemService(Context.ConnectivityService)!;
        _connectivity.RegisterDefaultNetworkCallback(new Callback(this));
    }

    // The default network's interface, its addresses and the gateways its
    // routes go through.
    public string? DescribeNetwork()
    {
        try
        {
            if (_connectivity.ActiveNetwork is not { } network)
                return "none";
            if (_connectivity.GetLinkProperties(network) is not { } link)
                return null;

            var addresses = link.LinkAddresses
                .Select(a => a.Address?.HostAddress)
                .Where(a => a != null && IPAddress.TryParse(StripZone(a), out var ip) && NetworkDescription.Counts(ip))
                .Select(a => a!);
            var gateways = link.Routes
                .Select(r => r.Gateway)
                .Where(g => g is { IsAnyLocalAddress: false })
                .Select(g => g!.HostAddress!);

            return NetworkDescription.Of([new NetworkDescription.Interface(link.InterfaceName ?? "?", addresses, gateways)]);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // An IPv6 address as Java writes it can carry its interface ("fe80::1%wlan0").
    private static string StripZone(string address) =>
        address.IndexOf('%') is var at and >= 0 ? address[..at] : address;

    private sealed class Callback(AndroidNetworkChange owner) : ConnectivityManager.NetworkCallback
    {
        // The callback reports the current default network as soon as it is
        // registered. That describes where the device already is, not a move,
        // so it is not passed on.
        private bool _seenFirst;

        public override void OnAvailable(Network network)
        {
            if (!_seenFirst)
            {
                _seenFirst = true;
                return;
            }

            owner.Changed?.Invoke();
        }

        public override void OnLost(Network network) => owner.Changed?.Invoke();

        // The same network gaining or losing an address - DHCP handing out a
        // new lease, a VPN adding its interface's routes.
        public override void OnLinkPropertiesChanged(Network network, LinkProperties linkProperties)
        {
            if (_seenFirst)
                owner.Changed?.Invoke();
        }
    }
}
