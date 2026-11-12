using System.IO;

using Flower.Logging;
using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests.Logging;

// When crash reporting runs at all, and the switch that turns it off.
[Collection("PlatformDataDirectory")]
public class CrashReportingSwitchTests : PinnedDataDirectory
{
    // Every untagged build is a MinVer pre-release, and so is a tagged beta.
    [Theory]
    [InlineData("1.2.0", false)]
    [InlineData("1.2.0+6a91fb55", false)]
    [InlineData("0.9.1-alpha.0.14+6a91fb55", true)]
    [InlineData("1.0.0-beta.1", true)]
    public void Only_a_release_version_reports(string version, bool prerelease)
    {
        Assert.Equal(prerelease, AppVersion.IsPrerelease(version));
    }

    [Fact]
    public void Crash_reports_are_on_when_nothing_says_otherwise()
    {
        Assert.True(AppSettingsStore.ReadSendCrashReports());
    }

    [Fact]
    public void Crash_reports_turned_off_in_settings_are_off_from_the_next_launchs_first_moment()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppSettingsStore.StorePath)!);
        File.WriteAllText(AppSettingsStore.StorePath, """{ "SendCrashReports": false }""");

        Assert.False(AppSettingsStore.ReadSendCrashReports());
    }

    // The install's identity counts installs; it is the same one every time.
    [Fact]
    public void An_install_keeps_one_identity()
    {
        Assert.Equal(CrashReportingId.Current, CrashReportingId.Current);
    }

    // Off and on again is a stranger: nothing links the reports after to
    // the ones before.
    [Fact]
    public void Switching_reports_off_forgets_the_installs_identity()
    {
        var before = CrashReportingId.Current;

        CrashReporting.Enabled = false;
        CrashReporting.Enabled = true;

        Assert.NotEqual(before, CrashReportingId.Current);
    }
}
