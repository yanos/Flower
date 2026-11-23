using System;
using System.IO;

using Flower.Logging;
using Flower.Services;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests.Logging;

// The desktop's parent process, reporting the app it was waiting on. Sentry
// does not run in a test, so what these see is what it leaves for the next
// launch.
[Collection("PlatformDataDirectory")]
public class CrashWatcherTests : PinnedDataDirectory
{
    private const int ProcessId = 4242;
    private const int SegmentationFault = 139;
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1);

    private static void SkipUnlessCrashReportFiles() =>
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(),
            "Only Linux and macOS leave crash report files.");

    private static string DotNetReport()
    {
        Directory.CreateDirectory(CrashReportScanner.CrashReportsDirectory);
        var path = Path.Combine(CrashReportScanner.CrashReportsDirectory, $"flower-{ProcessId}.dmp.crashreport.json");
        File.WriteAllText(path, """{"payload":{"threads":[{"crashed":"true","stack_frames":[{"filename":"libminiaudio","unmanaged_name":"ma_device_start"}]}]}}""");
        return path;
    }

    private static void Report(int exitCode, DateTimeOffset? startedAt = null) =>
        CrashWatcher.ReportChildCrash(ProcessId, exitCode, startedAt ?? StartedAt, () => true, TimeSpan.Zero);

    [Theory]
    [InlineData(139, false, true)] // SIGSEGV
    [InlineData(134, false, true)] // SIGABRT
    [InlineData(0, false, false)]
    [InlineData(1, false, false)]
    [InlineData(143, false, false)] // SIGTERM: someone quit it
    [InlineData(137, false, false)] // SIGKILL
    [InlineData(unchecked((int)0xC0000005), true, true)] // access violation
    [InlineData(unchecked((int)0xE0434352), true, true)] // .NET's unhandled exception
    [InlineData(unchecked((int)0xC000013A), true, false)] // Ctrl+C
    [InlineData(0, true, false)]
    public void Only_an_exit_that_is_a_crash_counts_as_one(int exitCode, bool onWindows, bool crashed)
    {
        Assert.Equal(crashed, CrashWatcher.IsCrashExit(exitCode, onWindows));
    }

    [Fact]
    public void A_native_crash_is_taken_and_filed_under_the_exact_version_of_this_build()
    {
        SkipUnlessCrashReportFiles();
        var report = DotNetReport();

        Report(SegmentationFault);

        var crash = PendingCrash.Read();
        Assert.NotNull(crash);
        Assert.True(crash.Native);
        Assert.Equal(AppVersion.Full, crash.Version);
        Assert.Contains("ma_device_start", crash.Summary);
        Assert.False(File.Exists(report));
    }

    [Fact]
    public void A_clean_exit_leaves_everything_alone()
    {
        SkipUnlessCrashReportFiles();
        var report = DotNetReport();

        Report(exitCode: 0);

        Assert.Null(PendingCrash.Read());
        Assert.True(File.Exists(report));
    }

    // It went to Sentry as the process died. The report .NET also left for it
    // is taken, or the next launch would call it a second crash.
    [Fact]
    public void An_exception_the_sdk_already_saw_is_not_reported_again()
    {
        SkipUnlessCrashReportFiles();
        var report = DotNetReport();
        var thrown = new PendingCrash("System.InvalidOperationException: no", "at Foo", "0.11.0", null, DateTimeOffset.UtcNow, ReachedSentry: true);
        thrown.Record();

        Report(134);

        Assert.Equal(thrown, PendingCrash.Read());
        Assert.False(File.Exists(report));
    }

    [Fact]
    public void An_exception_from_before_the_sdk_started_gets_this_builds_version()
    {
        SkipUnlessCrashReportFiles();
        new PendingCrash("System.InvalidOperationException: early", "at Main", null, null, DateTimeOffset.UtcNow).Record();

        Report(134);

        var crash = PendingCrash.Read();
        Assert.Equal("System.InvalidOperationException: early", crash?.Summary);
        Assert.Equal(AppVersion.Full, crash?.Version);
    }

    // Left unanswered by an earlier run: this crash is the fresher story.
    [Fact]
    public void An_older_unanswered_crash_gives_way_to_this_one()
    {
        SkipUnlessCrashReportFiles();
        DotNetReport();
        new PendingCrash("Old crash", "at Old", "0.10.0", null, StartedAt.AddDays(-1)).Record();

        Report(SegmentationFault);

        Assert.Contains("ma_device_start", PendingCrash.Read()?.Summary);
    }

    [Fact]
    public void The_crash_goes_in_the_log_of_the_run_that_crashed()
    {
        SkipUnlessCrashReportFiles();
        DotNetReport();
        Directory.CreateDirectory(AppLogging.LogsDirectory);
        var log = Path.Combine(AppLogging.LogsDirectory, "flower-20261122-090000.log");
        File.WriteAllText(log, "last line before the crash\n");

        Report(SegmentationFault);

        Assert.Contains("[FTL] Native crash", File.ReadAllText(log));
        Assert.Equal(log, PendingCrash.Read()?.LogFile);
    }

    [Fact]
    public void A_log_older_than_the_run_is_another_runs_and_is_left_alone()
    {
        SkipUnlessCrashReportFiles();
        DotNetReport();
        Directory.CreateDirectory(AppLogging.LogsDirectory);
        var log = Path.Combine(AppLogging.LogsDirectory, "flower-20261120-090000.log");
        File.WriteAllText(log, "another run\n");
        File.SetLastWriteTimeUtc(log, StartedAt.UtcDateTime.AddHours(-2));

        Report(SegmentationFault);

        Assert.Equal("another run\n", File.ReadAllText(log));
        Assert.Null(PendingCrash.Read()?.LogFile);
    }

    [Theory]
    [InlineData("Faulting application name: Flower.Desktop.exe\nFaulting process id: 0x1a2b\nFaulting module name: x.dll", 0x1a2b)]
    [InlineData("Faulting process id: 1a2b", 0x1a2b)]
    [InlineData("Application: Flower.Desktop.exe\nException Info: System.Exception", null)]
    public void A_Windows_event_says_which_process_crashed_when_it_can(string description, int? processId)
    {
        Assert.Equal(processId, CrashReportScanner.WindowsEventProcessId(description));
    }
}
