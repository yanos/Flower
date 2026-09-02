using System;
using System.Threading;
using System.Threading.Tasks;

using Flower.Audio;
using Flower.Models;

namespace Flower.Tests.TestSupport;

// Exercises GaplessCoordinator's (and, via GaplessAudioManager's internal
// test seam, GaplessAudioManager's) handover/idempotency/generation state
// machine without ever touching real LibVLC decode.
public sealed class FakeTrackDecoder : ITrackDecoder
{
    public Track Track { get; }
    public long BytesProduced { get; set; }
    public bool PrepareResult { get; set; } = true;

    public volatile bool StartDecodingCalled;
    public volatile bool RetireCalled;
    public GaplessRingBuffer? PromotedTo { get; private set; }

    // What PromoteTarget hands back, so a test can drive
    // GaplessCoordinator's seam reporting (a clean handover vs. one that
    // underran) without needing a real decode to produce one.
    public PromotionSplice PromotionSplice { get; set; } =
        new(StagedBytes: 0, BytesMoved: 1, MillisecondsToFirstByte: 0, DestinationUnderrunsAtFirstByte: 0, TotalMilliseconds: 0);

    public GaplessRingBuffer? PrimedTo { get; private set; }

    public PromotionSplice PrimeSplice { get; set; } =
        new(StagedBytes: 0, BytesMoved: 0, MillisecondsToFirstByte: -1, DestinationUnderrunsAtFirstByte: -1, TotalMilliseconds: 0);
    public float? LastSeekPosition { get; private set; }

    public event Action? Drained;
    public event Action? Faulted;
    public event Action<long>? SeekSettled;

    public FakeTrackDecoder(Track track) => Track = track;

    public Task<bool> PrepareAsync(CancellationToken cancellationToken = default) => Task.FromResult(PrepareResult);
    public void StartDecoding() => StartDecodingCalled = true;
    public void Seek(float position) => LastSeekPosition = position;
    // Moves nothing by default: the real one only moves anything when the
    // destination has room, and every coordinator test drives the seam
    // through PromotionSplice below instead.
    public PromotionSplice PrimeTarget(GaplessRingBuffer newTarget)
    {
        PrimedTo = newTarget;
        return PrimeSplice;
    }

    public PromotionSplice PromoteTarget(GaplessRingBuffer newTarget)
    {
        PromotedTo = newTarget;
        return PromotionSplice;
    }
    public void Retire() => RetireCalled = true;
    public void Dispose()
    {
    }

    public void RaiseDrained() => Drained?.Invoke();
    public void RaiseFaulted() => Faulted?.Invoke();

    // Stands in for the real decoder discovering, once the seek's flush
    // has landed, that the demuxer put it somewhere other than where it
    // was asked to go.
    public void RaiseSeekSettled(long landedBytes) => SeekSettled?.Invoke(landedBytes);
}
