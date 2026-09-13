using System;
using System.Runtime.InteropServices;

using Flower.Audio;

namespace Flower.DeviceChecks;

// The few questions these checks ask of a buffer of decoded PCM. Deliberately
// predicates returning a complaint rather than assertions: they have to run
// where there is no test framework, which on a phone is everywhere.
//
// Flower.Tests/TestSupport/Pcm.cs is the richer version of this - THD,
// step analysis, silence runs - for tests that only ever run on a desktop.
// What is here is the subset a device can answer with.
public static class PcmOracle
{
    private static ReadOnlySpan<short> Samples(ReadOnlySpan<byte> pcm) => MemoryMarshal.Cast<byte, short>(pcm);

    // Byte-identical to the PCM the fixture was built from. SyntheticWav
    // writes at the pipeline's own rate and channel count precisely so no
    // resampling stands between the file and this comparison - anything but an
    // exact match means something in the path is altering the audio.
    public static string? Diff(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
    {
        var expectedSamples = Samples(expected);
        var actualSamples = Samples(actual);
        var shared = Math.Min(expectedSamples.Length, actualSamples.Length);

        for (var i = 0; i < shared; i++)
        {
            if (expectedSamples[i] == actualSamples[i])
                continue;

            var frame = i / (int)GaplessFormat.Channels;
            return $"diverges at frame {frame} ({Milliseconds(frame):F1}ms): expected {expectedSamples[i]}, got {actualSamples[i]}";
        }

        if (expectedSamples.Length != actualSamples.Length)
            return $"length differs: expected {expectedSamples.Length} samples, got {actualSamples.Length}";

        return null;
    }

    // Every frame one greater than the last, wrapping as Int16 does. A dropped
    // block, a duplicated one, or a stretch of stale pre-seek audio all break
    // it, and the complaint names where. The starting value is not asserted,
    // because after a seek nobody knows it in advance - that is the point of
    // asking about the sequence rather than the absolute value.
    public static string? RampBreak(ReadOnlySpan<byte> pcm)
    {
        var samples = Samples(pcm);
        var channels = (int)GaplessFormat.Channels;
        var frames = samples.Length / channels;
        if (frames < 2)
            return $"too little audio to judge: {frames} frames";

        for (var frame = 1; frame < frames; frame++)
        {
            var expected = unchecked((short)(samples[(frame - 1) * channels] + 1));
            for (var channel = 0; channel < channels; channel++)
            {
                var actual = samples[frame * channels + channel];
                if (actual != expected)
                    return $"ramp breaks at frame {frame} channel {channel} ({Milliseconds(frame):F1}ms): {actual}, expected {expected}";
            }
        }

        return null;
    }

    // A decoder that produces the right *number* of bytes and none of the
    // right ones reads as a pass to every byte-count assertion ever written.
    // This is the cheap guard against that.
    public static bool IsSilent(ReadOnlySpan<byte> pcm)
    {
        foreach (var sample in Samples(pcm))
        {
            if (sample != 0)
                return false;
        }

        return true;
    }

    // What a lossy format can be held to. AAC and MP3 do not give back the
    // samples they were handed, so "is it the right audio" has to be asked of
    // the sound rather than of the bytes: is there energy at the tone the
    // fixture holds, and not much of it anywhere else.
    //
    // That is a real oracle, not a weaker version of one. Every failure these
    // checks exist for - silence, a fabricated tail, a fragment looped,
    // another track's audio - moves the answer, because none of them are a
    // clean 440Hz. Goertzel rather than an FFT because only a handful of bins
    // are ever asked about.
    public static string? ToneMismatch(ReadOnlySpan<byte> pcm, double toneHz)
    {
        var samples = Samples(pcm);
        if (samples.Length == 0)
            return "no audio came out at all";

        var channels = (int)GaplessFormat.Channels;
        var mono = new double[samples.Length / channels];
        for (var frame = 0; frame < mono.Length; frame++)
            mono[frame] = samples[frame * channels] / 32768.0;

        var atTone = Magnitude(mono, toneHz);
        if (atTone <= 0)
            return $"nothing at all at {toneHz:F0}Hz";

        // Compared against neighbours far enough away that the encoder's own
        // spreading does not count against it, and against a harmonic, which
        // is where a genuinely distorted decode puts its energy.
        double[] elsewhere = [toneHz / 4, toneHz / 2, toneHz * 2, toneHz * 3, toneHz * 5];
        foreach (var frequency in elsewhere)
        {
            var magnitude = Magnitude(mono, frequency);
            if (magnitude >= atTone)
                return $"{frequency:F0}Hz is as loud as the {toneHz:F0}Hz the fixture holds - this is not the fixture's audio";
        }

        return null;
    }

    private static double Magnitude(double[] samples, double frequencyHz)
    {
        var w = 2.0 * Math.PI * frequencyHz / GaplessFormat.SampleRate;
        var coefficient = 2.0 * Math.Cos(w);
        double s1 = 0, s2 = 0;

        foreach (var sample in samples)
        {
            var s0 = sample + coefficient * s1 - s2;
            s2 = s1;
            s1 = s0;
        }

        return Math.Sqrt(s1 * s1 + s2 * s2 - coefficient * s1 * s2) / samples.Length;
    }

    private static double Milliseconds(int frames) => frames * 1000.0 / GaplessFormat.SampleRate;
}
