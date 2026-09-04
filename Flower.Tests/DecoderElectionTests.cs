using System;

using System.Text.Json;

using Flower.Audio;
using Flower.Audio.Ffmpeg;
using Flower.Persistence;

namespace Flower.Tests;

// Which decoder runs, and therefore how many bits the pipeline carries.
//
// The election has to be able to say no. flower_ffmpeg is built for macOS and
// nothing else so far (native/ffmpeg/README.md), so a preference for it is a
// preference that four of the five platform heads cannot honour - and the
// answer to that has to be LibVLC and a warning in the log, not a decode
// thread throwing DllNotFoundException the moment somebody presses play.
public class DecoderElectionTests
{
    private static T WithEnvironment<T>(string? value, Func<T> body)
    {
        var previous = Environment.GetEnvironmentVariable(DecoderElection.EnvironmentVariable);
        Environment.SetEnvironmentVariable(DecoderElection.EnvironmentVariable, value);
        try
        {
            return body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(DecoderElection.EnvironmentVariable, previous);
        }
    }

    // The decoder decides the format, and this is the pairing that makes that
    // true: LibVLC's amem module hardcodes S16N and never reads back the
    // fourcc it was asked for, so electing it and then widening the pipeline
    // would be carrying eight zero bits and calling them hi-res.
    [Fact]
    public void The_libvlc_decoder_pins_the_pipeline_to_16_bits()
    {
        Assert.Equal(PcmSampleFormat.S16, DecoderElection.CanonicalFormatFor(TrackDecoderKind.LibVlc));
        Assert.Equal(PcmSampleFormat.S24, DecoderElection.CanonicalFormatFor(TrackDecoderKind.Ffmpeg));
    }

    [Fact]
    public void The_libvlc_decoder_is_always_available()
    {
        Assert.Equal(TrackDecoderKind.LibVlc, WithEnvironment(null, () => DecoderElection.Resolve(TrackDecoderKind.LibVlc)));
    }

    // Asserted as a relationship rather than as a value, because the answer is
    // genuinely different on a machine that has run native/ffmpeg/macos/
    // build.sh and one that has not - which includes CI. A test that hardcoded
    // either would be testing the build environment.
    [Fact]
    public void The_ffmpeg_decoder_is_elected_exactly_when_the_facade_is_loadable()
    {
        var elected = WithEnvironment(null, () => DecoderElection.Resolve(TrackDecoderKind.Ffmpeg));

        Assert.Equal(
            FfmpegDecoder.IsAvailable ? TrackDecoderKind.Ffmpeg : TrackDecoderKind.LibVlc,
            elected);
    }

    // The point of the fallback: asking for a decoder that is not there must
    // not widen the pipeline, because nothing would then be able to fill it.
    [Fact]
    public void A_decoder_that_is_not_there_does_not_widen_the_pipeline()
    {
        var elected = WithEnvironment(null, () => DecoderElection.Resolve(TrackDecoderKind.Ffmpeg));

        if (FfmpegDecoder.IsAvailable)
            return;

        Assert.Equal(PcmSampleFormat.S16, DecoderElection.CanonicalFormatFor(elected));
    }

    [Fact]
    public void The_environment_overrides_the_persisted_preference()
    {
        Assert.Equal(
            FfmpegDecoder.IsAvailable ? TrackDecoderKind.Ffmpeg : TrackDecoderKind.LibVlc,
            WithEnvironment("ffmpeg", () => DecoderElection.Resolve(TrackDecoderKind.LibVlc)));

        // And in the other direction, which is the one that matters while
        // FFmpeg is the thing being tried out: a bad session has to be
        // recoverable without editing settings.json back.
        Assert.Equal(
            TrackDecoderKind.LibVlc,
            WithEnvironment("LibVlc", () => DecoderElection.Resolve(TrackDecoderKind.Ffmpeg)));
    }

    // settings.json is the only way to set this - there is deliberately no
    // picker - so it has to survive the round trip, and as a name rather than
    // an ordinal, since the file is meant to be read and edited by a person.
    [Fact]
    public void The_preference_persists_as_a_name()
    {
        var written = JsonSerializer.Serialize(
            new AppSettings { AudioDecoder = TrackDecoderKind.Ffmpeg },
            FlowerJsonContext.Default.AppSettings);

        Assert.Contains("\"AudioDecoder\":\"Ffmpeg\"", written);

        var read = JsonSerializer.Deserialize(written, FlowerJsonContext.Default.AppSettings)!;
        Assert.Equal(TrackDecoderKind.Ffmpeg, read.AudioDecoder);

        // And an absent key is the safe default rather than whatever zero
        // happens to mean today.
        Assert.Equal(
            TrackDecoderKind.LibVlc,
            JsonSerializer.Deserialize("{}", FlowerJsonContext.Default.AppSettings)!.AudioDecoder);
    }

    // A typo in an environment variable is not a reason to change decoders.
    [Fact]
    public void An_unrecognised_override_leaves_the_preference_alone()
    {
        Assert.Equal(
            TrackDecoderKind.LibVlc,
            WithEnvironment("libavcodec", () => DecoderElection.Resolve(TrackDecoderKind.LibVlc)));
    }
}
