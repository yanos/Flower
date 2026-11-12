using System;
using System.Runtime.InteropServices;

using Flower.Audio;

using Xunit;

namespace Flower.Tests.TestSupport;

// Analysis and assertions over what a listener hears: S16 interleaved stereo,
// the narrowest format a device is opened in, which is what the output stage
// writes for it and what RenderPumpSink captures. Plus float buffers, and the
// two conversions to and from the pipeline's own float PCM (see
// GaplessFormat) - FromS16 for a fake decoder writing a known signal into a
// ring, ToS16 for a test reading one back out.
//
// Exists because the reported playback faults - clicks, a fragment repeating,
// a song stopping early - are all properties of the samples that come out, and
// nothing in the suite could see a sample. Tests asserted that the state
// machine took the right path, which every one of those faults is perfectly
// compatible with. These are the primitives for asserting on the audio itself:
// a click is a step discontinuity, a repeat is a broken ramp, an early stop is
// a missing tail, and distortion is measurable as THD.
public static class Pcm
{
    private const float S16Scale = 32768f;

    // Pipeline PCM whose samples are exactly these 16-bit ones.
    public static byte[] FromS16(ReadOnlySpan<short> samples)
    {
        var pcm = new byte[samples.Length * GaplessFormat.BytesPerSample];
        WriteS16(samples, pcm);
        return pcm;
    }

    public static byte[] FromS16(ReadOnlySpan<byte> s16) => FromS16(MemoryMarshal.Cast<byte, short>(s16));

    // The same, into a buffer a fake decoder already holds.
    public static void WriteS16(ReadOnlySpan<short> samples, Span<byte> pipeline)
    {
        var destination = MemoryMarshal.Cast<byte, float>(pipeline);
        for (var i = 0; i < samples.Length; i++)
            destination[i] = samples[i] / S16Scale;
    }

    // Pipeline PCM narrowed to S16, the way a unity-gain output stage would
    // narrow it. Exact for anything FromS16 or a 16-bit file produced; a
    // sample that is not on the 16-bit grid fails, rather than rounding into
    // passing, so a decoder that is off by a fraction of an LSB is caught.
    public static byte[] ToS16(ReadOnlySpan<byte> pipeline)
    {
        var source = MemoryMarshal.Cast<byte, float>(pipeline);
        var s16 = new short[source.Length];
        for (var i = 0; i < source.Length; i++)
        {
            var scaled = source[i] * S16Scale;
            if (scaled != MathF.Round(scaled))
                Assert.Fail($"sample {i} is {scaled} in 16-bit units, which no 16-bit sample decodes to");

            s16[i] = (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
        }

        return MemoryMarshal.AsBytes(s16.AsSpan()).ToArray();
    }

    // The same narrowing without the check, for a render loop on its own
    // thread, where a failed assertion would only kill the loop: rounds,
    // which is what a unity-gain output stage does with an on-grid sample too.
    public static void NarrowToS16(ReadOnlySpan<byte> pipeline, Span<byte> s16)
    {
        var source = MemoryMarshal.Cast<byte, float>(pipeline);
        var destination = MemoryMarshal.Cast<byte, short>(s16);
        for (var i = 0; i < source.Length; i++)
            destination[i] = (short)Math.Clamp(MathF.Round(source[i] * S16Scale), short.MinValue, short.MaxValue);
    }

    public static Span<short> AsSamples(byte[] pcm) => MemoryMarshal.Cast<byte, short>(pcm.AsSpan());

    public static ReadOnlySpan<short> AsSamples(ReadOnlySpan<byte> pcm) => MemoryMarshal.Cast<byte, short>(pcm);

    // The known-perfect-PCM comparison: byte-identical, reported at the first
    // frame that differs rather than as "two arrays aren't equal", because the
    // useful information in a failure is *where* the stream diverged.
    public static void AssertBitExact(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
    {
        var expectedSamples = AsSamples(expected);
        var actualSamples = AsSamples(actual);

        var shared = Math.Min(expectedSamples.Length, actualSamples.Length);
        for (var i = 0; i < shared; i++)
        {
            if (expectedSamples[i] == actualSamples[i])
                continue;

            var frame = i / (int)GaplessFormat.Channels;
            Assert.Fail(
                $"PCM diverges at frame {frame} (sample {i}, {FramesToMilliseconds(frame):F1}ms): "
                + $"expected {expectedSamples[i]}, got {actualSamples[i]}");
        }

        Assert.Equal(expectedSamples.Length, actualSamples.Length);
    }

    // The largest jump between one sample and the next, per channel. A click
    // is exactly this: a step the waveform could not have taken on its own.
    // Compared per channel rather than across the interleaved stream, since
    // consecutive interleaved samples are different channels and legitimately
    // unrelated.
    public static int MaxStep(ReadOnlySpan<byte> pcm)
    {
        var samples = AsSamples(pcm);
        var channels = (int)GaplessFormat.Channels;
        var max = 0;

        for (var i = channels; i < samples.Length; i++)
        {
            var step = Math.Abs(samples[i] - samples[i - channels]);
            if (step > max)
                max = step;
        }

        return max;
    }

    public static void AssertContinuous(ReadOnlySpan<byte> pcm, int maxStep, string because)
    {
        var samples = AsSamples(pcm);
        var channels = (int)GaplessFormat.Channels;

        for (var i = channels; i < samples.Length; i++)
        {
            var step = Math.Abs(samples[i] - samples[i - channels]);
            if (step <= maxStep)
                continue;

            var frame = i / channels;
            Assert.Fail(
                $"{because}: step of {step} at frame {frame} ({FramesToMilliseconds(frame):F1}ms), "
                + $"{samples[i - channels]} -> {samples[i]}, limit {maxStep}");
        }
    }

    // Longest run of all-zero frames, in frames. A gap in playback is a silent
    // run where the source had signal.
    public static int LongestSilentRun(ReadOnlySpan<byte> pcm)
    {
        var samples = AsSamples(pcm);
        var channels = (int)GaplessFormat.Channels;
        var frames = samples.Length / channels;

        var longest = 0;
        var run = 0;
        for (var frame = 0; frame < frames; frame++)
        {
            var silent = true;
            for (var channel = 0; channel < channels; channel++)
            {
                if (samples[frame * channels + channel] != 0)
                {
                    silent = false;
                    break;
                }
            }

            if (silent)
            {
                run++;
                if (run > longest)
                    longest = run;
            }
            else
            {
                run = 0;
            }
        }

        return longest;
    }

    public static double Rms(ReadOnlySpan<byte> pcm)
    {
        var samples = AsSamples(pcm);
        if (samples.Length == 0)
            return 0;

        double sumSquares = 0;
        foreach (var sample in samples)
            sumSquares += (double)sample * sample;

        return Math.Sqrt(sumSquares / samples.Length);
    }

    public static double Rms(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0)
            return 0;

        double sumSquares = 0;
        foreach (var sample in samples)
            sumSquares += (double)sample * sample;

        return Math.Sqrt(sumSquares / samples.Length);
    }

    // Peak level in dBFS, full scale being 32768. Negative infinity for
    // digital silence.
    public static double PeakDb(ReadOnlySpan<byte> pcm)
    {
        var samples = AsSamples(pcm);
        var peak = 0;
        foreach (var sample in samples)
            peak = Math.Max(peak, Math.Abs((int)sample));

        return peak == 0 ? double.NegativeInfinity : 20 * Math.Log10(peak / 32768.0);
    }

    // Validates a SyntheticWav.Ramp() stream frame by frame: every frame's
    // value must be its own index. A dropped block, a duplicated one, or a
    // stretch of replayed pre-flush audio all break it, and the failure names
    // the frame where the sequence first went wrong.
    public static void AssertRampSequence(ReadOnlySpan<byte> pcm, int startFrame, string because)
    {
        var samples = AsSamples(pcm);
        var channels = (int)GaplessFormat.Channels;
        var frames = samples.Length / channels;

        for (var frame = 0; frame < frames; frame++)
        {
            var expected = unchecked((short)(startFrame + frame));
            for (var channel = 0; channel < channels; channel++)
            {
                var actual = samples[frame * channels + channel];
                if (actual == expected)
                    continue;

                Assert.Fail(
                    $"{because}: frame {frame} channel {channel} is {actual}, expected {expected} "
                    + $"(ramp starting at {startFrame})");
            }
        }
    }

    // Total harmonic distortion of a single-tone signal, as a fraction of the
    // fundamental's magnitude: the root-sum-square of harmonics 2..10 over the
    // fundamental. Goertzel rather than a full FFT because only eleven bins
    // are wanted and this needs no dependency.
    //
    // The measure that separates "the gain stage is doing arithmetic
    // correctly" from "it is quantising, truncating or clipping": a clean path
    // leaves this at the quantisation floor, and hard clipping or an undithered
    // truncation lifts it by orders of magnitude.
    public static double Thd(ReadOnlySpan<byte> pcm, double fundamentalHz)
    {
        var samples = AsSamples(pcm);
        var channels = (int)GaplessFormat.Channels;
        var frames = samples.Length / channels;

        var mono = new double[frames];
        for (var frame = 0; frame < frames; frame++)
            mono[frame] = samples[frame * channels];

        var fundamental = GoertzelMagnitude(mono, fundamentalHz);
        if (fundamental <= 0)
            return 0;

        double harmonics = 0;
        for (var harmonic = 2; harmonic <= 10; harmonic++)
        {
            var frequency = fundamentalHz * harmonic;
            if (frequency >= PcmFormat.Default.SampleRate / 2.0)
                break;

            var magnitude = GoertzelMagnitude(mono, frequency);
            harmonics += magnitude * magnitude;
        }

        return Math.Sqrt(harmonics) / fundamental;
    }

    private static double GoertzelMagnitude(double[] samples, double frequencyHz)
    {
        var omega = 2 * Math.PI * frequencyHz / PcmFormat.Default.SampleRate;
        var coefficient = 2 * Math.Cos(omega);

        double s1 = 0, s2 = 0;
        foreach (var sample in samples)
        {
            var s0 = sample + coefficient * s1 - s2;
            s2 = s1;
            s1 = s0;
        }

        var real = s1 - s2 * Math.Cos(omega);
        var imaginary = s2 * Math.Sin(omega);
        return Math.Sqrt(real * real + imaginary * imaginary) / samples.Length;
    }

    private static double FramesToMilliseconds(int frames) => frames * 1000.0 / PcmFormat.Default.SampleRate;
}
