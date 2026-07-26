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

        Task<bool> PrepareAsync(CancellationToken cancellationToken = default);
        void StartDecoding();
        void Seek(float position);
        void PromoteTarget(GaplessRingBuffer newTarget);
        void Retire();
    }
}
