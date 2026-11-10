using System.Linq;

using Flower.Tests.Decoding.Scenarios;

using Xunit;

namespace Flower.Tests.Audio;

// NativeOutputStageChecks, as a fact: the C output stage in
// libflowerbridge held to OutputStage, byte for byte. The checks are portable
// so the phone runners can run them too; this is how they run on each
// desktop, against the build FlowerBridge.props copies beside the tests, and
// inside the Mono iOS suite (scripts/ios-tests.sh), which links the framework
// Flower.iOS ships.
public class NativeOutputStageTests
{
    private readonly ITestOutputHelper _output;

    public NativeOutputStageTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void The_native_output_stage_renders_what_OutputStage_renders()
    {
        var results = NativeOutputStageChecks.RunAll();

        foreach (var result in results)
            _output.WriteLine(result.ToString());

        var failed = results.Where(result => !result.Passed).ToList();
        Assert.True(failed.Count == 0, string.Join("\n", failed.Select(result => result.ToString())));
    }
}
