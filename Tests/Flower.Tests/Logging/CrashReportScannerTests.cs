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

    [Fact]
    public void The_newest_crash_report_comes_back_to_be_offered_for_reporting()
    {
        SkipUnlessCrashReportFiles();
        File.SetLastWriteTimeUtc(Report("1.crashreport.json", "older"), new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Report("2.crashreport.json", "newer"), new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc));

        var crash = CrashReportScanner.ScanAndLog(new RecordingLogger<CrashReportScannerTests>());

        Assert.Equal("newer", crash?.Details);
    }

    [Fact]
    public void No_crash_report_means_nothing_to_offer()
    {
        Assert.Null(CrashReportScanner.ScanAndLog(new RecordingLogger<CrashReportScannerTests>()));
    }

    // The shape createdump writes: the crashed thread's frames, managed and
    // native, are what the issue shows; every other thread is left to the log.
    [Fact]
    public void A_crash_report_is_described_by_its_crashed_threads_frames()
    {
        const string report = """
            {
              "payload": {
                "threads": [
                  { "crashed": "false", "stack_frames": [ { "filename": "libother.dylib", "unmanaged_name": "idle" } ] },
                  { "crashed": "true", "stack_frames": [
                      { "is_managed": "false", "filename": "/usr/lib/libminiaudio.dylib", "unmanaged_name": "ma_device_read" },
                      { "is_managed": "true", "filename": "Flower.dll", "method_name": "Flower.Audio.MiniaudioSink.Render" }
                  ] }
                ]
              }
            }
            """;

        var crash = CrashReportScanner.DescribeCrashReport(report, DateTimeOffset.UnixEpoch);

        Assert.Equal("Native crash in libminiaudio.dylib!ma_device_read", crash.Summary);
        Assert.Contains("Flower.dll!Flower.Audio.MiniaudioSink.Render", crash.Details);
        Assert.DoesNotContain("idle", crash.Details);
    }

    [Fact]
    public void A_managed_exception_in_a_crash_report_is_its_summary()
    {
        const string report = """
            { "payload": { "threads": [ { "crashed": "true", "managed_exception_type": "System.NullReferenceException", "stack_frames": [] } ] } }
            """;

        Assert.Equal("System.NullReferenceException", CrashReportScanner.DescribeCrashReport(report, DateTimeOffset.UnixEpoch).Summary);
    }

    [Fact]
    public void A_crash_report_not_in_the_expected_shape_is_reported_as_it_is()
    {
        var crash = CrashReportScanner.DescribeCrashReport("SIGSEGV somewhere", DateTimeOffset.UnixEpoch);

        Assert.Equal("Native crash", crash.Summary);
        Assert.Equal("SIGSEGV somewhere", crash.Details);
    }

    [Fact]
    public void A_windows_runtime_event_is_summarised_by_its_exception()
    {
        const string description = "Application: Flower.Desktop.exe\r\nCoreCLR Version: 10.0\r\nDescription: The process was terminated due to an unhandled exception.\r\nException Info: System.IO.IOException: The disk is full\r\n   at Flower.Save()";

        Assert.Equal("System.IO.IOException: The disk is full", CrashReportScanner.DescribeWindowsEvent(description, DateTimeOffset.UnixEpoch).Summary);
    }

    [Fact]
    public void A_windows_application_error_is_summarised_by_its_faulting_module()
    {
        const string description = "Faulting application name: Flower.Desktop.exe, version: 0.9.0.0\r\nFaulting module name: flowerbridge.dll, version: 0.0.0.0, time stamp: 0x0\r\nException code: 0xc0000005";

        Assert.Equal("Native crash in flowerbridge.dll", CrashReportScanner.DescribeWindowsEvent(description, DateTimeOffset.UnixEpoch).Summary);
    }
}
