namespace Flower.Services
{
    // The small tick a phone gives when a long press opens a menu - UIKit's
    // impact feedback on iOS, the view's own haptic feedback on Android, which
    // each follow the system's setting for it.
    public interface IHaptics
    {
        void LongPress();
    }

    // Set by the phone heads (Flower.iOS's AppDelegate, Flower.Android's
    // MainActivity). Null on the desktop, where a menu opens from a click and
    // gives nothing to feel.
    public static class PlatformHaptics
    {
        public static IHaptics? Current { get; set; }
    }
}
