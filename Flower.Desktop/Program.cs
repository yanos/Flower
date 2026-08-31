using System;

using Avalonia;

using Flower.Logging;
using Flower.Platform;

namespace Flower.Desktop;

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

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithFlowerFonts()
            .LogToTrace()
            // Runs once native platform setup (incl. NSApplication) exists -
            // see MacDockIcon for why this can't just be Window.Icon. Kept
            // here even though Flower.MacOS is now the macOS head: this one
            // still runs on macOS from Rider and from `dotnet run`, it just
            // has no AVKit (see docs/AIRPLAY-BLUETOOTH-PLAN.md).
            .AfterSetup(_ => MacDockIcon.Apply());

}
