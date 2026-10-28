using System;

using Avalonia;

using Flower.Logging;
using Flower.Platform;

using Velopack;

namespace Flower.Desktop;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // First, before anything else: the Windows installer runs the app
        // with arguments of its own while installing, updating and
        // uninstalling, and this answers them and exits. Without it Setup.exe
        // would open Flower's window instead. A no-op for a run that is not
        // one of those, a plain `dotnet run` included.
        VelopackApp.Build().Run();

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
