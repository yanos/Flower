using System;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Runtime.Versioning;

using Microsoft.Extensions.Logging;

namespace Flower.Logging
{
    // A native crash (e.g. inside libvlc, loaded via VlcNativeSetup) bypasses
    // AppDomain.UnhandledException entirely - it never becomes a managed
    // exception, so App.axaml.cs's existing hook never fires for it. Rather
    // than installing our own signal/SEH handlers (risking interference with
    // the CLR's own use of them - see docs/CRASH-REPORTING-PLAN.md), this
    // reads back whatever the OS/runtime already recorded about the previous
    // run's crash and folds it into this run's log, so it doesn't just vanish.
    //
    // Windows: the CLR already writes native-fault ("Application Error", ID
    // 1000) and managed-unhandled (".NET Runtime", ID 1026) entries to the
    // Application event log automatically - nothing to enable.
    // Linux/macOS: relies on .NET's own DOTNET_EnableCrashReport feature,
    // which Flower.Desktop/Program.cs enables via a one-time self-relaunch
    // (the env var has to be set before the CLR starts, which is too late to
    // do from here) - see its own comment for why.
    public static class CrashReportScanner
    {
        // Where Flower.Desktop/Program.cs points DOTNET_CrashReportDirectory
        // on Linux/macOS.
        public static string CrashReportsDirectory => Path.Combine(AppLogging.LogsDirectory, "crash-reports");

        private static string WindowsMarkerPath => Path.Combine(AppLogging.LogsDirectory, "windows-crash-scan.marker");

        // Only Application Error (native faults) and .NET Runtime (managed
        // unhandled exceptions that reached the OS crash-reporting layer,
        // e.g. before any of our own handlers could run) are relevant here -
        // everything else in the Application log belongs to other software
        // on the machine.
        private const string WindowsEventSources = "Application Error";
        private const string DotNetRuntimeEventSource = ".NET Runtime";

        public static void ScanAndLog(ILogger logger)
        {
            if (OperatingSystem.IsWindows())
                ScanWindowsEventLog(logger);
            else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                ScanCrashReportFiles(logger);
        }

        private static void ScanCrashReportFiles(ILogger logger)
        {
            if (!Directory.Exists(CrashReportsDirectory))
                return;

            foreach (var file in Directory.GetFiles(CrashReportsDirectory, "*.crashreport.json"))
            {
                try
                {
                    var content = File.ReadAllText(file);
                    logger.LogCritical("Native crash report found from previous run ({File}):\n{Content}", Path.GetFileName(file), content);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to read crash report {File}", file);
                }
                finally
                {
                    TryDelete(file);
                }
            }
        }

        [SupportedOSPlatform("windows")]
        private static void ScanWindowsEventLog(ILogger logger)
        {
            // First run ever (no marker yet): only look back 24h rather than
            // the whole Application log's history, since anything older is
            // very unlikely to be the crash that just preceded this launch.
            var since = ReadWindowsMarker() ?? DateTime.UtcNow.AddHours(-24);
            var now = DateTime.UtcNow;

            // The exe name as it appears in the event's formatted description
            // ("Faulting application name: Flower.Desktop.exe", "Application:
            // Flower.Desktop.exe ...") - there's no reliable structured field
            // to match our process against instead, since ID 1000 is often
            // logged by a separate WerFault.exe helper process.
            var exeName = Path.GetFileName(Environment.ProcessPath);

            try
            {
                var sinceUtc = since.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
                var query = new EventLogQuery("Application", PathType.LogName,
                    $"*[System[(Provider[@Name='{WindowsEventSources}'] or Provider[@Name='{DotNetRuntimeEventSource}']) and TimeCreated[@SystemTime>='{sinceUtc}']]]");

                using var reader = new EventLogReader(query);
                for (var record = reader.ReadEvent(); record != null; record = reader.ReadEvent())
                {
                    using (record)
                    {
                        string? description;
                        try
                        {
                            description = record.FormatDescription();
                        }
                        catch
                        {
                            // Provider manifest not resolvable on this machine - skip, not worth failing the whole scan over one entry.
                            continue;
                        }

                        if (string.IsNullOrEmpty(exeName) || description == null ||
                            !description.Contains(exeName, StringComparison.OrdinalIgnoreCase))
                            continue;

                        logger.LogCritical("Crash found in Windows Event Log ({Provider}, ID {Id}, {Time}): {Description}",
                            record.ProviderName, record.Id, record.TimeCreated, description);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to scan Windows Event Log for crash records");
            }

            WriteWindowsMarker(now);
        }

        [SupportedOSPlatform("windows")]
        private static DateTime? ReadWindowsMarker()
        {
            try
            {
                if (File.Exists(WindowsMarkerPath) &&
                    DateTime.TryParse(File.ReadAllText(WindowsMarkerPath), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var since))
                    return since;
            }
            catch
            {
                // Best effort - fall through to the 24h default lookback in the caller.
            }
            return null;
        }

        [SupportedOSPlatform("windows")]
        private static void WriteWindowsMarker(DateTime timestamp)
        {
            try
            {
                Directory.CreateDirectory(AppLogging.LogsDirectory);
                File.WriteAllText(WindowsMarkerPath, timestamp.ToString("o", CultureInfo.InvariantCulture));
            }
            catch
            {
                // Best effort - if this fails we just rescan the same window next time.
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // Best effort - e.g. permissions; not worth failing startup over a leftover crash-report file.
            }
        }
    }
}
