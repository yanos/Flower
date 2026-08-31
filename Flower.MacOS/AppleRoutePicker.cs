using AVKit;

using Avalonia.Controls;
using Avalonia.Platform;

using CoreGraphics;

using Flower.Services;

namespace Flower.MacOS;

// Surfaces AVRoutePickerView - the same AirPlay button Music uses - inside
// Flower's Avalonia tree on macOS. The iOS twin of this class lives in
// Flower.iOS; they cannot be shared, because the two SDKs bind AVKit into
// different, mutually exclusive target frameworks.
//
// Unlike iOS, macOS has no AVAudioSession: with no player attached, the
// picker drives the *system* audio output context, which is the behaviour
// Flower wants - miniaudio renders to whatever CoreAudio's default output
// device is, so a route the user picks here is one Flower's audio follows
// without this class telling the audio pipeline anything. That is the claim
// that most needs confirming by hand on a real AirPlay receiver; see the plan
// doc.
public sealed class AppleRoutePicker : IPlatformRoutePicker
{
    public Control CreatePicker() => new RoutePickerHost();

    private sealed class RoutePickerHost : NativeControlHost
    {
        // Held so the managed wrapper outlives the handle Avalonia is given -
        // the NSView itself is retained by the view hierarchy, but nothing
        // else keeps the binding object from being collected.
        private AVRoutePickerView? _picker;

        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            // An explicit frame because a NativeControlHost has no intrinsic
            // size to hand down and AVRoutePickerView does not size itself;
            // the Avalonia side sets the same numbers (see MainView.axaml).
            _picker = new AVRoutePickerView(new CGRect(0, 0, 22, 22))
            {
                // Borderless, so it reads as one of the status bar's flat
                // glyph buttons rather than a raised push button.
                RoutePickerButtonBordered = false
            };

            return new PlatformHandle(_picker.Handle, "NSView");
        }

        protected override void DestroyNativeControlCore(IPlatformHandle control)
        {
            _picker?.RemoveFromSuperview();
            _picker?.Dispose();
            _picker = null;
        }
    }
}
