using System;
using System.Threading;

namespace Flower.Manager
{
    // Single-producer/single-consumer byte ring buffer carrying canonical PCM
    // (Float32/48kHz/stereo throughout the gapless pipeline - see
    // GaplessCoordinator). Read() is lock-free and never blocks, so it's safe
    // to call from a real-time audio render callback (Flower.Apple's
    // AVAudioSourceNode). Write()/TryWrite() may be called from a LibVLC
    // decode callback thread, which is not real-time, so blocking there under
    // backpressure is fine.
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
    // it. Best-effort zeroing both indices from Reset() too, on top of that,
    // is purely so AvailableBytes reads 0 immediately after a Reset() that
    // isn't racing an in-flight writer/reader - it's not load-bearing for
    // correctness.
    public sealed class GaplessRingBuffer
    {
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
                var writeIdx = Volatile.Read(ref _writerGeneration) == generation ? Volatile.Read(ref _writeIndex) : 0;
                var readIdx = Volatile.Read(ref _readerGeneration) == generation ? Volatile.Read(ref _readIndex) : 0;
                return Math.Max(0, writeIdx - readIdx);
            }
        }

        // Reads up to dest.Length bytes without blocking. Returns the number
        // of bytes actually copied - 0 means the buffer is currently empty,
        // not end-of-stream (callers decide what "empty" means for them).
        public int Read(Span<byte> dest)
        {
            var generation = Volatile.Read(ref _generation);
            if (generation != _readerGeneration)
            {
                _readerGeneration = generation;
                _readIndex = 0;
            }

            var readIdx = _readIndex;
            var writeIdx = Volatile.Read(ref _writeIndex);
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

            readIdx += toRead;
            _readIndex = readIdx;
            Volatile.Write(ref _readIndex, readIdx);
            _progressSignal.Set();

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
                _writerGeneration = generation;
                _writeIndex = 0;
            }

            var writeIdx = _writeIndex;
            var readIdx = Volatile.Read(ref _readIndex);
            var free = _capacity - (writeIdx - readIdx);

            if (free <= 0)
                return 0;

            var toWrite = (int)Math.Min(data.Length, free);
            CopyIntoRing(writeIdx, data[..toWrite]);

            if (Volatile.Read(ref _generation) != generation)
                return 0;

            writeIdx += toWrite;
            _writeIndex = writeIdx;
            Volatile.Write(ref _writeIndex, writeIdx);
            _progressSignal.Set();

            return toWrite;
        }

        // Blocks (backpressure) until all of data has been written, or until
        // Reset() invalidates this write (e.g. a flush/seek raced with an
        // in-flight write), in which case it returns early having written
        // only part of data - callers mid-flush don't care about the rest.
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

                _progressSignal.Reset();
                if (Volatile.Read(ref _generation) != generation)
                    return;

                _progressSignal.Wait(millisecondsTimeout: 50, cancellationToken);
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

        // Drops all buffered data and wakes any blocked reader/writer - used
        // on manual skip/seek, where stale pre-flush audio must never reach
        // the render sink. Safe to call from a thread other than the
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
