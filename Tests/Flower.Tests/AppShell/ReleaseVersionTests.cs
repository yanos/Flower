using Flower.Services;

using Xunit;

namespace Flower.Tests.AppShell;

// Whether a release is newer than the build running - the dmg's own check
// (GitHubReleaseUpdater) compares a tag with AppVersion, and gets it wrong in
// the ways that matter if it compares strings, or treats a pre-release as the
// release it leads up to.
public class ReleaseVersionTests
{
    [Theory]
    [InlineData("v0.5.0", "0.4.1")]
    [InlineData("v0.10.0", "0.9.0")]               // not compared as text
    [InlineData("v1.0.0", "0.99.99")]
    [InlineData("v0.5.0", "0.5.0-rc.1")]           // the release comes after its pre-releases
    [InlineData("v0.5.0", "0.4.1-alpha.0.227+dcef049")] // MinVer's commit is ignored
    [InlineData("v0.5.0-rc.2", "0.5.0-rc.1")]
    [InlineData("v0.5.0-rc.10", "0.5.0-rc.9")]     // numeric identifiers as numbers
    [InlineData("v0.5.0-rc.1", "0.5.0-rc")]        // more identifiers come later
    [InlineData("v0.5.0-beta", "0.5.0-1")]         // words come after numbers
    public void Newer(string candidate, string current) =>
        Assert.True(ReleaseVersion.IsNewer(candidate, current));

    [Theory]
    [InlineData("v0.4.1", "0.4.1")]
    [InlineData("v0.4.1", "0.4.1+dcef049")]
    [InlineData("v0.4.0", "0.4.1")]
    [InlineData("v0.5.0-rc.1", "0.5.0")]
    [InlineData("nightly", "0.4.1")]               // not a version: never offered
    [InlineData("v0.5.0", "unknown")]
    public void Not_newer(string candidate, string current) =>
        Assert.False(ReleaseVersion.IsNewer(candidate, current));
}
