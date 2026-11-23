using System;

using Flower.Logging;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests.Logging;

// A crash the SDK did not see is sent by another process, perhaps of another
// build: it is filed under the one that crashed, and only if that was a
// release.
[Collection("PlatformDataDirectory")]
public class NativeCrashReleaseTests : PinnedDataDirectory
{
    private static PendingCrash Crash(string? version) =>
        new("SIGABRT", "  at abort", version, null, DateTimeOffset.UnixEpoch, Native: true);

    [Theory]
    [InlineData("0.11.0", "0.11.0")]
    [InlineData("0.11.0+e38ad700", "0.11.0")]
    public void A_crash_is_filed_under_the_release_that_crashed(string crashed, string release)
    {
        var sentryEvent = CrashReporting.RecordedEvent(Crash(crashed));

        Assert.NotNull(sentryEvent);
        Assert.Equal(release, sentryEvent.Release);
    }

    [Theory]
    [InlineData("0.11.1-alpha.0.41+d6ff2b7f")]
    [InlineData("0.12.0-beta.1+d6ff2b7f")]
    [InlineData("0.12.0-beta.1.3+d6ff2b7f")]
    [InlineData("0.12.0-prerelease")]
    public void A_pre_release_crash_found_by_a_release_is_not_sent(string crashed)
    {
        Assert.Null(CrashReporting.RecordedEvent(Crash(crashed)));
    }

    // Better a rare pre-release's report than a lost crash, and under a
    // release of its own rather than blaming the version that found it.
    [Fact]
    public void A_crash_of_unknown_build_is_still_sent_and_says_so()
    {
        var sentryEvent = CrashReporting.RecordedEvent(Crash(null));

        Assert.NotNull(sentryEvent);
        Assert.Equal(CrashReporting.UnknownRelease, sentryEvent.Release);
        Assert.True(sentryEvent.Extra.ContainsKey("reported_by_version"));
    }

    // MetricKit's record of the crashed build, whatever is running now.
    [Theory]
    [InlineData("0.11.0", true)]
    [InlineData("0.12.0-beta.1", false)]
    [InlineData("0.10.2-alpha.0.31", false)]
    public void An_iPhone_crash_is_sent_only_for_a_release(string bundleVersion, bool sent)
    {
        var crash = CrashReportScanner.DescribeMetricKitCrash(11, 1, null, null, DateTimeOffset.UnixEpoch, bundleVersion);

        Assert.Equal(sent, CrashReporting.RecordedEvent(crash) != null);
    }
}
