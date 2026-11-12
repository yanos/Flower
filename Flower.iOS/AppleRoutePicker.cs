using System;

using AVKit;

using Avalonia.Controls;
using Avalonia.iOS;
using Avalonia.Platform;

using UIKit;

using Flower.Services;

namespace Flower.iOS;

// Surfaces AVRoutePickerView - the same AirPlay/Bluetooth button Music and
// Podcasts use - inside Flower's Avalonia tree. Tapping it opens the system
// route sheet, and picking a route there moves the whole app's audio: the
// route belongs to the AVAudioSession (see AppleAudioSession), not to any
// particular player object, so nothing here has to tell Flower's audio
// pipeline about it.
public sealed class AppleRoutePicker : IPlatformRoutePicker
{
    public Control CreatePicker() => new RoutePickerHost();

    private sealed class RoutePickerHost : NativeControlHost
    {
        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            var picker = new AVRoutePickerView
            {
                // SystemBlue rather than Flower's own palette: this is the
                // OS's control, and it reads as one - the same way the route
                // sheet it opens does.
                ActiveTintColor = UIColor.SystemBlue,
                // White in either theme: its one place is Now Playing's top
                // bar, on the cover's colour, where every control is white
                // (PaletteOnArt).
                TintColor = UIColor.White
            };

            // The picker itself is iOS 11, but this is iOS 13, and the
            // project still deploys back to 12.2. Same guarding convention as
            // AppDelegate's MetricKit hook.
            if (OperatingSystem.IsIOSVersionAtLeast(13))
            {
                // Video devices first would put an Apple TV above a pair of
                // AirPods, which is the wrong order for a music app.
                picker.PrioritizesVideoDevices = false;
            }

            return new UIViewControlHandle(picker);
        }

        protected override void DestroyNativeControlCore(IPlatformHandle control)
        {
            if (control is UIViewControlHandle handle)
                handle.Destroy();
            else
                base.DestroyNativeControlCore(control);
        }
    }
}
