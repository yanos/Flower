using System.Threading;

namespace Flower.Audio
{
    // The one canonical PCM format every track is decoded to before it ever
    // reaches a GaplessRingBuffer - fixed for the life of a session so that a
    // track boundary is never a format change, which is what lets a render
    // sink stay continuous across tracks instead of reconfiguring/glitching at
    // each transition.
    //
    // Two of its three parts are negotiated once at startup and then frozen:
    //
    // - The sample rate comes from the output device, so the endpoint's own
    //   rate is the pipeline's and nothing resamples on the way out.
    // - The sample format comes from the *decoder*, capped by what the device
    //   would open with. That is the direction it has to run, and it is the
    //   whole reason flower-ffmpeg exists: LibVLC's amem module hardcodes S16N
    //   and never reads back the requested fourcc at all, so a pipeline
    //   carrying 24 bits over it would have been carrying eight zeroes and
    //   calling it hi-res.
    //
    // Frozen rather than merely defaulted: a decoder already open cannot
    // change format, so a device change mid-session keeps the negotiated one.
    // See MiniaudioSink.OpenDevice's _hasNegotiatedFormat.
    public static class GaplessFormat
    {
        // What TrackDecoder asks LibVLC for, and gets whether it asks or not.
        public const string LibVlcFourCc = "S16N";

        // The fallback also keeps test fixtures and headless sinks
        // deterministic. MiniaudioSink replaces the process's session rate
        // with the opened output device's native rate before any decoder is
        // constructed, avoiding its otherwise unavoidable second resample.
        public const uint DefaultSampleRate = 48000;

        // What the pipeline carries until something negotiates otherwise -
        // which is to say what it carries for the LibVLC decoder, for every
        // device check, and for every test that does not say otherwise.
        public const PcmSampleFormat DefaultSampleFormat = PcmSampleFormat.S16;

        public const uint Channels = 2;

        // For buffers that must be allocated before the negotiation above has
        // happened - the shared ring is built and handed to the sink whose
        // opening of the device is what decides the format. Sizing those in
        // the widest frame the pipeline can carry costs a few hundred
        // kilobytes and keeps the buffer's depth *in time* from silently
        // halving when the format widens.
        public const int MaxBytesPerSample = 3;
        public const int MaxBytesPerFrame = MaxBytesPerSample * (int)Channels;

        private static uint _sampleRate = DefaultSampleRate;
        private static int _sampleFormat = (int)DefaultSampleFormat;

        public static uint SampleRate => Volatile.Read(ref _sampleRate);

        public static PcmSampleFormat SampleFormat => (PcmSampleFormat)Volatile.Read(ref _sampleFormat);

        public static int BytesPerSample => BytesPerSampleOf(SampleFormat);

        public static int BytesPerFrame => BytesPerSample * (int)Channels;

        public static int BytesPerSampleOf(PcmSampleFormat format) =>
            format == PcmSampleFormat.S24 ? 3 : 2;

        // Both configured before the first decoder exists and not touched
        // afterwards, so neither needs to be safe against a decode in flight -
        // the Volatile pair is for the render callback's read, not for a
        // change underneath it.
        internal static void ConfigureSampleRate(uint sampleRate)
        {
            if (sampleRate != 0)
                Volatile.Write(ref _sampleRate, sampleRate);
        }

        internal static void ConfigureSampleFormat(PcmSampleFormat format) =>
            Volatile.Write(ref _sampleFormat, (int)format);
    }
}
