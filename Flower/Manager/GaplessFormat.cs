using System.Threading;

namespace Flower.Manager
{
    // The one canonical PCM format every track is decoded to before it ever
    // reaches a GaplessRingBuffer - fixed so that a track boundary is never a
    // format change, which is what lets a render sink stay continuous across
    // tracks instead of reconfiguring/glitching at each transition. LibVLC's
    // own resampler/channel-mixer performs the normalization, so no
    // hand-rolled conversion is needed.
    //
    // S16N (16-bit signed, host-endian), not FL32: confirmed against LibVLC
    // 3.0.x's own source that its amem audio-output module - the module
    // SetAudioCallbacks/SetAudioFormat actually go through - hardcodes S16N
    // and never reads back the requested fourcc at all (the fix that makes
    // FL32/S32N real landed in the 4.0 line, never backported to 3.0.x,
    // which is what every VideoLAN.LibVLC.* package here bundles). The play
    // callback hands over 16-bit int PCM regardless of what's requested, so
    // the pipeline has to actually be built around that rather than the
    // requested-but-unhonored format.
    public static class GaplessFormat
    {
        public const string LibVlcFourCc = "S16N";
        // The fallback also keeps test fixtures and headless sinks
        // deterministic. MiniaudioSink replaces the process's session rate
        // with the opened output device's native rate before any decoder is
        // constructed, avoiding its otherwise unavoidable second resample.
        public const uint DefaultSampleRate = 48000;
        private static uint _sampleRate = DefaultSampleRate;

        public static uint SampleRate => Volatile.Read(ref _sampleRate);

        internal static void ConfigureSampleRate(uint sampleRate)
        {
            if (sampleRate != 0)
                Volatile.Write(ref _sampleRate, sampleRate);
        }

        public const uint Channels = 2;
        public const int BytesPerSample = 2;
        public const int BytesPerFrame = BytesPerSample * (int)Channels;
    }
}
