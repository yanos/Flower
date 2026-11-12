using System.Threading;

using Flower.Audio;
using Flower.Tests.Decoding.Scenarios;

namespace Flower.Tests.Audio;

// The render callback timing itself, in C, which the watchdog reads once a
// second (MiniaudioSink's "callback timing pressure" line). Driven through
// OutputStageHarness with real sleeps between callbacks, and asserted only on
// what a sleep guarantees - at least so long - so a slow machine cannot fail
// these by oversleeping.
public class AudioCallbackTimingTests
{
    private static OutputStageHarness Stage() =>
        new(DeviceSampleFormat.S24, new AudioTimingSettings { GainRampMs = 0, DeclickFadeMs = 0 }, sampleRate: 48_000);

    [Fact]
    public void A_late_callback_is_reported_with_the_period_it_was_late_for()
    {
        using var stage = Stage();

        stage.RenderNothing(2_048);
        Thread.Sleep(80);
        stage.RenderNothing(2_048);

        var timing = stage.TakeTiming();

        Assert.Equal(1, timing.InterestingGaps);
        Assert.Equal(2, timing.CallbackCount);
        Assert.Equal(4_096, timing.TotalFrames);
        Assert.Equal(2_048, timing.AverageFramesPerCallback);
        Assert.Equal(2_048, timing.PrecedingFramesAtMaxGap);
        Assert.Equal(2_048, timing.CurrentFramesAtMaxGap);
        Assert.True(timing.MaxGapMilliseconds >= 80, $"an 80ms sleep was measured as {timing.MaxGapMilliseconds:F2}ms");

        // 2048 frames at 48kHz, the period the second callback was due after.
        Assert.InRange(timing.ExpectedPeriodMilliseconds, 42.66, 42.67);
        Assert.True(timing.MaxLateMilliseconds >= 80 - 42.67, $"late by only {timing.MaxLateMilliseconds:F2}ms");
    }

    // The gap across a pause is the device not running, not the audio thread
    // being late; the sink resets around every stop and start so that the
    // first callback after a resume is not reported as a stall.
    [Fact]
    public void A_reset_forgets_the_last_callback_so_a_pause_is_not_a_gap()
    {
        using var stage = Stage();

        stage.RenderNothing(2_048);
        stage.ResetTiming();
        Thread.Sleep(80);
        stage.RenderNothing(2_048);

        var timing = stage.TakeTiming();

        Assert.Equal(0, timing.InterestingGaps);
        Assert.Equal(0, timing.MaxGapNanoseconds);
        Assert.Equal(1, timing.CallbackCount);
        Assert.Equal(2_048, timing.TotalFrames);
    }

    [Fact]
    public void The_callback_sizes_a_device_asks_for_are_reported()
    {
        using var stage = Stage();

        stage.RenderNothing(512);
        stage.RenderNothing(2_048);
        stage.RenderNothing(1_024);

        var timing = stage.TakeTiming();

        Assert.Equal(512, timing.MinFrames);
        Assert.Equal(2_048, timing.MaxFrames);
        Assert.Equal(3_584 / 3.0, timing.AverageFramesPerCallback, 6);
    }

    // A snapshot is a window: taking one starts the next from nothing, so a
    // stall is reported once, by the tick that saw it.
    [Fact]
    public void Taking_a_snapshot_starts_the_next_window_empty()
    {
        using var stage = Stage();

        stage.RenderNothing(2_048);
        Thread.Sleep(80);
        stage.RenderNothing(2_048);
        stage.TakeTiming();

        var next = stage.TakeTiming();

        Assert.Equal(0, next.InterestingGaps);
        Assert.Equal(0, next.CallbackCount);
        Assert.Equal(0, next.MaxGapNanoseconds);
        Assert.Equal(0, next.MinFrames);
    }
}
