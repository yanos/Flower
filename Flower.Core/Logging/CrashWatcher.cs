using System;
using System.Collections.Generic;
using System.IO;

using Flower.Services;

using Sentry;

namespace Flower.Logging
{
    // Reports a desktop app's crash the moment it happens. A process that has
    // just crashed cannot send anything itself: its memory may be corrupt and
    // its allocator locked. Its parent (CrashReportRelaunch), which only waits
    // for it, is healthy, and is the same build, so it knows the exact version
    // without having to work it out from the crash report.
    //
    // A .NET exception normally reached Sentry as the process died, and is
    // left alone. What the SDK missed - a native crash, or an exception from
    // before it started - is sent from here. If it cannot go now, Sentry keeps
    // it on disk for the next launch.
    public static class CrashWatcher
    {
        // How long the operating system gets to finish its report.
        private static readonly TimeSpan ReportWait = TimeSpan.FromSeconds(4);

        // How long a closing app may hold on to send it.
        private static readonly TimeSpan SendWait = TimeSpan.FromSeconds(5);

        // SIGILL, SIGTRAP, SIGABRT, SIGBUS (7 on Linux, 10 on macOS), SIGFPE
        // and SIGSEGV. Not SIGTERM or SIGKILL, which are someone quitting it.
        private static readonly HashSet<int> CrashSignals = [4, 5, 6, 7, 8, 10, 11];

        private const uint WindowsControlC = 0xC000013A;

        // A Windows crash exits with an NTSTATUS error, which reads as negative.
        public static bool IsCrashExit(int exitCode, bool onWindows) =>
            onWindows
                ? exitCode < 0 && unchecked((uint)exitCode) != WindowsControlC
                : exitCode > 128 && CrashSignals.Contains(exitCode - 128);

        public static void ReportChildCrash(
            int processId,
            int exitCode,
            DateTimeOffset startedAt,
            Func<bool> reportingEnabled,
            TimeSpan? reportWait = null)
        {
            if (!IsCrashExit(exitCode, OperatingSystem.IsWindows()))
                return;

            try
            {
                var logger = AppLogging.CreateLogger("Flower.CrashWatcher");
                var recorded = PendingCrash.Read();
                var thrown = recorded is { Native: false } && recorded.When >= startedAt ? recorded : null;

                // Taken even after an exception, which leaves a report too:
                // the next launch must not count it as a second crash.
                var found = CrashReportScanner.TakeCrashOfProcess(
                    logger, processId, startedAt, thrown != null ? TimeSpan.Zero : reportWait ?? ReportWait);

                if (thrown is { ReachedSentry: true } || (thrown ?? found) is not { } crash)
                    return;

                crash = crash with
                {
                    Version = AppVersion.Full,
                    LogFile = crash.LogFile ?? CrashedRunLog(startedAt),
                };
                if (crash.Native)
                    AppendToLog(crash);

                CrashReporting.Enabled = reportingEnabled();
                using var sentry = CrashReporting.StartForApp(AppVersion.Full);
                if (sentry != null && CrashReporting.Enabled)
                {
                    CrashReporting.CaptureRecorded(crash);
                    SentrySdk.Flush(SendWait);
                    crash = crash with { ReachedSentry = true };
                }

                // For the next launch: the GitHub banner if nothing was sent.
                crash.Record();
            }
            catch
            {
                // Reporting a crash must never keep the app from closing. The
                // next launch still scans for it.
            }
        }

        // The newest log, if the run that crashed got as far as writing one.
        private static string? CrashedRunLog(DateTimeOffset startedAt)
        {
            var log = PendingCrash.PreviousLogFile(string.Empty);
            return log != null && File.GetLastWriteTimeUtc(log) >= startedAt.UtcDateTime ? log : null;
        }

        private static void AppendToLog(PendingCrash crash)
        {
            if (crash.LogFile is { } log)
                File.AppendAllText(log, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [FTL] Native crash: {crash.Summary}{Environment.NewLine}{crash.Details}{Environment.NewLine}");
        }
    }
}
