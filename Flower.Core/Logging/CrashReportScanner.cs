using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

namespace Flower.Logging
{
    // A native crash (e.g. inside ffaudio or miniaudio) bypasses
    // AppDomain.UnhandledException entirely - it never becomes a managed
    // exception, so App.axaml.cs's existing hook never fires for it. Rather
    // than installing our own signal/SEH handlers (risking interference with
    // the CLR's own use of them - see docs/CRASH-REPORTING-PLAN.md), this
    // reads back whatever the OS/runtime already recorded about the previous
    // run's crash and folds it into this run's log, so it doesn't just vanish.
    // The newest crash it finds also comes back as a PendingCrash, which the
    // caller records so this launch can offer to report it.
    //
    // Windows: the CLR already writes native-fault ("Application Error", ID
    // 1000) and managed-unhandled (".NET Runtime", ID 1026) entries to the
    // Application event log automatically - nothing to enable.
    // Linux/macOS: relies on .NET's own DOTNET_EnableCrashReport feature,
    // which both desktop heads enable via a one-time self-relaunch
    // (CrashReportRelaunch - the env vars have to be set before the CLR
    // starts, which is too late to do from here).
    // macOS also: the report macOS itself writes for every crash
    // (~/Library/Logs/DiagnosticReports/<process>-<time>.ips), preferred over
    // .NET's when both exist - it names the fault (EXC_BAD_ACCESS, SIGSEGV,
    // the address) and the faulting thread's frames by library and symbol,
    // where .NET's report leads with its own signal handler.
    public static class CrashReportScanner
    {
        // Where CrashReportRelaunch points DOTNET_DbgMiniDumpName
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

        // Version and LogFile are left for the caller: a crash found here
        // belongs to a run that has already ended, whose build only the
        // caller can guess at and whose log only it knows to look for.
        public static PendingCrash? ScanAndLog(ILogger logger)
        {
            if (OperatingSystem.IsWindows())
                return ScanWindowsEventLog(logger);
            if (OperatingSystem.IsMacOS())
            {
                var fromMacOS = ScanMacDiagnosticReports(logger);
                var fromDotNet = ScanCrashReportFiles(logger);
                return fromMacOS ?? fromDotNet;
            }
            if (OperatingSystem.IsLinux())
                return ScanCrashReportFiles(logger);
            return null;
        }

        // Where macOS keeps the crash reports it writes for this user's
        // processes. Settable for tests.
        public static string MacDiagnosticReportsDirectory { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "DiagnosticReports");

        private static string MacMarkerPath => Path.Combine(AppLogging.LogsDirectory, "macos-crash-scan.marker");

        // Flower's own reports among everyone's: macOS names each after the
        // process, and this is the process. Newer than the last scan only -
        // macOS keeps them for weeks, and they are not Flower's to delete. A
        // first scan looks back a day, as the Windows one does.
        public static PendingCrash? ScanMacDiagnosticReports(ILogger logger, string? processPath = null)
        {
            // The executable's whole name: GetFileNameWithoutExtension would
            // take "Flower.Desktop" for "Flower" plus an extension, and find
            // nothing (it did, until a deliberate crash showed it).
            var processName = Path.GetFileName(processPath ?? Environment.ProcessPath);
            if (string.IsNullOrEmpty(processName) || !Directory.Exists(MacDiagnosticReportsDirectory))
                return null;

            var since = ReadMarker(MacMarkerPath) ?? DateTime.UtcNow.AddHours(-24);
            var now = DateTime.UtcNow;
            PendingCrash? newest = null;
            try
            {
                foreach (var file in Directory.GetFiles(MacDiagnosticReportsDirectory, processName + "-*.ips"))
                {
                    var written = File.GetLastWriteTimeUtc(file);
                    if (written <= since)
                        continue;

                    try
                    {
                        var crash = DescribeMacReport(File.ReadAllText(file), new DateTimeOffset(written, TimeSpan.Zero));
                        logger.LogCritical("Crash found in macOS's crash reports ({File}): {Summary}\n{Frames}",
                            Path.GetFileName(file), crash.Summary, crash.Details);
                        if (newest == null || crash.When > newest.When)
                            newest = crash;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to read macOS crash report {File}", file);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to scan macOS's crash reports");
            }

            WriteMarker(MacMarkerPath, now);
            return newest;
        }

        // An .ips file: a line of JSON about the report, then the report as
        // JSON. Only the fault and the faulting thread's frames are kept -
        // the rest carries a reporter key, a sleep/wake ID, the Mac's model
        // and the path the app ran from, none of which is the crash.
        public static PendingCrash DescribeMacReport(string content, DateTimeOffset when)
        {
            var newline = content.IndexOf('\n');
            using var document = JsonDocument.Parse(newline >= 0 ? content[(newline + 1)..] : content);
            var root = document.RootElement;

            var fault = "Native crash";
            var detail = new StringBuilder();
            if (root.TryGetProperty("exception", out var exception) && exception.ValueKind == JsonValueKind.Object)
            {
                var signal = Text(exception, "signal");
                var type = Text(exception, "type");
                fault = (signal, type) switch
                {
                    ({ Length: > 0 }, { Length: > 0 }) => $"{signal} ({type})",
                    ({ Length: > 0 }, _) => signal,
                    (_, { Length: > 0 }) => type,
                    _ => fault,
                };
                if (Text(exception, "subtype") is { Length: > 0 } subtype)
                    detail.AppendLine(subtype);
            }

            string? topFrame = null;
            if (root.TryGetProperty("faultingThread", out var faulting) && faulting.TryGetInt32(out var index)
                && root.TryGetProperty("threads", out var threads) && threads.ValueKind == JsonValueKind.Array
                && index >= 0 && index < threads.GetArrayLength()
                && threads[index].TryGetProperty("frames", out var frames) && frames.ValueKind == JsonValueKind.Array)
            {
                root.TryGetProperty("usedImages", out var images);
                foreach (var frame in frames.EnumerateArray().Take(ReportedFrames))
                {
                    string? image = null;
                    if (frame.TryGetProperty("imageIndex", out var imageIndex) && imageIndex.TryGetInt32(out var i)
                        && images.ValueKind == JsonValueKind.Array && i >= 0 && i < images.GetArrayLength())
                    {
                        image = Text(images[i], "name");
                    }

                    var symbol = Text(frame, "symbol");
                    var name = (image, symbol) switch
                    {
                        ({ Length: > 0 }, { Length: > 0 }) => $"{image}!{symbol}",
                        ({ Length: > 0 }, _) => $"{image}+{Text(frame, "imageOffset")}",
                        _ => "?",
                    };
                    if (image != null)
                        topFrame ??= name;
                    detail.Append("  at ").AppendLine(name);
                }
            }

            var summary = topFrame != null ? $"{fault} in {topFrame}" : fault;
            return new PendingCrash(summary, detail.ToString().TrimEnd(), null, null, when, Native: true);
        }

        private static PendingCrash? ScanCrashReportFiles(ILogger logger)
        {
            if (!Directory.Exists(CrashReportsDirectory))
                return null;

            PendingCrash? newest = null;
            foreach (var file in Directory.GetFiles(CrashReportsDirectory, "*.crashreport.json"))
            {
                try
                {
                    var content = File.ReadAllText(file);
                    logger.LogCritical("Native crash report found from previous run ({File}):\n{Content}", Path.GetFileName(file), content);
                    var when = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
                    if (newest == null || when > newest.When)
                        newest = DescribeCrashReport(content, when);
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

            return newest;
        }

        // Where the kernel enters a signal handler: _sigtramp on macOS,
        // __restore_rt on glibc.
        private static readonly HashSet<string> SignalTrampolines = new(StringComparer.Ordinal)
        {
            "_sigtramp", "__restore_rt", "__kernel_rt_sigreturn",
        };

        // How many of the crashed thread's frames make it into the details -
        // the top of the stack is what says where; the rest is in the log.
        private const int ReportedFrames = 30;

        // A .NET crash report (createdump's JSON) as a summary and the
        // crashed thread's frames, one per line. Anything that is not the
        // shape expected is reported as it is, so a report from a runtime
        // that writes it differently is still a report.
        public static PendingCrash DescribeCrashReport(string content, DateTimeOffset when)
        {
            try
            {
                using var document = JsonDocument.Parse(content);
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("payload", out var payload) &&
                    payload.ValueKind == JsonValueKind.Object &&
                    payload.TryGetProperty("threads", out var threads) &&
                    threads.ValueKind == JsonValueKind.Array)
                {
                    foreach (var thread in threads.EnumerateArray())
                    {
                        if (thread.ValueKind != JsonValueKind.Object || Text(thread, "crashed") != "true")
                            continue;

                        var frames = new StringBuilder();
                        string? topFrame = null;
                        if (thread.TryGetProperty("stack_frames", out var stack) && stack.ValueKind == JsonValueKind.Array)
                        {
                            // A native fault's thread starts with .NET's own
                            // signal handler writing this report, down to the
                            // signal trampoline; the crash is what comes after.
                            var all = stack.EnumerateArray().ToList();
                            var trampoline = all.FindIndex(frame => Text(frame, "unmanaged_name") is { } name && SignalTrampolines.Contains(name));
                            foreach (var frame in all.Skip(trampoline + 1).Take(ReportedFrames))
                            {
                                var name = FrameName(frame);
                                topFrame ??= name;
                                frames.Append("  at ").AppendLine(name);
                            }
                        }

                        var exceptionType = Text(thread, "managed_exception_type");
                        var summary = !string.IsNullOrEmpty(exceptionType)
                            ? exceptionType
                            : topFrame != null ? $"Native crash in {topFrame}" : "Native crash";
                        return new PendingCrash(summary, frames.ToString().TrimEnd(), null, null, when, Native: true);
                    }
                }
            }
            catch (JsonException)
            {
                // Not JSON at all - reported raw below.
            }

            return new PendingCrash("Native crash", content, null, null, when, Native: true);
        }

        private static string FrameName(JsonElement frame)
        {
            if (frame.ValueKind != JsonValueKind.Object)
                return "?";

            var module = Text(frame, "filename");
            var method = Text(frame, "method_name") is { Length: > 0 } managed ? managed : Text(frame, "unmanaged_name");
            if (!string.IsNullOrEmpty(module))
                module = Path.GetFileName(module);

            return (string.IsNullOrEmpty(module), string.IsNullOrEmpty(method)) switch
            {
                (false, false) => $"{module}!{method}",
                (true, false) => method!,
                (false, true) => module!,
                _ => Text(frame, "native_address") ?? "?",
            };
        }

        private static string? Text(JsonElement element, string property) =>
            element.TryGetProperty(property, out var value)
                ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()
                : null;

        // A crash Android recorded in its exit history (MainActivity's
        // CollectCrashExitReasons), which says little more than why.
        public static PendingCrash? DescribeAndroidExitReasons(IReadOnlyList<string> reasons, DateTimeOffset when) =>
            reasons.Count == 0
                ? null
                : new PendingCrash("Crash recorded by Android", string.Join("\n", reasons), null, null, when, Native: true);

        [SupportedOSPlatform("windows")]
        private static PendingCrash? ScanWindowsEventLog(ILogger logger)
        {
            PendingCrash? newest = null;
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
                        var when = record.TimeCreated is { } created ? new DateTimeOffset(created) : DateTimeOffset.Now;
                        if (newest == null || when > newest.When)
                            newest = DescribeWindowsEvent(description, when);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to scan Windows Event Log for crash records");
            }

            WriteWindowsMarker(now);
            return newest;
        }

        // An Application Error (1000) names the faulting module; a .NET
        // Runtime (1026) entry carries the exception. Either line makes a
        // better title than the event's first, which only names the app.
        public static PendingCrash DescribeWindowsEvent(string description, DateTimeOffset when)
        {
            var lines = description.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var exception = lines.FirstOrDefault(line => line.StartsWith("Exception Info:", StringComparison.OrdinalIgnoreCase));
            var module = lines.FirstOrDefault(line => line.StartsWith("Faulting module name:", StringComparison.OrdinalIgnoreCase));
            var summary = exception != null
                ? exception["Exception Info:".Length..].Trim()
                : module != null
                    ? $"Native crash in {module["Faulting module name:".Length..].Split(',')[0].Trim()}"
                    : "Native crash";
            return new PendingCrash(summary, description, null, null, when, Native: true);
        }

        [SupportedOSPlatform("windows")]
        private static DateTime? ReadWindowsMarker() => ReadMarker(WindowsMarkerPath);

        private static DateTime? ReadMarker(string path)
        {
            try
            {
                if (File.Exists(path) &&
                    DateTime.TryParse(File.ReadAllText(path), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var since))
                    return since;
            }
            catch
            {
                // Best effort - fall through to the 24h default lookback in the caller.
            }
            return null;
        }

        [SupportedOSPlatform("windows")]
        private static void WriteWindowsMarker(DateTime timestamp) => WriteMarker(WindowsMarkerPath, timestamp);

        private static void WriteMarker(string path, DateTime timestamp)
        {
            try
            {
                Directory.CreateDirectory(AppLogging.LogsDirectory);
                File.WriteAllText(path, timestamp.ToString("o", CultureInfo.InvariantCulture));
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
