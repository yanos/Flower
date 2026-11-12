using System;
using System.Diagnostics;
using System.Threading;

namespace Flower.Audio;

// Captures timing facts from the render callback without allocating, locking,
// or logging there. A ring-buffer underrun says PCM was unavailable; these
// values answer the separate question of whether the audio thread itself was
// delivered late or spent too long rendering a period.
internal sealed class AudioCallbackTiming
{
    // A 60ms delivery gap is materially longer than the usual 42ms Bluetooth
    // period, but still catches a brief stall that can be audible. The prior
    // 5ms-over-period threshold reported routine 50ms jitter every second,
    // which drowned out the useful signal. This is a diagnostic threshold,
    // not an audio deadline: the exact preceding period is recorded alongside
    // every gap and can vary by backend and route.
    private static readonly long InterestingGapThresholdTicks = Stopwatch.Frequency * 60 / 1000;

    private long _previousStartedAt;
    private int _previousFrameCount;
    private long _interestingGapCount;
    private long _renderOverrunCount;
    private long _callbackCount;
    private long _totalFrames;
    private long _maxGapTicks;
    private long _expectedTicksAtMaxGap;
    private long _maxLateTicks;
    private long _maxRenderTicks;
    private int _minFrames;
    private int _maxFrames;
    private int _precedingFramesAtMaxGap;
    private int _currentFramesAtMaxGap;

    // Called once per native output callback, after PCM has been rendered.
    // Its fields are read by the watchdog with Interlocked operations only.
    public void Record(long startedAt, long completedAt, uint frameCount, uint sampleRate)
    {
        var frames = checked((int)frameCount);
        UpdateMinimum(ref _minFrames, frames);
        UpdateMaximum(ref _maxFrames, frames);
        Interlocked.Increment(ref _callbackCount);
        Interlocked.Add(ref _totalFrames, frames);

        var expectedRenderTicks = Math.Max(1, frameCount * Stopwatch.Frequency / Math.Max(1u, sampleRate));
        var previousStartedAt = Interlocked.Exchange(ref _previousStartedAt, startedAt);
        var previousFrames = Interlocked.Exchange(ref _previousFrameCount, frames);
        if (previousStartedAt != 0 && startedAt > previousStartedAt)
        {
            var gapTicks = startedAt - previousStartedAt;
            // The interval from callback N-1 to callback N covers the frames
            // requested by N-1, not N. This tells us whether a large gap
            // followed a deliberately larger callback.
            var expectedGapTicks = Math.Max(1, (long)Math.Max(1, previousFrames) * Stopwatch.Frequency / Math.Max(1u, sampleRate));
            if (UpdateMaximum(ref _maxGapTicks, gapTicks))
            {
                Volatile.Write(ref _expectedTicksAtMaxGap, expectedGapTicks);
                Volatile.Write(ref _precedingFramesAtMaxGap, previousFrames);
                Volatile.Write(ref _currentFramesAtMaxGap, frames);
            }

            var lateTicks = Math.Max(0, gapTicks - expectedGapTicks);
            UpdateMaximum(ref _maxLateTicks, lateTicks);
            if (gapTicks >= InterestingGapThresholdTicks)
                Interlocked.Increment(ref _interestingGapCount);
        }

        if (completedAt > startedAt)
        {
            var renderTicks = completedAt - startedAt;
            UpdateMaximum(ref _maxRenderTicks, renderTicks);
            if (renderTicks > expectedRenderTicks)
                Interlocked.Increment(ref _renderOverrunCount);
        }
    }

    // Sampling resets only windowed peaks and counts. The preceding callback
    // timestamp intentionally stays: the first callback in the next window is
    // still compared with the one before it.
    public AudioCallbackTimingSnapshot TakeSnapshot() => new(
        Interlocked.Exchange(ref _interestingGapCount, 0),
        Interlocked.Exchange(ref _renderOverrunCount, 0),
        Interlocked.Exchange(ref _callbackCount, 0),
        Interlocked.Exchange(ref _totalFrames, 0),
        Interlocked.Exchange(ref _maxGapTicks, 0),
        Interlocked.Exchange(ref _expectedTicksAtMaxGap, 0),
        Interlocked.Exchange(ref _maxLateTicks, 0),
        Interlocked.Exchange(ref _maxRenderTicks, 0),
        Interlocked.Exchange(ref _minFrames, 0),
        Interlocked.Exchange(ref _maxFrames, 0),
        Interlocked.Exchange(ref _precedingFramesAtMaxGap, 0),
        Interlocked.Exchange(ref _currentFramesAtMaxGap, 0));

    public void Reset()
    {
        Interlocked.Exchange(ref _previousStartedAt, 0);
        Interlocked.Exchange(ref _previousFrameCount, 0);
        TakeSnapshot();
    }

    private static bool UpdateMaximum(ref long location, long value)
    {
        while (true)
        {
            var current = Volatile.Read(ref location);
            if (value <= current)
                return false;

            if (Interlocked.CompareExchange(ref location, value, current) == current)
                return true;
        }
    }

    private static bool UpdateMaximum(ref int location, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref location);
            if (value <= current)
                return false;

            if (Interlocked.CompareExchange(ref location, value, current) == current)
                return true;
        }
    }

    private static void UpdateMinimum(ref int location, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref location);
            if (current != 0 && value >= current)
                return;

            if (Interlocked.CompareExchange(ref location, value, current) == current)
                return;
        }
    }
}

internal readonly record struct AudioCallbackTimingSnapshot(
    long InterestingGaps,
    long RenderOverruns,
    long CallbackCount,
    long TotalFrames,
    long MaxGapTicks,
    long ExpectedTicksAtMaxGap,
    long MaxLateTicks,
    long MaxRenderTicks,
    int MinFrames,
    int MaxFrames,
    int PrecedingFramesAtMaxGap,
    int CurrentFramesAtMaxGap)
{
    public double MaxGapMilliseconds => ToMilliseconds(MaxGapTicks);
    public double ExpectedPeriodMilliseconds => ToMilliseconds(ExpectedTicksAtMaxGap);
    public double MaxLateMilliseconds => ToMilliseconds(MaxLateTicks);
    public double MaxRenderMilliseconds => ToMilliseconds(MaxRenderTicks);
    public double AverageFramesPerCallback => CallbackCount == 0 ? 0 : (double)TotalFrames / CallbackCount;

    private static double ToMilliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
