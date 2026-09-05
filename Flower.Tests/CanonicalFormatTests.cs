using System;
using System.Runtime.InteropServices;

using Flower.Audio;
using Flower.Audio.Ffmpeg;

using Miniaudio;

namespace Flower.Tests;

// The canonical PCM format, now that it is more than one format.
//
// LibVLC's amem seam truncates every track to 16 bits before Flower sees a
// byte of it (docs/AUDIOPHILE-PLAN.md, "The 16-bit ceiling, measured"), and
// flower-ffmpeg exists to get past that. But moving the ceiling out of LibVLC
// is not the same as removing it: GaplessFormat.BytesPerSample was a const 2,
// and every stage between the decoder and the sound card was built on it, so a
// 24-bit decode would have been narrowed one stage later by the pipeline
// itself. FfmpegDecoderTests proves the decoder can carry 24 bits; this proves
// the pipeline can.
//
// Everything here is arithmetic over spans - no device, no decoder, no LibVLC.
public class CanonicalFormatTests
{
    private const int Channels = (int)GaplessFormat.Channels;

    // Deliberately not GaplessFormat's own constants: the point of these is to
    // fail if the layout ever stops being packed three-byte little-endian,
    // which is what miniaudio's ma_format_s24 and the façade's pack_s24 both
    // mean by S24.
    private const int S24Max = 8_388_607;
    private const int S24Min = -8_388_608;

    private static AudioTimingSettings Silent() => new()
    {
        GainRampMs = 0,
        DeclickFadeMs = 0,
        TransportFadeMs = 0,
        FadeOutWaitMs = 0,
        PrebufferMs = 0,
    };

    private static byte[] PackS24(ReadOnlySpan<int> samples)
    {
        var bytes = new byte[samples.Length * 3];
        for (var i = 0; i < samples.Length; i++)
        {
            bytes[i * 3] = (byte)samples[i];
            bytes[i * 3 + 1] = (byte)(samples[i] >> 8);
            bytes[i * 3 + 2] = (byte)(samples[i] >> 16);
        }

        return bytes;
    }

    private static int[] UnpackS24(ReadOnlySpan<byte> bytes)
    {
        var samples = new int[bytes.Length / 3];
        for (var i = 0; i < samples.Length; i++)
        {
            var value = bytes[i * 3] | (bytes[i * 3 + 1] << 8) | (bytes[i * 3 + 2] << 16);
            if ((value & 0x00800000) != 0)
                value |= unchecked((int)0xFF000000);

            samples[i] = value;
        }

        return samples;
    }

    // Past the declick fade-in that the first buffer of any new generation
    // gets, so a test measuring something else is not measuring that.
    private static OutputStage Stage(PcmSampleFormat format)
    {
        var stage = new OutputStage(GaplessFormat.SampleRate, format) { Timing = Silent() };
        stage.TargetGain = 1f;
        stage.Process(new byte[4800 * Channels * GaplessFormat.BytesPerSampleOf(format)], generation: 1);
        return stage;
    }

    // The same bar OutputStageTests holds the S16 path to, at 24: widening to
    // float, filtering, ramping and requantising is only worth doing if it is
    // transparent when asked to do nothing.
    //
    // It is not a foregone conclusion at this width. A float mantissa holds 24
    // bits, so S24 is the widest integer format that survives this round trip
    // exactly - which is why PcmSampleFormat stops here rather than at S32.
    [Fact]
    public void At_24_bits_a_unity_gain_pass_is_bit_identical()
    {
        var stage = Stage(PcmSampleFormat.S24);

        var samples = new int[4800 * Channels];
        for (var i = 0; i < samples.Length; i++)
        {
            // A ramp that reaches full scale in both directions and carries
            // meaningful data in its bottom eight bits - the bits that only
            // exist at all above 16.
            samples[i] = (int)(S24Max * Math.Sin(2 * Math.PI * 997 * i / (double)samples.Length)) + (i % 251) - 125;
            samples[i] = Math.Clamp(samples[i], S24Min, S24Max);
        }

        var input = PackS24(samples);
        var buffer = (byte[])input.Clone();
        stage.Process(buffer, generation: 1);

        Assert.Equal(input, buffer);
    }

    // The classic packed-S24 bug, and the reason UnpackS24 above exists twice
    // over: three bytes carry no sign bit in the 32-bit sense, so a negative
    // sample read without sign extension comes back as a large positive one -
    // full-scale positive noise in place of quiet audio.
    [Fact]
    public void Negative_24_bit_samples_survive_the_round_trip()
    {
        var stage = Stage(PcmSampleFormat.S24);

        int[] samples = [S24Min, -1, -256, -8_388_607, 0, 1, 256, S24Max];
        var buffer = PackS24(samples);
        stage.Process(buffer, generation: 1);

        Assert.Equal(samples, UnpackS24(buffer));
    }

    // What the widening is actually for, stated as the thing a listener would
    // notice: signal quiet enough to fall off the bottom of 16 bits is still
    // there at 24.
    //
    // -96dBFS is one LSB of S16 - a tone at that level is a square wave of
    // +-1 count at best, and anything below it is silence. The same tone at 24
    // bits is a couple of hundred counts of real waveform.
    [Fact]
    public void A_tone_below_the_16_bit_noise_floor_survives_at_24()
    {
        var stage = Stage(PcmSampleFormat.S24);

        const double amplitude = S24Max / 65536.0; // -96dBFS, in 24-bit counts
        var samples = new int[2400 * Channels];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (int)Math.Round(amplitude * Math.Sin(2 * Math.PI * 440 * (i / Channels) / 48000.0));

        var buffer = PackS24(samples);
        stage.Process(buffer, generation: 1);

        var output = UnpackS24(buffer);
        Assert.Equal(samples, output);

        // And it is a real waveform rather than a couple of counts of
        // dither - roughly +-128, which at 16 bits would all round to zero.
        var peak = 0;
        foreach (var sample in output)
            peak = Math.Max(peak, Math.Abs(sample));

        Assert.InRange(peak, 100, 130);
        Assert.True(peak / 256 <= 1, "the same peak is at most one LSB once narrowed to 16 bits");
    }

    // Clipping has to happen at the destination format's full scale, not at a
    // constant. A clamp left at +-32767 would hard-limit every 24-bit sample
    // above -48dBFS - the widening would make the output worse, not better.
    //
    // Reached the only way it can be reached, since nothing before the
    // requantiser can clip on its own and TargetGain is clamped to unity: an
    // EQ boost, which is the user asking for it.
    [Fact]
    public void A_boost_past_full_scale_clamps_at_24_bit_full_scale()
    {
        var boosted = new EqualizerSettings { Enabled = true };
        Array.Fill(boosted.BandGainsDb, 12.0);

        var stage = Stage(PcmSampleFormat.S24);
        stage.Equalizer = Equalizer.BuildFrom(boosted, GaplessFormat.SampleRate);

        var samples = new int[4800 * Channels];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (int)(S24Max * 0.95 * Math.Sin(2 * Math.PI * 997 * (i / Channels) / 48000.0));

        var buffer = PackS24(samples);
        stage.Process(buffer, generation: 1);

        var output = UnpackS24(buffer);
        var peak = 0;
        foreach (var sample in output)
            peak = Math.Max(peak, Math.Abs(sample));

        // It clipped - so the clamp is doing something - and it clipped at 24
        // bits rather than at 16.
        Assert.Equal(S24Max + 1, peak);
        foreach (var sample in output)
            Assert.InRange(sample, S24Min, S24Max);
    }


    // The requantisation noise floor moves down with the format. Both widths
    // land within one and a half counts of the exact value - a half from the
    // rounding and a whole from the TPDF dither's one-LSB triangle, which is
    // the noise it trades the distortion for - but a count is 256 times
    // smaller at 24 bits, which is the whole of what a wider pipeline buys.
    //
    // Parameterised over both formats rather than written for the new one:
    // the arithmetic is now shared, so the assertion that S16 still behaves is
    // as much the point as the assertion that S24 does.
    [Theory]
    [InlineData(PcmSampleFormat.S16, 32767)]
    [InlineData(PcmSampleFormat.S24, S24Max)]
    public void Requantisation_error_is_at_most_one_lsb_of_the_destination_format(PcmSampleFormat format, int fullScale)
    {
        var stage = Stage(format);

        // A gain that cannot be represented as an exact division, so every
        // sample genuinely needs rounding rather than passing through the
        // "already an integer" shortcut.
        stage.TargetGain = 0.3f;

        var count = 4800 * Channels;
        var source = new int[count];
        for (var i = 0; i < count; i++)
            source[i] = (int)(fullScale * 0.9 * Math.Sin(2 * Math.PI * 997 * (i / Channels) / 48000.0));

        var buffer = format == PcmSampleFormat.S16 ? PackS16(source) : PackS24(source);
        stage.Process(buffer, generation: 1);
        var output = format == PcmSampleFormat.S16 ? UnpackS16(buffer) : UnpackS24(buffer);

        var worst = 0.0;
        for (var i = 0; i < count; i++)
            worst = Math.Max(worst, Math.Abs(output[i] - source[i] * 0.3f));

        Assert.True(worst <= 1.5, $"worst requantisation error was {worst} counts");
    }

    private static byte[] PackS16(ReadOnlySpan<int> samples)
    {
        var bytes = new byte[samples.Length * 2];
        var shorts = MemoryMarshal.Cast<byte, short>(bytes.AsSpan());
        for (var i = 0; i < samples.Length; i++)
            shorts[i] = (short)samples[i];

        return bytes;
    }

    private static int[] UnpackS16(ReadOnlySpan<byte> bytes)
    {
        var shorts = MemoryMarshal.Cast<byte, short>(bytes);
        var samples = new int[shorts.Length];
        for (var i = 0; i < shorts.Length; i++)
            samples[i] = shorts[i];

        return samples;
    }

    [Theory]
    [InlineData(PcmSampleFormat.S16, 2, 4)]
    [InlineData(PcmSampleFormat.S24, 3, 6)]
    public void Frame_size_follows_the_sample_format(PcmSampleFormat format, int bytesPerSample, int bytesPerFrame)
    {
        Assert.Equal(bytesPerSample, GaplessFormat.BytesPerSampleOf(format));
        Assert.Equal(bytesPerFrame, GaplessFormat.BytesPerSampleOf(format) * (int)GaplessFormat.Channels);
    }

    // Buffers allocated before the negotiation has happened - the shared ring
    // above all - are sized in this, so it has to stay the widest the
    // pipeline can actually carry. If a format is ever added above S24, a ring
    // sized on a stale maximum silently holds less audio in time than its
    // comment claims.
    [Fact]
    public void The_declared_maximum_frame_is_the_widest_format_there_is()
    {
        foreach (PcmSampleFormat format in Enum.GetValues<PcmSampleFormat>())
            Assert.True(GaplessFormat.BytesPerSampleOf(format) <= GaplessFormat.MaxBytesPerSample);

        Assert.Equal(GaplessFormat.MaxBytesPerSample * (int)GaplessFormat.Channels, GaplessFormat.MaxBytesPerFrame);
    }

    // Packed three-byte little-endian on both sides of the P/Invoke, so
    // nothing converts between the ring and the device buffer. The mapping is
    // one line and exactly the kind of line that gets "simplified".
    [Fact]
    public void The_pipeline_format_maps_onto_miniaudio_and_ffmpeg_without_conversion()
    {
        Assert.Equal(ma_format.ma_format_s16, MiniaudioSink.MiniaudioFormatFor(PcmSampleFormat.S16));
        Assert.Equal(ma_format.ma_format_s24, MiniaudioSink.MiniaudioFormatFor(PcmSampleFormat.S24));

        Assert.Equal(FfmpegSampleFormat.S16, FfmpegTrackDecoder.FormatFor(PcmSampleFormat.S16));
        Assert.Equal(FfmpegSampleFormat.S24, FfmpegTrackDecoder.FormatFor(PcmSampleFormat.S24));
    }
}
