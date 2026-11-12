using System;
using System.Diagnostics;
using System.IO;

namespace Flower.Logging
{
    // .NET's own native-crash-report feature (see CrashReportScanner) only
    // works via DOTNET_EnableCrashReport and the dump settings below - env
    // vars, which the CLR reads before any managed code - including a head's
    // Main - ever runs, so setting them from inside the process would be too
    // late. The only way to get them in place in time is to have them already
    // set in the process's environment *before* the process starts, hence
    // relaunching ourselves once with them added.
    //
    // Shared by every desktop head rather than living in one of them: the
    // macOS head (Flower.MacOS) and the Windows/Linux head (Flower.Desktop)
    // both need it and would otherwise carry the same fifty lines twice.
    public static class CrashReportRelaunch
    {
        // The report is written by createdump, which only runs with dumps
        // enabled; CrashReportOnly then keeps the report and skips the dump
        // itself (hundreds of megabytes nobody reads), and the dump's name
        // decides where the report goes - <name>.crashreport.json. There is
        // no setting for the report's folder of its own: this once set a
        // DOTNET_CrashReportDirectory that the runtime does not have, and no
        // report was ever written (found November 2026, by crashing on
        // purpose).
        private const string EnableCrashReportVariable = "DOTNET_EnableCrashReport";
        private const string CrashReportOnlyVariable = "DOTNET_EnableCrashReportOnly";
        private const string EnableDumpVariable = "DOTNET_DbgEnableMiniDump";
        private const string DumpNameVariable = "DOTNET_DbgMiniDumpName";

        // Returns true if this process just relaunched a child and should exit
        // immediately instead of starting Avalonia itself.
        //
        // Skipped on Windows (the feature isn't supported there -
        // CrashReportScanner reads the Windows Event Log instead) and skipped
        // whenever a debugger is already attached, so a normal Rider/VS
        // Run/Debug launch (which attaches its debugger to the process it
        // starts) still debugs the real process directly instead of an
        // un-debugged child.
        public static bool RelaunchIfNeeded(string[] args)
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
            startInfo.Environment[CrashReportOnlyVariable] = "1";
            startInfo.Environment[EnableDumpVariable] = "1";
            startInfo.Environment[DumpNameVariable] = Path.Combine(CrashReportScanner.CrashReportsDirectory, "flower-%p.dmp");

            using var child = Process.Start(startInfo);
            if (child == null)
                return false;

            child.WaitForExit();
            Environment.Exit(child.ExitCode);
            return true;
        }
    }
}
