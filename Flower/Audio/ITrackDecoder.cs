using System;
using System.Threading;
using System.Threading.Tasks;

using Flower.Models;

namespace Flower.Audio
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

        // Returns why, not just whether - a streamed track that was never
        // parsed, one whose server stopped answering, and one that is simply
        // broken are three different findings. See DecodePrepareResult.
        Task<DecodePrepareResult> PrepareAsync(CancellationToken cancellationToken = default);
        void StartDecoding();
        void Seek(float position);
        // Moves whatever staged audio fits into newTarget right now without
        // blocking, leaving the write target alone - the first half of a
        // handover, run before EndReached's subscribers get the decode
        // thread. See RetargetableRingWriter.PrimeTarget.
        PromotionSplice PrimeTarget(GaplessRingBuffer newTarget);
        // Returns a measurement of the handover seam - see
        // PromotionSplice, and GaplessCoordinator.HandleDrainedOrFaulted
        // for what is done with it.
        PromotionSplice PromoteTarget(GaplessRingBuffer newTarget);
        void Retire();
    }
}
