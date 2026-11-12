using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Flower.Logging
{
    // A crash the next launch has yet to offer to report - see
    // docs/CRASH-REPORTING-PLAN.md. The crash itself cannot ask: a terminating
    // exception leaves no time for a dialog, and a native one runs no managed
    // code at all. So whatever caught it leaves this behind, and the next
    // launch shows a banner asking to open a prefilled GitHub issue
    // (CrashIssueLink), which is where a person reads what is about to be
    // sent before anything leaves the machine.
    //
    // Two kinds of evidence fill it. A managed exception is recorded by
    // App.axaml.cs's UnhandledException hook as the process dies. A native
    // crash is found at the next launch by CrashReportScanner or Android's
    // exit history, and only fills an empty slot: on Linux and macOS a
    // terminating managed exception also leaves a .NET crash report, which
    // says less than the exception the hook already wrote.
    //
    // One crash at a time, the newest: a run that crashes again before the
    // last one was answered has the fresher story.
    //
    // With crash reporting on (CrashReporting), a managed crash has already
    // gone to Sentry as the process died and this is just forgotten; a native
    // one (Native) is sent from here at the next launch. Only with reporting
    // off does it become the GitHub banner.
    public sealed record PendingCrash(
        string Summary,
        string Details,
        string? Version,
        string? LogFile,
        DateTimeOffset When,
        bool Native = false)
    {
        public static string FilePath => Path.Combine(AppLogging.LogsDirectory, "pending-crash.json");

        // The exception's type and first line as the summary, which becomes
        // the issue's title; the whole of ToString(), inner exceptions and
        // stacks, as the details.
        public static PendingCrash FromException(Exception exception, string? version, string? logFile, DateTimeOffset when)
        {
            var firstLine = exception.Message.Split('\n', 2)[0].Trim();
            var summary = firstLine.Length == 0
                ? exception.GetType().FullName ?? exception.GetType().Name
                : $"{exception.GetType().FullName}: {firstLine}";
            return new PendingCrash(summary, exception.ToString(), version, logFile, when);
        }

        // Called from inside a dying process, so it never throws: a crash
        // report that failed to write is not worth a second exception on top
        // of the first.
        public void Record()
        {
            try
            {
                Directory.CreateDirectory(AppLogging.LogsDirectory);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, PendingCrashJsonContext.Default.PendingCrash));
            }
            catch
            {
                // Best effort - the crash is in the log either way.
            }
        }

        public void RecordUnlessPending()
        {
            if (!File.Exists(FilePath))
                Record();
        }

        public static PendingCrash? Read()
        {
            try
            {
                return File.Exists(FilePath)
                    ? JsonSerializer.Deserialize(File.ReadAllText(FilePath), PendingCrashJsonContext.Default.PendingCrash)
                    : null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // A file from a build that wrote it differently, or half of
                // one: nothing anyone can report, so it goes.
                Clear();
                return null;
            }
        }

        // Answered - reported or dismissed - so the next launch does not ask
        // again.
        public static void Clear()
        {
            try
            {
                File.Delete(FilePath);
            }
            catch
            {
                // Best effort - at worst the same crash is offered once more.
            }
        }

        // The log of the run that crashed, for a crash found only at the next
        // launch: the newest log file other than this run's own.
        public static string? PreviousLogFile(string currentLogFile)
        {
            try
            {
                return Directory.GetFiles(AppLogging.LogsDirectory, "flower-*.log")
                    .Where(path => !string.Equals(path, currentLogFile, StringComparison.Ordinal))
                    .OrderByDescending(path => path, StringComparer.Ordinal)
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }
    }

    [JsonSerializable(typeof(PendingCrash))]
    internal partial class PendingCrashJsonContext : JsonSerializerContext
    {
    }
}
