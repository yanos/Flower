namespace Flower.Audio
{
    // The sample formats the canonical pipeline can carry between a decoder
    // and the render sink. Two of them, and the absences are the design.
    //
    // S16 is what LibVLC's amem seam delivers and the only thing it can
    // deliver - see GaplessFormat.LibVlcFourCc. S24 is packed three-byte
    // little-endian, which is what miniaudio's ma_format_s24 takes and what
    // flower-ffmpeg's pack_s24 produces, and it is the real ceiling of every
    // hi-res release and every DAC that plays one.
    //
    // S32 and F32 are deliberately not here. A 32-bit integer source cannot
    // survive OutputStage, which does its arithmetic in float: a float
    // mantissa holds 24 bits exactly and no more, so the bottom eight bits of
    // a true S32 sample would be lost to the EQ and gain stage rather than
    // carried by it - a widening that quietly narrows again. F32 would avoid
    // that but buys nothing a music library contains, since every PCM source
    // Flower decodes is 16- or 24-bit integer and both are exact in a float.
    // Adding either means giving OutputStage a double path, which is a real
    // cost for no reachable source.
    public enum PcmSampleFormat
    {
        S16,
        S24,
    }
}
