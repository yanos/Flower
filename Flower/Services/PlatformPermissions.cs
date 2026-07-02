namespace Flower.Services
{
    public interface IMediaPermissionStatus
    {
        bool IsGranted();
        void OpenAppSettings();
    }

    // Set by a platform entry point (Flower.Android's MainActivity) before Avalonia
    // starts, on platforms with a runtime media-access permission that can be
    // permanently denied (Android). Left null on desktop/iOS, which have nothing
    // equivalent to check or a system settings screen to deep-link into.
    public static class PlatformPermissions
    {
        public static IMediaPermissionStatus? Current { get; set; }
    }
}
