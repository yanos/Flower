using Android.Content;
using Android.Net.Wifi;

using Microsoft.Extensions.Logging;

using Flower.Logging;
using Flower.Services;

namespace Flower.Android;

// Android drops incoming multicast packets by default to save battery, which
// silently breaks mDNS discovery unless a WifiManager.MulticastLock is held for
// the duration - see https://developer.android.com/reference/android/net/wifi/WifiManager.MulticastLock.
public class AndroidMulticastLockHolder : IMulticastLockHolder
{
    private readonly WifiManager.MulticastLock _lock;

    // Constructed by the Android head before the DI container exists, so the
    // static hatch rather than an injected ILogger<T>.
    private static readonly ILogger Logger =
        AppLogging.CreateLogger(typeof(AndroidMulticastLockHolder).FullName!);

    public AndroidMulticastLockHolder(Context context)
    {
        var wifiManager = (WifiManager)context.ApplicationContext!.GetSystemService(Context.WifiService)!;
        _lock = wifiManager.CreateMulticastLock("flower-mdns")!;
    }

    // Logged because of how this fails: without the lock, mDNS does not break
    // loudly - Android just drops the incoming multicast packets, so discovery
    // returns nothing and looks like "no servers on this network". These two
    // lines are what separate that from a server that genuinely is not there.
    public void Acquire()
    {
        _lock.Acquire();
        Logger.LogDebug("Multicast lock acquired; mDNS discovery can receive packets.");
    }

    public void Release()
    {
        _lock.Release();
        Logger.LogDebug("Multicast lock released; mDNS discovery will stop receiving packets.");
    }
}
