using System;
using System.Threading;
using System.Threading.Tasks;

using Flower.Manager;
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
    public float? LastSeekPosition { get; private set; }

    public event Action? Drained;
    public event Action? Faulted;
    public event Action<long>? SeekSettled;

    public FakeTrackDecoder(Track track) => Track = track;

    public Task<bool> PrepareAsync(CancellationToken cancellationToken = default) => Task.FromResult(PrepareResult);
    public void StartDecoding() => StartDecodingCalled = true;
    public void Seek(float position) => LastSeekPosition = position;
    public void PromoteTarget(GaplessRingBuffer newTarget) => PromotedTo = newTarget;
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
