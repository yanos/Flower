using System.Diagnostics;

using Flower.Audio;

namespace Flower.Tests;

public class AudioCallbackTimingTests
{
    [Fact]
    public void Reports_interesting_delivery_gaps_and_render_overruns_with_the_callback_shape()
    {
        var timing = new AudioCallbackTiming();
        var ticksPerPeriod = 2_048L * Stopwatch.Frequency / 48_000;
        var sixtyMilliseconds = Stopwatch.Frequency * 60 / 1_000;

        timing.Record(1_000, 1_000 + ticksPerPeriod / 8, 2_048, 48_000);
        timing.Record(1_000 + sixtyMilliseconds + 1, 1_000 + ticksPerPeriod * 3 + 2, 2_048, 48_000);

        var snapshot = timing.TakeSnapshot();

        Assert.Equal(1, snapshot.InterestingGaps);
        Assert.Equal(1, snapshot.RenderOverruns);
        Assert.Equal(2, snapshot.CallbackCount);
        Assert.Equal(4_096, snapshot.TotalFrames);
        Assert.Equal(2_048, snapshot.AverageFramesPerCallback);
        Assert.Equal(2_048, snapshot.MinFrames);
        Assert.Equal(2_048, snapshot.MaxFrames);
        Assert.Equal(2_048, snapshot.PrecedingFramesAtMaxGap);
        Assert.Equal(2_048, snapshot.CurrentFramesAtMaxGap);
        Assert.InRange(snapshot.MaxGapMilliseconds, 60, 61);
        Assert.InRange(snapshot.MaxRenderMilliseconds, 67, 69);
    }

    [Fact]
    public void Reset_discards_the_pause_gap_before_the_next_callback()
    {
        var timing = new AudioCallbackTiming();
        var ticksPerPeriod = 2_048L * Stopwatch.Frequency / 48_000;

        timing.Record(1_000, 1_001, 2_048, 48_000);
        timing.Reset();
        timing.Record(1_000 + ticksPerPeriod * 100, 1_001 + ticksPerPeriod * 100, 2_048, 48_000);

        var snapshot = timing.TakeSnapshot();

        Assert.Equal(0, snapshot.InterestingGaps);
        Assert.Equal(0, snapshot.MaxGapTicks);
    }

    [Fact]
    public void Retains_routine_jitter_below_the_interesting_gap_threshold_in_the_snapshot_without_warning_it()
    {
        var timing = new AudioCallbackTiming();
        var fiftyFiveMilliseconds = Stopwatch.Frequency * 55 / 1_000;

        timing.Record(1_000, 1_001, 2_048, 48_000);
        timing.Record(1_000 + fiftyFiveMilliseconds, 1_001 + fiftyFiveMilliseconds, 2_048, 48_000);

        var snapshot = timing.TakeSnapshot();

        Assert.Equal(0, snapshot.InterestingGaps);
        Assert.InRange(snapshot.MaxGapMilliseconds, 54, 56);
    }
}
