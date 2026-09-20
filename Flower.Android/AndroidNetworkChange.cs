using System;

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
public sealed class AndroidNetworkChange : INetworkChangeSource
{
    public event Action? Changed;

    public AndroidNetworkChange(Context context)
    {
        var connectivity = (ConnectivityManager)context.ApplicationContext!.GetSystemService(Context.ConnectivityService)!;
        connectivity.RegisterDefaultNetworkCallback(new Callback(this));
    }

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
