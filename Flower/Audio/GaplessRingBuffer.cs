using System;
using System.Threading;

namespace Flower.Audio
{
    // Single-producer/single-consumer byte ring buffer carrying canonical PCM
    // (S16/48kHz/stereo throughout the gapless pipeline - see
    // GaplessCoordinator). Read() is lock-free, never blocks and never signals
    // an event, so it's safe to call from a real-time audio render callback
    // (MiniaudioSink's ma_device data callback). Write()/TryWrite() may be
    // called from a LibVLC decode callback thread, which is not real-time, so
    // blocking there under backpressure is fine.
    //
    // Reset() (used on flush/seek/manual-skip) never writes to _writeIndex or
    // _readIndex itself - only their single owning thread (writer/reader,
    // respectively) ever does. Instead Reset() just bumps _generation; each
    // side notices the change on its own next call and self-resets its own
    // index to 0, and abandons (returns 0, without publishing) any write/read
    // that was already mid-flight when the generation changed. An external
    // thread reaching in and zeroing an index some other thread might be
    // mid-write to was a real, if narrow, corruption window in an earlier
    // version of this class - a stale in-flight write could land after a
    // fresh post-reset write to the same wrapped-around offset and clobber
    // it.
    //
    // The corollary is that between a Reset() and the counterpart's next call,
    // the counterpart's index still holds a value from the *previous* epoch,
    // and comparing across that boundary is meaningless. So each side must
    // check that the other has rebased into the current generation before
    // reading its index, and treat "hasn't rebased yet" as empty. Read()
    // skipping that check was the bug behind the stale-audio-on-loop symptom:
    // the reader rebased to 0, read the writer's huge pre-flush _writeIndex,
    // concluded that seconds of audio were available, and replayed the
    // pre-flush ring contents (wrapping repeatedly) until the writer caught
    // up. Every rebase therefore publishes its index *before* the generation
    // that acknowledges it, so a counterpart that sees the new generation is
    // guaranteed to see the zeroed index.
    //
    // Both indices are read and written with Interlocked, not plain
    // Volatile.Read/Write: a 64-bit Volatile.Read isn't atomic on a 32-bit
    // runtime, and Flower.Android still ships an armeabi-v7a build, where a
    // torn index read would yield a garbage available/free count.
    //
    // The progress event is writer->reader only. The writer signals it from
    // TryWrite so a blocked ReadBlocking wakes promptly; the reader never
    // touches it, because ManualResetEventSlim.Set() takes an internal monitor
    // (and can lazily allocate a kernel event when a waiter exists), and a
    // waiter does exist on the shared ring for the whole of every track
    // handover - RetargetableRingWriter.PromoteTarget writes through the
    // blocking Write(). The render thread contending on that monitor at the
    // gapless seam was an audible click. Writers under backpressure therefore
    // poll instead of waiting; they are decode/promotion threads where a 1 ms
    // poll costs nothing.
    public sealed class GaplessRingBuffer
    {
        // How long a writer blocked on backpressure sleeps between retries.
        private const int WriterPollMs = 1;

        private readonly byte[] _buffer;
        private readonly int _capacity;

        private long _writeIndex; // owned by the writer thread only
        private long _readIndex;  // owned by the reader thread only

        private int _writerGeneration;
        private int _readerGeneration;

        private readonly ManualResetEventSlim _progressSignal = new(false);
        private int _generation;
        private long _underrunCount;

        public GaplessRingBuffer(int capacityBytes)
        {
            if (capacityBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacityBytes));

            _capacity = capacityBytes;
            _buffer = new byte[capacityBytes];
        }

        public int Capacity => _capacity;

        public long UnderrunCount => Interlocked.Read(ref _underrunCount);

        // Bytes currently buffered and ready to read - best-effort/racy by
        // nature (the two indices are owned by different threads), purely
        // diagnostic, never used to gate Read/Write correctness. Treats
        // either side as 0 if it hasn't self-reset to the current
        // generation yet (see Reset()), so this reads 0 right after a
        // Reset() even before the writer/reader's next call notices it.
        public long AvailableBytes
        {
            get
            {
                var generation = Volatile.Read(ref _generation);
                var writeIdx = Volatile.Read(ref _writerGeneration) == generation ? Interlocked.Read(ref _writeIndex) : 0;
                var readIdx = Volatile.Read(ref _readerGeneration) == generation ? Interlocked.Read(ref _readIndex) : 0;
                return Math.Max(0, writeIdx - readIdx);
            }
        }

        // Cumulative bytes ever read out of this ring since the current
        // generation began (0 immediately after Reset(), even before the
        // reader notices it) - unlike AvailableBytes this reflects real
        // consumption (what actually reached the render sink), which is
        // what GaplessCoordinator needs to compute playback position: a
        // decoder can decode arbitrarily far ahead of real time, but this
        // only advances as fast as the reader actually drains the ring.
        public long TotalBytesRead
        {
            get
            {
                var generation = Volatile.Read(ref _generation);
                return Volatile.Read(ref _readerGeneration) == generation ? Interlocked.Read(ref _readIndex) : 0;
            }
        }

        // Cumulative bytes ever written into this ring since the current
        // generation began - see TotalBytesRead. Used to mark the
        // write-position split point between tracks at a gapless handover.
        public long TotalBytesWritten
        {
            get
            {
                var generation = Volatile.Read(ref _generation);
                return Volatile.Read(ref _writerGeneration) == generation ? Interlocked.Read(ref _writeIndex) : 0;
            }
        }

        // Bumped by every Reset(). A caller that writes in several steps
        // (see TrackDecoder.OnPlay, which pauses between them so a
        // retarget can get in) reads this before it starts and abandons
        // the rest of its data if it changes underneath - a flush/seek
        // means those bytes belong to a stream nobody wants anymore.
        // MiniaudioSink also watches it to fade in after a flush.
        public int Generation => Volatile.Read(ref _generation);

        // Reads up to dest.Length bytes without blocking. Returns the number
        // of bytes actually copied - 0 means the buffer is currently empty,
        // not end-of-stream (callers decide what "empty" means for them).
        public int Read(Span<byte> dest)
        {
            var generation = Volatile.Read(ref _generation);
            if (generation != _readerGeneration)
            {
                // Publish the rebased index before acknowledging the
                // generation, so a writer that observes _readerGeneration ==
                // generation can never see the previous epoch's _readIndex.
                Interlocked.Exchange(ref _readIndex, 0);
                Volatile.Write(ref _readerGeneration, generation);
            }

            // The writer hasn't rebased into this epoch yet, so _writeIndex
            // still describes the previous one. Nothing is readable, and
            // comparing the two indices across that boundary would hand back
            // pre-flush audio - see the class remarks.
            if (Volatile.Read(ref _writerGeneration) != generation)
                return 0;

            var readIdx = Interlocked.Read(ref _readIndex);
            var writeIdx = Interlocked.Read(ref _writeIndex);
            var available = writeIdx - readIdx;

            if (available <= 0)
            {
                if (dest.Length > 0)
                    Interlocked.Increment(ref _underrunCount);
                return 0;
            }

            // Capped at _capacity, not just dest.Length/available: a single
            // requested read (e.g. LibVLC refilling its own internal cache
            // after a scrub-triggered flush/discontinuity) can ask for more
            // bytes than the ring can ever hold at once. CopyOutOfRing's
            // wrap-around math only holds for lengths <= capacity - beyond
            // that its second (wrapped) chunk can exceed the backing array
            // and throw. A short read here is standard Stream contract
            // (callers loop for more), so clamping is always safe.
            var toRead = (int)Math.Min(Math.Min(dest.Length, available), _capacity);
            CopyOutOfRing(readIdx, dest[..toRead]);

            // A Reset() landed while we were copying - our readIdx/writeIdx
            // snapshot (and the bytes we just copied) belong to a now-stale
            // epoch, so discard rather than publish it.
            if (Volatile.Read(ref _generation) != generation)
                return 0;

            Interlocked.Exchange(ref _readIndex, readIdx + toRead);

            return toRead;
        }

        // Writes as much of data as currently fits without blocking. Returns
        // the number of bytes actually written (may be less than data.Length,
        // or 0, if the buffer is full).
        public int TryWrite(ReadOnlySpan<byte> data)
        {
            var generation = Volatile.Read(ref _generation);
            if (generation != _writerGeneration)
            {
                // Index before generation - see Read().
                Interlocked.Exchange(ref _writeIndex, 0);
                Volatile.Write(ref _writerGeneration, generation);
            }

            var writeIdx = Interlocked.Read(ref _writeIndex);

            // A reader that hasn't rebased yet has consumed nothing in this
            // epoch, so assume 0 - the conservative direction, since
            // overestimating what it has drained would let us overwrite bytes
            // it hasn't read.
            var readIdx = Volatile.Read(ref _readerGeneration) == generation ? Interlocked.Read(ref _readIndex) : 0;
            var free = _capacity - (writeIdx - readIdx);

            if (free <= 0)
                return 0;

            var toWrite = (int)Math.Min(data.Length, free);
            CopyIntoRing(writeIdx, data[..toWrite]);

            if (Volatile.Read(ref _generation) != generation)
                return 0;

            Interlocked.Exchange(ref _writeIndex, writeIdx + toWrite);
            _progressSignal.Set();

            return toWrite;
        }

        // Blocks (backpressure) until all of data has been written, or until
        // Reset() invalidates this write (e.g. a flush/seek raced with an
        // in-flight write), in which case it returns early having written
        // only part of data - callers mid-flush don't care about the rest.
        //
        // Polls rather than waiting on _progressSignal: the reader is a
        // real-time thread and must not signal events (see class remarks).
        public void Write(ReadOnlySpan<byte> data, CancellationToken cancellationToken = default)
        {
            var generation = Volatile.Read(ref _generation);
            var remaining = data;

            while (remaining.Length > 0)
            {
                var written = TryWrite(remaining);
                if (written > 0)
                {
                    remaining = remaining[written..];
                    continue;
                }

                if (Volatile.Read(ref _generation) != generation || cancellationToken.IsCancellationRequested)
                    return;

                Thread.Sleep(WriterPollMs);
            }
        }

        // Blocks until at least one byte is available, or Reset()/ended is
        // signaled via generation change, or the timeout elapses. Returns
        // bytes read (0 on timeout/regeneration - caller decides how to
        // interpret that).
        public int ReadBlocking(Span<byte> dest, CancellationToken cancellationToken = default)
        {
            var generation = Volatile.Read(ref _generation);

            while (true)
            {
                var read = Read(dest);
                if (read > 0)
                    return read;

                if (Volatile.Read(ref _generation) != generation || cancellationToken.IsCancellationRequested)
                    return 0;

                _progressSignal.Reset();
                if (Volatile.Read(ref _generation) != generation)
                    return 0;

                _progressSignal.Wait(millisecondsTimeout: 50, cancellationToken);
            }
        }

        // Drops all buffered data and wakes any blocked reader - used on
        // manual skip/seek, where stale pre-flush audio must never reach the
        // render sink. Safe to call from a thread other than the
        // reader/writer - see class remarks for why it doesn't touch
        // _readIndex/_writeIndex directly.
        public void Reset()
        {
            Interlocked.Increment(ref _generation);
            _progressSignal.Set();
        }

        private void CopyIntoRing(long startIndex, ReadOnlySpan<byte> data)
        {
            var offset = (int)(startIndex % _capacity);
            var firstChunk = Math.Min(data.Length, _capacity - offset);

            data[..firstChunk].CopyTo(_buffer.AsSpan(offset, firstChunk));
            if (firstChunk < data.Length)
                data[firstChunk..].CopyTo(_buffer.AsSpan(0, data.Length - firstChunk));
        }

        private void CopyOutOfRing(long startIndex, Span<byte> dest)
        {
            var offset = (int)(startIndex % _capacity);
            var firstChunk = Math.Min(dest.Length, _capacity - offset);

            _buffer.AsSpan(offset, firstChunk).CopyTo(dest[..firstChunk]);
            if (firstChunk < dest.Length)
                _buffer.AsSpan(0, dest.Length - firstChunk).CopyTo(dest[firstChunk..]);
        }
    }
}
