using System;
using System.Runtime.InteropServices;

using Flower.Manager;

namespace Flower.Tests;

// Pure-logic coverage for the DSP core spliced into MiniaudioSink.DataCallback
// - no LibVLC/audio hardware involved, so none of this needs RequiresLibVLC.
// GaplessAudioManagerTests covers the forwarding plumbing (ApplyEqualizer
// reaching the sink); this file is only about Equalizer's own signal math.
public class EqualizerTests
{
    private const uint SampleRate = GaplessFormat.SampleRate;

    private static byte[] GenerateSine(double frequencyHz, int frameCount, float amplitude = 0.5f)
    {
        var bytes = new byte[frameCount * GaplessFormat.BytesPerFrame];
        var samples = MemoryMarshal.Cast<byte, short>(bytes.AsSpan());
        for (var i = 0; i < frameCount; i++)
        {
            var value = (short)(amplitude * short.MaxValue * Math.Sin(2 * Math.PI * frequencyHz * i / SampleRate));
            samples[i * 2] = value;
            samples[i * 2 + 1] = value;
        }

        return bytes;
    }

    private static double Rms(byte[] pcm)
    {
        var samples = MemoryMarshal.Cast<byte, short>(pcm);
        double sumSquares = 0;
        foreach (var s in samples)
            sumSquares += (double)s * s;
        return Math.Sqrt(sumSquares / samples.Length);
    }

    private static EqualizerSettings FlatSettings() => new()
    {
        Enabled = true,
        PreampDb = 0,
        BandGainsDb = new double[Equalizer.BandCount],
    };

    [Fact]
    public void BuildFrom_AllZeroGains_IsNearUnity()
    {
        var input = GenerateSine(1000, 4800);
        var output = (byte[])input.Clone();

        Equalizer.BuildFrom(FlatSettings(), SampleRate).ProcessInPlace(output);

        Assert.InRange(Rms(output) / Rms(input), 0.97, 1.03);
    }

    [Fact]
    public void BuildFrom_BoostAtBandCenterFrequency_IncreasesRms()
    {
        var bandIndex = Array.IndexOf(Equalizer.CenterFrequenciesHz, 1000.0);
        var input = GenerateSine(1000, 4800);
        var output = (byte[])input.Clone();

        var settings = FlatSettings();
        settings.BandGainsDb[bandIndex] = 12;
        Equalizer.BuildFrom(settings, SampleRate).ProcessInPlace(output);

        Assert.True(Rms(output) > Rms(input) * 1.2);
    }

    [Fact]
    public void BuildFrom_CutAtBandCenterFrequency_DecreasesRms()
    {
        var bandIndex = Array.IndexOf(Equalizer.CenterFrequenciesHz, 1000.0);
        var input = GenerateSine(1000, 4800);
        var output = (byte[])input.Clone();

        var settings = FlatSettings();
        settings.BandGainsDb[bandIndex] = -12;
        Equalizer.BuildFrom(settings, SampleRate).ProcessInPlace(output);

        Assert.True(Rms(output) < Rms(input) * 0.8);
    }

    [Fact]
    public void BuildFrom_PreampAppliesUniformGain()
    {
        // 700Hz sits well clear of the 500/1000 bands, so only the preamp
        // stage should affect the level here.
        var input = GenerateSine(700, 4800);
        var output = (byte[])input.Clone();

        var settings = FlatSettings();
        settings.PreampDb = 6;
        Equalizer.BuildFrom(settings, SampleRate).ProcessInPlace(output);

        var expectedRatio = Math.Pow(10, 6.0 / 20.0);
        Assert.InRange(Rms(output) / Rms(input), expectedRatio * 0.9, expectedRatio * 1.1);
    }

    [Fact]
    public void ProcessInPlace_HeavyBoostAcrossManyFrames_NeverProducesInvalidSamples()
    {
        var settings = FlatSettings();
        settings.PreampDb = 12;
        for (var i = 0; i < Equalizer.BandCount; i++)
            settings.BandGainsDb[i] = 12;

        var equalizer = Equalizer.BuildFrom(settings, SampleRate);
        var buffer = GenerateSine(1000, 4800, amplitude: 1.0f);

        for (var i = 0; i < 50; i++)
            equalizer.ProcessInPlace(buffer);

        Assert.Equal(4800 * GaplessFormat.BytesPerFrame, buffer.Length);
    }
}
