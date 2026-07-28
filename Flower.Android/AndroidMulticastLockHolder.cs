using Android.Content;
using Android.Net.Wifi;

using Flower.Services;

namespace Flower.Android;

// Android drops incoming multicast packets by default to save battery, which
// silently breaks mDNS discovery unless a WifiManager.MulticastLock is held for
// the duration - see https://developer.android.com/reference/android/net/wifi/WifiManager.MulticastLock.
public class AndroidMulticastLockHolder : IMulticastLockHolder
{
    private readonly WifiManager.MulticastLock _lock;

    public AndroidMulticastLockHolder(Context context)
    {
        var wifiManager = (WifiManager)context.ApplicationContext!.GetSystemService(Context.WifiService)!;
        _lock = wifiManager.CreateMulticastLock("flower-mdns")!;
    }

    public void Acquire() => _lock.Acquire();

    public void Release() => _lock.Release();
}
