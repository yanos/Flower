using Flower.Services;

using Xunit;

namespace Flower.Tests.AppShell;

// What every build carries for its licences - see OpenSourceNotices. A phone
// app has no folder beside it, so if the embedded copy goes missing (a
// renamed file, a dropped EmbeddedResource) nothing else would notice that
// the app had stopped carrying FFmpeg's licence.
public class OpenSourceNoticesTests
{
    [Fact]
    public void Every_build_carries_the_notice_and_both_licence_texts()
    {
        var text = OpenSourceNotices.Text;

        Assert.Contains("LGPL-2.1-or-later: FFmpeg", text);
        Assert.Contains("GNU LESSER GENERAL PUBLIC LICENSE", text);
        Assert.Contains("Version 2.1, February 1999", text);
        Assert.Contains("Apache License", text);
        Assert.Contains("TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION", text);
    }
}
