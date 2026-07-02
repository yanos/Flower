namespace Flower.Services
{
    public interface IMulticastLockHolder
    {
        void Acquire();
        void Release();
    }

    // Set by a platform entry point (Flower.Android's MainActivity) before Avalonia
    // starts, on platforms where mDNS multicast packets are silently dropped without
    // an explicit lock (Android). Left null on desktop/iOS, which need no such lock.
    public static class PlatformMulticastLock
    {
        public static IMulticastLockHolder? Current { get; set; }
    }
}
