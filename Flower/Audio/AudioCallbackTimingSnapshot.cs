namespace Flower.Audio;

// The render callback's timing over one watchdog window, as libflowerbridge
// measures it - see flower_audio_bridge_snapshot for what each field counts.
// A ring-buffer underrun says PCM was unavailable; these answer the separate
// question of whether the audio thread itself was called late or spent too
// long rendering a period, which a ring that never ran short cannot.
//
// Measured in C, in the callback, because that is the only render callback
// there is; it used to be gathered by the managed one, which the bridge
// replaced on every platform, so for a while nothing measured it at all.
public readonly record struct AudioCallbackTimingSnapshot(
    long InterestingGaps,
    long RenderOverruns,
    long CallbackCount,
    long TotalFrames,
    long MaxGapNanoseconds,
    long ExpectedNanosecondsAtMaxGap,
    long MaxLateNanoseconds,
    long MaxRenderNanoseconds,
    int MinFrames,
    int MaxFrames,
    int PrecedingFramesAtMaxGap,
    int CurrentFramesAtMaxGap)
{
    public double MaxGapMilliseconds => ToMilliseconds(MaxGapNanoseconds);
    public double ExpectedPeriodMilliseconds => ToMilliseconds(ExpectedNanosecondsAtMaxGap);
    public double MaxLateMilliseconds => ToMilliseconds(MaxLateNanoseconds);
    public double MaxRenderMilliseconds => ToMilliseconds(MaxRenderNanoseconds);
    public double AverageFramesPerCallback => CallbackCount == 0 ? 0 : (double)TotalFrames / CallbackCount;

    private static double ToMilliseconds(long nanoseconds) => nanoseconds / 1_000_000.0;
}
