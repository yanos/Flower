using System.Linq;

using Flower.DeviceChecks;

using Xunit;

namespace Flower.Tests;

// The device checks, run here. Same code, same fixtures, same oracle as
// Flower.DeviceChecks.iOS runs on a simulator or a phone - see DecodeChecks
// for why they are written to be portable at all.
//
// This is the desktop and CI half of that pair. It is not redundant with the
// device run: the whole value is that the two can disagree, and every
// streaming bug so far has been one that only the phone could see. Running
// them here is what makes a disagreement mean "the platform", rather than
// "nobody ran the same thing twice".
[Trait("Category", "RequiresLibVLC")]
[Collection("LibVLC")]
public class DeviceChecksTests
{
    private readonly ITestOutputHelper _output;

    public DeviceChecksTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void This_platform_decodes_local_and_streamed_tracks_correctly()
    {
        var results = DecodeChecks.RunAll();

        foreach (var result in results)
            _output.WriteLine(result.ToString());

        var failed = results.Where(result => !result.Passed).ToList();
        Assert.True(failed.Count == 0, string.Join("\n", failed.Select(result => result.ToString())));
    }
}
