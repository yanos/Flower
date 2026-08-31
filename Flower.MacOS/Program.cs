using System;

using AppKit;

using Avalonia;

using Flower.Logging;
using Flower.Platform;
using Flower.Services;

namespace Flower.MacOS;

// The macOS entry point. Flower.Desktop still builds and runs on macOS, but
// it is plain net10.0 and so has no Apple frameworks; this head exists to get
// at them - today AVKit, for the AirPlay/Bluetooth route picker (see
// docs/AIRPLAY-BLUETOOTH-PLAN.md Phase 2), and it is the natural home for the
// AppKit-backed pieces that currently go through hand-rolled objc_msgSend.
class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        if (CrashReportRelaunch.RelaunchIfNeeded(args))
            return;

        // Brings up the ObjC<->managed bridge that every AppKit/AVKit type
        // here depends on. Deliberately *not* followed by NSApplication.Main:
        // Avalonia runs the run loop itself, and calling both would start two.
        NSApplication.Init();

        // Apple's AirPlay/Bluetooth route button, hosted by
        // Flower.UserControls.RoutePickerControl. Set before Avalonia starts,
        // same timing/convention as Flower.iOS's AppDelegate, since
        // RoutePickerControl reads Current once in its constructor.
        PlatformRoutePicker.Current = new AppleRoutePicker();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithFlowerFonts()
            .LogToTrace()
            // Runs once native platform setup (incl. NSApplication) exists -
            // see MacDockIcon for why this can't just be Window.Icon.
            .AfterSetup(_ => MacDockIcon.Apply());
}
