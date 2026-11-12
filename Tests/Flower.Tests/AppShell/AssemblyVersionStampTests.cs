using System;
using System.Reflection;

using Flower.Services;

using Xunit;

namespace Flower.Tests.AppShell;

// Every Flower assembly's numeric version is the release it belongs to, not
// MinVer's <major>.0.0.0 - which read 0.0.0.0 for all of 0.x, in a crash
// report's module list and anywhere else an assembly is asked its version.
public class AssemblyVersionStampTests
{
    [Fact]
    public void The_assembly_version_is_the_informational_versions_numbers()
    {
        var release = Version.Parse(AppVersion.StripBuildMetadata(AppVersion.Full).Split('-')[0]);

        Assert.Equal(
            new Version(release.Major, release.Minor, release.Build, 0),
            typeof(AppVersion).Assembly.GetName().Version);
    }
}
