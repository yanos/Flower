using System;
using System.IO;
using System.Linq;

using Microsoft.Extensions.Logging;

using Flower.Logging;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests.Logging;

// The previous run's native crash, read back from the report .NET wrote for it
// (DOTNET_EnableCrashReport, on Linux and macOS) into this run's log, once.
// Windows reads its event log instead, which a test cannot fill.
[Collection("PlatformDataDirectory")]
public class CrashReportScannerTests : PinnedDataDirectory
{
    private static void SkipUnlessCrashReportFiles() =>
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(),
            "Only Linux and macOS leave crash report files for the scanner to read.");

    private static string Report(string name, string content)
    {
        Directory.CreateDirectory(CrashReportScanner.CrashReportsDirectory);
        var path = Path.Combine(CrashReportScanner.CrashReportsDirectory, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void A_crash_report_from_the_last_run_is_logged_as_critical_and_then_deleted()
    {
        SkipUnlessCrashReportFiles();
        var report = Report("12345.crashreport.json", """{"payload":"SIGSEGV in libminiaudio"}""");
        var logger = new RecordingLogger<CrashReportScannerTests>();

        CrashReportScanner.ScanAndLog(logger);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Critical, entry.Level);
        Assert.Contains("SIGSEGV in libminiaudio", entry.Message);
        Assert.False(File.Exists(report));
    }

    // Deleted so the next launch does not report the same crash again.
    [Fact]
    public void A_crash_report_is_logged_by_one_launch_only()
    {
        SkipUnlessCrashReportFiles();
        Report("12345.crashreport.json", "{}");
        CrashReportScanner.ScanAndLog(new RecordingLogger<CrashReportScannerTests>());
        var logger = new RecordingLogger<CrashReportScannerTests>();

        CrashReportScanner.ScanAndLog(logger);

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void Each_of_several_crash_reports_is_logged()
    {
        SkipUnlessCrashReportFiles();
        Report("1.crashreport.json", "first");
        Report("2.crashreport.json", "second");
        var logger = new RecordingLogger<CrashReportScannerTests>();

        CrashReportScanner.ScanAndLog(logger);

        Assert.Equal(2, logger.CountAt(LogLevel.Critical, "crash report"));
        Assert.Empty(Directory.GetFiles(CrashReportScanner.CrashReportsDirectory));
    }

    // The runtime can leave other files beside a report - a dump, say. They
    // are not the scanner's to read or to delete.
    [Fact]
    public void Other_files_in_the_folder_are_left_alone()
    {
        SkipUnlessCrashReportFiles();
        var dump = Report("coredump.12345", "binary");
        var logger = new RecordingLogger<CrashReportScannerTests>();

        CrashReportScanner.ScanAndLog(logger);

        Assert.Empty(logger.Entries);
        Assert.True(File.Exists(dump));
    }
}
