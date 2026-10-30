using System;
using System.Linq;

using Avalonia;

using Flower.Logging;
using Flower.Platform;
using Flower.Services;

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
        //
        // Uninstalling also takes the flower:// handler back out, if it is
        // still this install's - see UrlSchemeRegistration.
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ =>
            {
                if (OperatingSystem.IsWindows())
                    UrlSchemeRegistration.UnregisterOnWindows();
            })
            .Run();

        if (CrashReportRelaunch.RelaunchIfNeeded(args))
            return;

        // A pairing link opened while Flower already runs reaches a new
        // process on Windows and Linux, as its argument. Handed to the running
        // instance, and this one exits rather than becoming a second window;
        // with none running, this process is the instance and keeps the link
        // for its own window. See PairingLinkForwarder.
        if (args.FirstOrDefault(arg => PairingInvite.TryParse(arg) != null) is { } link &&
            PairingLinkForwarder.TryForward(link))
        {
            return;
        }
        PairingLinkInbox.Current.DeliverArguments(args);

        // macOS delivers a link to the running app itself (Flower.MacOS); this
        // head running there from the IDE has no scheme registered at all.
        if (!OperatingSystem.IsMacOS())
        {
            _ = PairingLinkForwarder.ListenAsync(PairingLinkInbox.Current);
            UrlSchemeRegistration.RegisterInBackground();
        }

        PlatformAppUpdater.Current = VelopackUpdater.ForThisInstall();

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
