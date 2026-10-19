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
    //   whole reason ffaudio exists: LibVLC's amem module hardcodes S16N
    //   and never reads back the requested fourcc at all, so a pipeline
    //   carrying 24 bits over it would have been carrying eight zeroes and
    //   calling it hi-res.
    //
    // Frozen rather than merely defaulted: a decoder already open cannot
    // change format, so a device change mid-session keeps the negotiated one.
    // See MiniaudioSink.OpenDevice's _hasNegotiatedFormat.
    //
    // What was negotiated is a PcmFormat, which the sink reports and each
    // piece of the pipeline is handed. This class keeps only what does not
    // vary: the defaults, the channel count and the widest frame.
    public static class GaplessFormat
    {
        // The fallback also keeps test fixtures and headless sinks
        // deterministic. MiniaudioSink replaces it with the opened output
        // device's native rate before any decoder is constructed, avoiding its otherwise unavoidable second resample.
        public const uint DefaultSampleRate = 48000;

        // What the pipeline carries until something negotiates otherwise -
        // which is to say for every device check and every test that does
        // not say otherwise. The app negotiates S24 at startup
        // (GaplessAudioManager.StartSink).
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

        public static int BytesPerSampleOf(PcmSampleFormat format) =>
            format == PcmSampleFormat.S24 ? 3 : 2;
    }
}
