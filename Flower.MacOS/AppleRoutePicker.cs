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
            // Drawn scaled up to match the glyphs beside it, see below.
            _picker = new ScaledRoutePickerView(new CGRect(0, 0, 28, 28))
            {
                // Borderless, so it reads as one of the status bar's flat
                // glyph buttons rather than a raised push button.
                RoutePickerButtonBordered = false
            };
            // The label colour, as the volume and output glyphs beside it are
            // the song title's, rather than AppKit's dimmer default. Only at
            // rest: routed elsewhere, it keeps the system's accent.
            _picker.SetRoutePickerButtonColor(AppKit.NSColor.Label, AVRoutePickerViewButtonState.Normal);

            return new PlatformHandle(_picker.Handle, "NSView");
        }

        protected override void DestroyNativeControlCore(IPlatformHandle control)
        {
            _picker?.RemoveFromSuperview();
            _picker?.Dispose();
            _picker = null;
        }
    }

    // AVRoutePickerView draws its glyph at one size whatever its frame, a
    // little smaller than the volume and output glyphs beside it. Bounds
    // smaller than the frame scale the drawing up, so this keeps the bounds
    // that much smaller than every frame Avalonia lays it out at - and its
    // button to those bounds, since the view lays the button out to its
    // frame's size, which in the smaller bounds put the glyph's middle
    // above the view's, out of line with the glyphs beside it. Filling the
    // bounds still leaves the glyph's middle 3.5 points below the middle of
    // the glyphs beside it (measured off a screenshot), so the button is
    // lifted by that much too.
    private sealed class ScaledRoutePickerView : AVRoutePickerView
    {
        private const double Scale = 28.0 / 22.0;
        private const double Lift = 3.5 / Scale;

        public ScaledRoutePickerView(CGRect frame) : base(frame) =>
            SetBoundsSize(Scaled(frame.Size));

        public override void SetFrameSize(CGSize newSize)
        {
            base.SetFrameSize(newSize);
            SetBoundsSize(Scaled(newSize));
        }

        public override void Layout()
        {
            base.Layout();
            var frame = Bounds;
            frame.Y += (nfloat)(IsFlipped ? -Lift : Lift);
            foreach (var subview in Subviews)
                subview.Frame = frame;
        }

        private static CGSize Scaled(CGSize size) => new(size.Width / Scale, size.Height / Scale);
    }
}
