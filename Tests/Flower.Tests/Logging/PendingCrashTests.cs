using System;
using System.IO;

using Flower.Logging;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests.Logging;

// What a crash leaves for the next launch to offer reporting, and when that
// offer stops being made.
[Collection("PlatformDataDirectory")]
public class PendingCrashTests : PinnedDataDirectory
{
    private static readonly DateTimeOffset When = new(2026, 11, 12, 9, 30, 0, TimeSpan.Zero);

    private static PendingCrash Thrown()
    {
        try
        {
            throw new InvalidOperationException("The queue was empty\nand a second line");
        }
        catch (InvalidOperationException exception)
        {
            return PendingCrash.FromException(exception, "0.9.0", "/logs/flower-1.log", When);
        }
    }

    [Fact]
    public void An_exception_recorded_as_the_process_dies_is_read_back_by_the_next_launch()
    {
        Thrown().Record();

        var crash = PendingCrash.Read();

        Assert.NotNull(crash);
        Assert.Equal("System.InvalidOperationException: The queue was empty", crash.Summary);
        Assert.Contains(nameof(Thrown), crash.Details);
        Assert.Equal("0.9.0", crash.Version);
        Assert.Equal("/logs/flower-1.log", crash.LogFile);
    }

    // On Linux and macOS a dying exception also leaves a .NET crash report,
    // found at the next launch; the exception says more.
    [Fact]
    public void A_native_crash_found_at_launch_does_not_replace_the_exception_already_recorded()
    {
        Thrown().Record();

        new PendingCrash("Native crash", "frames", null, null, When).RecordUnlessPending();

        Assert.Equal("System.InvalidOperationException: The queue was empty", PendingCrash.Read()?.Summary);
    }

    [Fact]
    public void A_native_crash_is_recorded_when_nothing_else_was()
    {
        new PendingCrash("Native crash", "frames", null, null, When).RecordUnlessPending();

        Assert.Equal("Native crash", PendingCrash.Read()?.Summary);
    }

    [Fact]
    public void An_unreadable_record_is_dropped_rather_than_offered()
    {
        Directory.CreateDirectory(AppLogging.LogsDirectory);
        File.WriteAllText(PendingCrash.FilePath, "{ half a record");

        Assert.Null(PendingCrash.Read());
        Assert.False(File.Exists(PendingCrash.FilePath));
    }

    // Reported or dismissed, the banner is answered: the next launch has
    // nothing to ask about.
    [Fact]
    public void Answering_the_banner_closes_it_and_the_next_launch_does_not_ask_again()
    {
        Thrown().Record();
        var offer = new CrashReportOfferViewModel(PendingCrash.Read()!, "0.9.1", "macOS", "/Users/someone");

        offer.AnswerCommand.Execute(null);

        Assert.False(offer.IsOpen);
        Assert.Null(PendingCrash.Read());
    }

    // A crash found only at the next launch belongs to the run before it,
    // whose log is the newest one that is not this run's own.
    [Fact]
    public void The_previous_runs_log_is_the_newest_other_than_this_ones()
    {
        Directory.CreateDirectory(AppLogging.LogsDirectory);
        string Log(string stamp)
        {
            var path = Path.Combine(AppLogging.LogsDirectory, $"flower-{stamp}.log");
            File.WriteAllText(path, "");
            return path;
        }

        Log("20261110-080000");
        var previous = Log("20261111-080000");
        var current = Log("20261112-080000");

        Assert.Equal(previous, PendingCrash.PreviousLogFile(current));
    }
}
