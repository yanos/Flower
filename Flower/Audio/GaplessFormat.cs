namespace Flower.Audio
{
    // The one canonical PCM format every track is decoded to before it ever
    // reaches a GaplessRingBuffer - fixed for the life of a session so that a
    // track boundary is never a format change, which is what lets a render
    // sink stay continuous across tracks instead of reconfiguring/glitching at
    // each transition.
    //
    // Interleaved stereo float32, normalised to +-1, from the decoder to the
    // output stage. Float because that is what nearly every source already is
    // - AAC, MP3, Vorbis and Opus all decode to float natively - and what the
    // output stage does its arithmetic in, so the only conversion left is the
    // one at the device write. It used to be packed S24, which put a round
    // trip through integer in front of the EQ, and the sample format had to be
    // negotiated with the device and frozen, because a decoder already open
    // cannot change format. A device's format is now the output stage's business
    // alone (DeviceSampleFormat). Float loses nothing on the way in: 16- and
    // 24-bit integer PCM are exact in a float's 24-bit mantissa.
    //
    // The sample rate is still negotiated once at startup and then frozen. It
    // comes from the output device, so the endpoint's own rate is the
    // pipeline's and nothing resamples on the way out, and it stays frozen
    // for the reason the format no longer has to: current and armed decoders
    // cannot change rate underneath the ring they share. See
    // MiniaudioSink.OpenDevice's _hasNegotiatedRate.
    //
    // What was negotiated is a PcmFormat, which the sink reports and each
    // piece of the pipeline is handed. This class keeps only what does not
    // vary.
    public static class GaplessFormat
    {
        // The fallback also keeps test fixtures and headless sinks
        // deterministic. MiniaudioSink replaces it with the opened output
        // device's native rate before any decoder is constructed, avoiding its otherwise unavoidable second resample.
        public const uint DefaultSampleRate = 48000;

        public const uint Channels = 2;

        public const int BytesPerSample = sizeof(float);
        public const int BytesPerFrame = BytesPerSample * (int)Channels;
    }
}
