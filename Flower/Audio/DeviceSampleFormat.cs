namespace Flower.Audio
{
    // What the output stage requantises to: the sample format the output
    // device was opened in. Not the pipeline's format - the pipeline is float
    // from the decoder to the output stage and has no sample format to choose (see
    // GaplessFormat) - so a device that will not open at 24 bits narrows
    // only this, at any open, without touching a decoder.
    //
    // S24 is packed three-byte little-endian, which is what miniaudio's
    // ma_format_s24 takes and the real ceiling of every hi-res release and
    // every DAC that plays one. S16 is what a device that refuses S24 gets.
    //
    // There is no S32, and that is a fact about float rather than a missing
    // case: a float mantissa holds exactly 24 bits, so a 32-bit device format
    // would carry eight bits the output stage never had. F32 is the one addition
    // that would mean something - most OS mixers are float - and it would
    // move the final quantisation, and its dither, out of Flower and into the
    // OS; see docs/AUDIO-QUALITY-PLAN.md, Phase 6.
    public enum DeviceSampleFormat
    {
        S16,
        S24,
    }

    public static class DeviceSampleFormatExtensions
    {
        public static int BytesPerSample(this DeviceSampleFormat format) =>
            format == DeviceSampleFormat.S24 ? 3 : 2;

        public static int BytesPerFrame(this DeviceSampleFormat format) =>
            format.BytesPerSample() * (int)GaplessFormat.Channels;
    }
}
