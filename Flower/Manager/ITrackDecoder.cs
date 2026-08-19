using System;
using System.Threading;
using System.Threading.Tasks;

using Flower.Models;

namespace Flower.Manager
{
    // The subset of TrackDecoder's surface GaplessCoordinator actually
    // depends on - lets tests substitute a fake decoder and exercise the
    // coordinator's handover/idempotency/generation logic without touching
    // real LibVLC decode. See GaplessCoordinator's factory-taking
    // constructor.
    public interface ITrackDecoder : IDisposable
    {
        Track Track { get; }
        long BytesProduced { get; }

        event Action? Drained;
        event Action? Faulted;

        // Raised once a Seek has actually landed, carrying the byte offset
        // into the track that decode genuinely resumed from - which is not
        // necessarily the offset that was asked for. A lossy seek is
        // keyframe/frame-aligned by the demuxer, so LibVLC routinely lands
        // somewhere near the request rather than on it. Without this the
        // seek target is the only thing anyone ever learns, and the
        // scrubber stays permanently offset from the audio by however far
        // the demuxer moved - see GaplessCoordinator.Seek.
        event Action<long>? SeekSettled;

        Task<bool> PrepareAsync(CancellationToken cancellationToken = default);
        void StartDecoding();
        void Seek(float position);
        void PromoteTarget(GaplessRingBuffer newTarget);
        void Retire();
    }
}
