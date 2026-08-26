using System;
using System.Diagnostics;
using System.IO;

using Avalonia;

using Flower.Logging;

namespace Flower.Desktop;

class Program
{
    // .NET's own native-crash-report feature (see CrashReportScanner) only
    // works via the DOTNET_EnableCrashReport/DOTNET_CrashReportDirectory env
    // vars, which the CLR reads before any of our managed code - including
    // this Main - ever runs, so setting them here would be too late. The only
    // way to get them in place in time is to have them already set in the
    // process's environment *before* this process starts, hence relaunching
    // ourselves once with them added. Skipped on Windows (the feature isn't
    // supported there - CrashReportScanner reads the Windows Event Log
    // instead) and skipped whenever a debugger is already attached, so a
    // normal Rider/VS Run/Debug launch (which attaches its debugger to the
    // process it starts) still debugs the real process directly instead of
    // an un-debugged child.
    private const string EnableCrashReportVariable = "DOTNET_EnableCrashReport";
    private const string CrashReportDirectoryVariable = "DOTNET_CrashReportDirectory";

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        if (RelaunchWithCrashReportingIfNeeded(args))
            return;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Returns true if this process just relaunched a child and should exit
    // immediately instead of starting Avalonia itself.
    private static bool RelaunchWithCrashReportingIfNeeded(string[] args)
    {
        if (OperatingSystem.IsWindows())
            return false;
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return false;
        if (Debugger.IsAttached)
            return false;
        if (Environment.GetEnvironmentVariable(EnableCrashReportVariable) == "1")
            return false; // already the relaunched child

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath))
            return false;
        // Launched via the "dotnet" muxer (e.g. `dotnet Flower.Desktop.dll`)
        // rather than the apphost - relaunching just this path would exec a
        // bare dotnet with no target and immediately fail, and reconstructing
        // the right "dotnet exec <dll> <args>" form reliably from here isn't
        // worth it. The apphost path (the normal Rider/packaged-app case)
        // below is the one that matters.
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            return false;

        Directory.CreateDirectory(CrashReportScanner.CrashReportsDirectory);

        var startInfo = new ProcessStartInfo(processPath)
        {
            UseShellExecute = false,
        };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);
        startInfo.Environment[EnableCrashReportVariable] = "1";
        startInfo.Environment[CrashReportDirectoryVariable] = CrashReportScanner.CrashReportsDirectory;

        using var child = Process.Start(startInfo);
        if (child == null)
            return false;

        child.WaitForExit();
        Environment.Exit(child.ExitCode);
        return true;
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
