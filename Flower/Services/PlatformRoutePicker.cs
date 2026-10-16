using Avalonia.Controls;

namespace Flower.Services
{
    // Apple's own AirPlay + Bluetooth route picker (AVKit's
    // AVRoutePickerView), surfaced inside Flower's UI. Deliberately not a
    // picker Flower draws: an in-app list built over whatever devices the
    // audio backend can enumerate (which is what the desktop status bar's
    // OutputDeviceControl is - see docs/AIRPLAY-BLUETOOTH-PLAN.md Phase 1)
    // cannot show or drive AirPlay receivers at all, and would sit alongside
    // the system picker the user already knows.
    //
    // The returned control is a NativeControlHost wrapping the real UIView, so
    // it can only be created by a platform head that has AVKit - which today
    // means Flower.iOS. Everywhere else Current stays null and
    // RoutePickerControl hides itself.
    public interface IPlatformRoutePicker
    {
        Control CreatePicker();
    }

    // A picker whose glyph can be drawn in a colour of the app's choosing -
    // white, on the desktop's player bar washed in the album's colour
    // (MainView). Null is the platform's own label colour.
    public interface IRoutePickerGlyphColor
    {
        Avalonia.Media.Color? GlyphColor { set; }
    }

    // Set by the platform entry point (Flower.iOS's AppDelegate) before
    // Avalonia starts - same timing/convention as PlatformNowPlaying.Current.
    public static class PlatformRoutePicker
    {
        public static IPlatformRoutePicker? Current { get; set; }
    }
}
