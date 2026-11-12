using System;
using System.Diagnostics;
using System.Threading;

namespace Flower.Audio
{
    // When the render path may play, run by AudioFeeder, which fills the
    // native bridge and primes it. MiniaudioSink's managed render callback
    // ran it too, until there was no managed render callback.
    //
    // Two ways to be closed, one way to open:
    //
    // - After a flush (a fresh start, a seek, a skip) the ring is empty while
    //   the decoder opens the file. Rendering then would play a starved
    //   trickle, so the latch holds silence until PrebufferMs is buffered, or
    //   gives up after FlushDeadlineMs so a decoder that never delivers cannot
    //   keep playback silent forever.
    //
    // - Running dry mid-track is the same problem arriving later, and it is
    //   the one a slow network produces. Left open, the path played every
    //   scrap the moment it landed: a phone on a link delivering a third of
    //   real time played a quarter-second blip every second or two for as long
    //   as the stream lasted, which reads as broken rather than as buffering
    //   (2026-11-04, "To Forget"). So once audio has played and the buffer
    //   empties, the latch closes again and waits for RebufferMs - long enough
    //   that what follows is a stretch of music and not a blip.
    //
    //   Its deadline only releases what is actually there. A rebuffer that
    //   gets nothing - the last song in the queue simply ended - stays closed
    //   rather than reopening, running dry and closing again every few
    //   seconds for as long as the device is left running.
    //
    // Owned by the one thread doing the rendering. The two counters are read
    // by the watchdog from another, which is all the Interlocked is for.
    internal sealed class PrimeLatch
    {
        public const int FlushDeadlineMs = 1500;

        // Long enough to fill RebufferMs on a link running at a third of real
        // time, which is roughly where a stream stops being merely slow.
        public const int RebufferDeadlineMs = 5000;

        private bool _primed;
        private bool _rebuffering;
        private bool _playedSinceArmed;
        private long _deadline;
        private long _rebufferStartedAt;

        private long _rebufferCount;
        private long _longestRebufferMs;

        public bool IsPrimed => _primed;

        public bool IsRebuffering => !_primed && _rebuffering;

        // Every time the latch has closed mid-track since it was built.
        public long RebufferCount => Interlocked.Read(ref _rebufferCount);

        // The longest a rebuffer has held output since the last call, for the
        // watchdog's line; reset as it is read.
        public long TakeLongestRebufferMs() => Interlocked.Exchange(ref _longestRebufferMs, 0);

        public void ArmAfterFlush(long now)
        {
            _primed = false;
            _rebuffering = false;
            _playedSinceArmed = false;
            _deadline = now + MillisecondsToTicks(FlushDeadlineMs);
        }

        // Real audio went out (or, for the feeder, was handed to the bridge
        // to go out). Only after that can running dry mean a rebuffer: an
        // empty ring before anything has played is the flush latch's case.
        public void Rendered(int realBytes)
        {
            if (realBytes > 0)
                _playedSinceArmed = true;
        }

        // The output has nothing left to play. Closes the latch and returns
        // true if this is a mid-track underrun worth waiting out; false when
        // it is not (rebuffering is off, the latch is already closed, or
        // nothing has played since it last opened).
        public bool RanDry(AudioTimingSettings timing, long now)
        {
            if (!_primed || !_playedSinceArmed || timing.RebufferMs <= 0)
                return false;

            _primed = false;
            _rebuffering = true;
            _playedSinceArmed = false;
            _rebufferStartedAt = now;
            _deadline = now + MillisecondsToTicks(RebufferDeadlineMs);
            Interlocked.Increment(ref _rebufferCount);
            return true;
        }

        // Opens the latch once enough is buffered, or once the deadline has
        // passed. Returns true on the call that opens it. `capacityFrames` is
        // the most the caller can ever have buffered: a requirement above it
        // could only ever be met by the deadline, so it is capped just under.
        //
        // In frames rather than bytes because a frame is what the requirement
        // is stated in: milliseconds at the device's rate.
        public bool TryRelease(long bufferedFrames, long capacityFrames, AudioTimingSettings timing, uint sampleRate, long now)
        {
            if (_primed)
                return false;

            var milliseconds = _rebuffering ? timing.RebufferMs : timing.PrebufferMs;
            var required = Math.Min(
                milliseconds * (long)sampleRate / 1000,
                capacityFrames - capacityFrames / 10);

            var enough = bufferedFrames >= required;
            var expired = now >= _deadline && (!_rebuffering || bufferedFrames > 0);
            if (!enough && !expired)
                return false;

            if (_rebuffering)
            {
                var heldMs = (long)((now - _rebufferStartedAt) * 1000.0 / Stopwatch.Frequency);
                var longest = Interlocked.Read(ref _longestRebufferMs);
                if (heldMs > longest)
                    Interlocked.Exchange(ref _longestRebufferMs, heldMs);
            }

            _primed = true;
            _rebuffering = false;
            return true;
        }

        private static long MillisecondsToTicks(int milliseconds) =>
            (long)(Stopwatch.Frequency * (milliseconds / 1000.0));
    }
}
