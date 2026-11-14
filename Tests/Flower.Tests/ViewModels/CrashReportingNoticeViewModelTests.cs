using Flower.Persistence;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests.ViewModels;

// The once-only notice that crash reports are on. What matters is that it is
// asked exactly once, that either answer counts, and that "Turn Off" really
// turns reporting off.
public class CrashReportingNoticeViewModelTests
{
    private sealed class Host
    {
        public AppSettings Settings { get; } = new();
        public int Saves { get; private set; }

        public CrashReportingNoticeViewModel? Offer() =>
            CrashReportingNoticeViewModel.OfferFor(Settings, () => Settings.SendCrashReports = false, () => Saves++);
    }

    [Fact]
    public void Closing_it_keeps_reports_on_and_is_not_asked_again()
    {
        var host = new Host();

        host.Offer()!.KeepCommand.Execute(null);

        Assert.True(host.Settings.SendCrashReports);
        Assert.Null(host.Offer());
        Assert.Equal(1, host.Saves);
    }

    [Fact]
    public void Turning_reports_off_from_it_turns_them_off_and_is_not_asked_again()
    {
        var host = new Host();

        var notice = host.Offer()!;
        notice.TurnOffCommand.Execute(null);

        Assert.False(notice.IsOpen);
        Assert.False(host.Settings.SendCrashReports);
        Assert.Null(host.Offer());
    }

    // Turned off in Settings first: there is nothing left to tell.
    [Fact]
    public void An_install_with_reports_already_off_is_not_told()
    {
        var host = new Host();
        host.Settings.SendCrashReports = false;

        Assert.Null(host.Offer());
    }

    // The Settings switch closes an open notice through Close; the notice's
    // own Turn Off then lands on an already-closed one. Saved once, not twice.
    [Fact]
    public void Answering_twice_counts_once()
    {
        var host = new Host();
        var notice = host.Offer()!;

        notice.Close();
        notice.TurnOffCommand.Execute(null);

        Assert.Equal(1, host.Saves);
    }
}
