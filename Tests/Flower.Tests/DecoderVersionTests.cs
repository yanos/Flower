using System.Text.RegularExpressions;

using FFAudio;

using Flower.Audio.Ffmpeg;

using Xunit;

namespace Flower.Tests;

// The version line on the About window and the phone's settings. Against the
// built façade, so that the FFmpeg half is asked of a real binary - a façade
// missing the export would still show a line, just without that half, and
// this is what notices.
[Trait("Category", "RequiresFfmpeg")]
public class DecoderVersionTests
{
    [Fact]
    public void Names_ffaudio_and_the_ffmpeg_inside_it()
    {
        Assert.True(Decoder.IsAvailable, "ffaudio is not loadable here");

        var line = DecoderVersion.Display;

        Assert.NotNull(line);
        Assert.Matches(new Regex(@"^ffaudio \S+ · FFmpeg \S+"), line);
    }
}
