using System;
using System.Collections.Generic;

using Flower.Audio;

namespace Flower.Tests.TestSupport
{
    // A managed stand-in for NativeAudioBridge that keeps the contract the C
    // implementation actually has, not a convenient version of it:
    //
    //  - a write takes what fits and no more, and takes nothing at all while
    //    a flush is outstanding;
    //  - a flush is only applied when the consumer acknowledges it, which is
    //    Drain's job here and the render callback's there;
    //  - draining is explicit, so a test decides exactly when the device
    //    consumed something.
    //
    // Everything the feeder can get wrong - losing bytes to a short write,
    // pumping into an unacknowledged flush, mixing the two sides of a seek -
    // shows up as a wrong Drained sequence.
    internal sealed class FakeAudioBridge : IAudioBridge
    {
        private readonly List<byte> _queued = [];

        public FakeAudioBridge(int capacity) => Capacity = capacity;

        public int Capacity { get; }

        public int Available => _queued.Count;

        // Everything the consumer has taken, in order, since construction.
        public List<byte> Drained { get; } = [];

        public long RequestedFlushes { get; private set; }

        public long FlushAcked { get; private set; }

        public bool Primed { get; private set; }

        public int FadeInFrames { get; private set; } = -1;

        public int FadeOutFrames { get; private set; } = -1;

        public bool FadeOutCompleted { get; set; }

        public bool FlushedWithoutConsumer { get; private set; }

        public int Write(ReadOnlySpan<byte> data)
        {
            if (RequestedFlushes != FlushAcked)
                return 0;

            var room = Math.Min(data.Length, Capacity - _queued.Count);
            if (room <= 0)
                return 0;

            _queued.AddRange(data[..room]);
            return room;
        }

        public long RequestFlush()
        {
            Primed = false;
            return ++RequestedFlushes;
        }

        public void FlushNow()
        {
            FlushedWithoutConsumer = true;
            _queued.Clear();
            FlushAcked = RequestedFlushes;
        }

        public void SetPrimed(bool primed) => Primed = primed;

        public void BeginFadeIn(int fadeFrames) => FadeInFrames = fadeFrames;

        public void BeginFadeOut(int fadeFrames) => FadeOutFrames = fadeFrames;

        public AudioBridgeSnapshot TakeSnapshot() => default;

        // Stands in for one render callback: acknowledges a pending flush by
        // dropping everything queued, then takes up to byteCount.
        public int Drain(int byteCount)
        {
            if (RequestedFlushes != FlushAcked)
            {
                _queued.Clear();
                FlushAcked = RequestedFlushes;
                return 0;
            }

            var taken = Math.Min(byteCount, _queued.Count);
            Drained.AddRange(_queued.GetRange(0, taken));
            _queued.RemoveRange(0, taken);
            return taken;
        }

        public void Dispose()
        {
        }
    }
}
