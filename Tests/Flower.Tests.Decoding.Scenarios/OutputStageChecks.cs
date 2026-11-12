using System;
using System.Collections.Generic;
using System.Threading;

using Flower.Audio;

namespace Flower.Tests.Decoding.Scenarios;

// The phone runners' look at the output stage they ship: the C in their own
// libflowerbridge build, which Flower.Tests' property tests (OutputStageTests,
// CanonicalFormatTests, EqualizerTests, AudioCallbackTimingTests) hold to its
// properties on each desktop and in the Mono iOS suite, but which those tests
// never reach on Android or under iOS NativeAOT. So a few of the properties,
// the ones a broken build or a miscompile would break first, run here too.
//
// It used to hold the C to a C# copy of the stage byte for byte. There is no
// C# copy any more (AUDIO-QUALITY-PLAN.md, Phase 6 step 8).
public static class OutputStageChecks
{
    private const uint SampleRate = 48000;

    public static IReadOnlyList<CheckResult> RunAll()
    {
        // Flower ships the bridge on every platform it builds for, so a
        // runner without it was packaged wrong. That has to fail rather than
        // quietly check nothing.
        if (!NativeAudioBridge.IsAvailable)
            return
            [
                DecodeChecks.Run("The native output stage is available to be checked at all", () =>
                    throw new CheckFailedException(
                        $"no {NativeAudioBridge.LibraryName} library loaded, or one older than this build"))
            ];

        var results = new List<CheckResult>();
        foreach (var format in new[] { DeviceSampleFormat.S16, DeviceSampleFormat.S24 })
        {
            results.Add(DecodeChecks.Run($"Native output stage at {format}: a unity pass hands back the source's integers",
                () => UnityIsTransparent(format)));
            results.Add(DecodeChecks.Run($"Native output stage at {format}: a fade-out reaches exactly silence",
                () => FadeOutReachesSilence(format)));
        }

        results.Add(DecodeChecks.Run("Native output stage: the callback times itself", CallbackIsTimed));
        return results;
    }

    private static AudioTimingSettings NoFades() =>
        new() { GainRampMs = 0, DeclickFadeMs = 0, TransportFadeMs = 0, FadeOutWaitMs = 0 };

    // Integers of the device's own width, normalised as the decoder hands
    // them over, must come back out unchanged: full scale both ways, and the
    // negative values a packed-S24 sign bug turns into loud positive ones.
    private static void UnityIsTransparent(DeviceSampleFormat format)
    {
        using var stage = new OutputStageHarness(format, NoFades());
        stage.RenderNothing(1);

        var bits = format.BytesPerSample() * 8;
        var max = (1 << (bits - 1)) - 1;
        var min = -(1 << (bits - 1));
        var source = new int[4800 * 2];
        for (var i = 0; i < source.Length; i++)
            source[i] = Math.Clamp((int)(max * Math.Sin(i * 0.013)) + i % 97 - 48, min, max);

        source[0] = min;
        source[1] = max;
        source[2] = -1;

        var scale = (float)(1 << (bits - 1));
        var pcm = new float[source.Length];
        for (var i = 0; i < source.Length; i++)
            pcm[i] = source[i] / scale;

        var output = OutputStageHarness.Unpack(format, stage.Render(pcm));
        for (var i = 0; i < source.Length; i++)
        {
            if (output[i] != source[i])
                throw new CheckFailedException($"sample {i} went in as {source[i]} and came out as {output[i]}");
        }
    }

    private static void FadeOutReachesSilence(DeviceSampleFormat format)
    {
        using var stage = new OutputStageHarness(format, new AudioTimingSettings { TransportFadeMs = 15, DeclickFadeMs = 0, GainRampMs = 0 });
        var loud = new float[4800 * 2];
        Array.Fill(loud, 0.9f);
        stage.Render(loud);

        stage.BeginFadeOut();
        stage.Render(loud);
        if (!stage.FadeOutCompleted)
            throw new CheckFailedException("100ms past a 15ms fade-out, it has not completed");

        var after = OutputStageHarness.Unpack(format, stage.Render(loud));
        var loudest = Array.FindIndex(after, sample => sample != 0);
        if (loudest >= 0)
            throw new CheckFailedException($"sample {loudest} after the fade-out is {after[loudest]}, not silence");
    }

    private static void CallbackIsTimed()
    {
        using var stage = new OutputStageHarness(DeviceSampleFormat.S24, NoFades(), SampleRate);
        stage.RenderNothing(512);
        Thread.Sleep(80);
        stage.RenderNothing(2048);

        var timing = stage.TakeTiming();
        if (timing.CallbackCount != 2 || timing.TotalFrames != 2560 || timing.MinFrames != 512 || timing.MaxFrames != 2048)
            throw new CheckFailedException(
                $"{timing.CallbackCount} callbacks of {timing.MinFrames}-{timing.MaxFrames} frames, {timing.TotalFrames} in all");

        if (timing.InterestingGaps != 1 || timing.MaxGapMilliseconds < 80)
            throw new CheckFailedException($"an 80ms gap was measured as {timing.MaxGapMilliseconds:F1}ms, {timing.InterestingGaps} interesting");
    }
}
