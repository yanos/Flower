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

    // A fault on a native thread - the audio render thread, say. .NET's
    // report starts with its own signal handler writing the report; the
    // crash is below the trampoline, and that is what the summary names.
    [Fact]
    public void A_native_faults_summary_skips_dotnets_own_signal_handler()
    {
        const string report = """
            { "payload": { "threads": [ { "crashed": "true", "stack_frames": [
                { "is_managed": "false", "unmanaged_name": "__wait4_nocancel" },
                { "is_managed": "false", "unmanaged_name": "PROCCreateCrashDump" },
                { "is_managed": "false", "unmanaged_name": "invoke_previous_action" },
                { "is_managed": "false", "unmanaged_name": "_sigtramp" },
                { "is_managed": "false", "unmanaged_name": "flower_render_callback" },
                { "is_managed": "false", "unmanaged_name": "_pthread_start" }
            ] } ] } }
            """;

        var crash = CrashReportScanner.DescribeCrashReport(report, DateTimeOffset.UnixEpoch);

        Assert.Equal("Native crash in flower_render_callback", crash.Summary);
        Assert.DoesNotContain("PROCCreateCrashDump", crash.Details);
    }

    // The shape macOS writes: a line about the report, then the report. Only
    // the fault and the faulting thread's frames are kept.
    private const string MacReport = """
        {"app_name":"Flower.Desktop","bug_type":"309","name":"Flower.Desktop"}
        {
          "crashReporterKey": "3CF148C2-8E5A-49C4-F8B6-BEF745AF6026",
          "sleepWakeUUID": "547DFD8B-E1B5-455F-B88C-3E108F534732",
          "modelCode": "MacBookPro18,4",
          "procPath": "/Users/someone/Applications/Flower.app/Contents/MacOS/Flower",
          "exception": { "type": "EXC_BAD_ACCESS", "signal": "SIGSEGV", "subtype": "KERN_INVALID_ADDRESS at 0x0000000000000010" },
          "faultingThread": 1,
          "threads": [
            { "frames": [ { "imageIndex": 1, "symbol": "mach_msg2_trap" } ] },
            { "frames": [
              { "imageIndex": 0, "symbol": "flower_render_callback", "imageOffset": 4096 },
              { "imageIndex": 2, "symbol": "ma_device__read_frames", "imageOffset": 128 }
            ] }
          ],
          "usedImages": [ { "name": "libflowerbridge.dylib" }, { "name": "libsystem_kernel.dylib" }, { "name": "libminiaudio.dylib" } ]
        }
        """;

    [Fact]
    public void A_macOS_crash_report_names_the_fault_and_where_it_happened()
    {
        var crash = CrashReportScanner.DescribeMacReport(MacReport, DateTimeOffset.UnixEpoch);

        Assert.True(crash.Native);
        Assert.Equal("SIGSEGV (EXC_BAD_ACCESS) in libflowerbridge.dylib!flower_render_callback", crash.Summary);
        Assert.Contains("KERN_INVALID_ADDRESS", crash.Details);
        Assert.Contains("libminiaudio.dylib!ma_device__read_frames", crash.Details);
        Assert.DoesNotContain("mach_msg2_trap", crash.Details);
    }

    [Fact]
    public void A_macOS_crash_report_leaves_the_machines_identity_behind()
    {
        var crash = CrashReportScanner.DescribeMacReport(MacReport, DateTimeOffset.UnixEpoch);
        var everything = crash.Summary + crash.Details;

        Assert.DoesNotContain("3CF148C2", everything);
        Assert.DoesNotContain("MacBookPro", everything);
        Assert.DoesNotContain("someone", everything);
    }

    // Everyone's crashes share the folder: only this process's, only ones
    // newer than the last scan, and never deleted - they are macOS's.
    [Fact]
    public void Only_this_processs_new_macOS_reports_are_read_and_none_is_deleted()
    {
        var folder = Directory.CreateTempSubdirectory("flower-test-diagnostics").FullName;
        var previous = CrashReportScanner.MacDiagnosticReportsDirectory;
        CrashReportScanner.MacDiagnosticReportsDirectory = folder;
        try
        {
            var ours = Path.Combine(folder, "Flower.Desktop-2026-11-12-090533.ips");
            File.WriteAllText(ours, MacReport);
            File.WriteAllText(Path.Combine(folder, "Safari-2026-11-12-090000.ips"), MacReport);
            var logger = new RecordingLogger<CrashReportScannerTests>();

            // As the process sees itself: a dot in its name, and no extension.
            const string process = "/Applications/Flower/Flower.Desktop";
            var first = CrashReportScanner.ScanMacDiagnosticReports(logger, process);
            var second = CrashReportScanner.ScanMacDiagnosticReports(new RecordingLogger<CrashReportScannerTests>(), process);

            Assert.NotNull(first);
            Assert.Equal(1, logger.CountAt(LogLevel.Critical, "macOS"));
            Assert.Null(second);
            Assert.True(File.Exists(ours));
        }
        finally
        {
            CrashReportScanner.MacDiagnosticReportsDirectory = previous;
            Directory.Delete(folder, recursive: true);
        }
    }
}
