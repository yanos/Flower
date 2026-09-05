using System;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

using Flower.Audio.Ffmpeg;

namespace Flower.Audio
{
    // Which of the two ITrackDecoder implementations decodes this session.
    [JsonConverter(typeof(JsonStringEnumConverter<TrackDecoderKind>))]
    public enum TrackDecoderKind
    {
        // LibVLC through its amem seam. The decoder the app has always used,
        // available on every platform, and permanently 16-bit - see
        // GaplessFormat and docs/AUDIOPHILE-PLAN.md's "The 16-bit ceiling,
        // measured".
        LibVlc,

        // flower-ffmpeg. Carries 24 bits, does its own demuxing, and answers a
        // seek with where it landed rather than making the caller correlate
        // callbacks after the fact - on every platform with a built artifact,
        // which is now all five (native/ffmpeg/README.md).
        Ffmpeg,
    }

    // Turns a preference into the decoder that is actually going to run, and
    // the canonical PCM format that follows from it.
    //
    // The direction is the point: the decoder decides the format, not the
    // device. LibVLC's amem module hardcodes S16N and never reads back the
    // fourcc it was asked for, so a pipeline carrying 24 bits over the LibVLC
    // decoder would be carrying eight zeroes and calling it hi-res. Widening
    // is therefore only meaningful for the decoder that can fill the width,
    // and MiniaudioSink is left with a veto rather than a vote - if a device
    // will not open at 24 bits, it says so and the pipeline narrows back.
    //
    // A preference rather than a switch, because electing FFmpeg can fail for
    // an ordinary reason - there is no built artifact for this platform - and
    // silently playing nothing would be a worse answer than playing through
    // LibVLC and saying so in the log.
    public static class DecoderElection
    {
        // Overrides the persisted setting for one run, so an A/B against the
        // same build is an environment variable rather than an edit to
        // settings.json and a relaunch. Same idiom as FLOWER_FFMPEG, which
        // points the loader at a particular façade build.
        public const string EnvironmentVariable = "FLOWER_DECODER";

        public static TrackDecoderKind Resolve(TrackDecoderKind preferred, ILogger? logger = null)
        {
            if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } requested)
            {
                if (Enum.TryParse<TrackDecoderKind>(requested, ignoreCase: true, out var overridden))
                {
                    logger?.LogInformation("{Variable}={Requested} overrides the configured decoder", EnvironmentVariable, requested);
                    preferred = overridden;
                }
                else
                {
                    logger?.LogWarning("{Variable}={Requested} names no decoder; using the configured one", EnvironmentVariable, requested);
                }
            }

            if (preferred != TrackDecoderKind.Ffmpeg)
                return TrackDecoderKind.LibVlc;

            if (!FfmpegDecoder.IsAvailable)
            {
                logger?.LogWarning(
                    "The FFmpeg decoder was asked for but flower_ffmpeg is not loadable here; decoding through LibVLC instead");
                return TrackDecoderKind.LibVlc;
            }

            return TrackDecoderKind.Ffmpeg;
        }

        // What the pipeline can carry end to end with this decoder in it -
        // subject to the output device accepting it, which is MiniaudioSink's
        // half of the negotiation.
        public static PcmSampleFormat CanonicalFormatFor(TrackDecoderKind kind) =>
            kind == TrackDecoderKind.Ffmpeg ? PcmSampleFormat.S24 : PcmSampleFormat.S16;

        // What FfmpegTrackDecoder asks the façade to hand back. Kept here
        // rather than in that class so the mapping between the pipeline's
        // format and FFmpeg's own lives beside the election that chose it.
        public static FfmpegSampleFormat FfmpegFormatFor(PcmSampleFormat format) =>
            format == PcmSampleFormat.S24 ? FfmpegSampleFormat.S24 : FfmpegSampleFormat.S16;
    }
}
