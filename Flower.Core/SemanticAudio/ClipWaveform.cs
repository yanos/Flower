using System;

namespace Flower.SemanticAudio;

// One grid window turned into what the audio encoder takes: LAION's own
// get_audio_embedding_from_data, which is short because the spectrogram is
// inside the exported graph (docs/VIBE-PLAYLIST-PLAN.md, "Preprocessing").
//
// Nothing here fails when it is wrong. A waveform that differs from Python's
// gives an embedding that is subtly off, and the playlists are just worse, so
// the reference test (ClipWaveformReferenceTests) is the judge of this code,
// not reading it.
public static class ClipWaveform
{
    // The int16 round trip: clamp to [-1, 1], scale by 32767, truncate toward
    // zero, scale back. LAION's training data went through it, and so does
    // every clip laion_clap embeds.
    public static short ToInt16(float sample)
    {
        var clamped = Math.Clamp(sample, -1f, 1f);
        return (short)(clamped * 32767f);
    }

    // 10 s of 48 kHz mono, ready for the encoder. A window shorter than that -
    // only a song's last can be - is repeat-padded: whole copies end to end,
    // then zeros (laion_clap's "repeatpad").
    public static float[] Prepare(ReadOnlySpan<float> window)
    {
        if (window.IsEmpty)
            throw new ArgumentException("A clip needs at least one sample.", nameof(window));

        var length = Math.Min(window.Length, Clap.ClipSamples);
        var prepared = new float[Clap.ClipSamples];
        for (var i = 0; i < length; i++)
            prepared[i] = ToInt16(window[i]) / 32767f;

        if (length < Clap.ClipSamples)
        {
            var copies = Clap.ClipSamples / length;
            for (var copy = 1; copy < copies; copy++)
                Array.Copy(prepared, 0, prepared, copy * length, length);
        }

        return prepared;
    }
}
