using System;
using System.IO;

namespace Flower.Manager
{
    // Adapts a GaplessRingBuffer into a .NET Stream so it can be handed to
    // LibVLCSharp's StreamMediaInput and played back as one continuous raw
    // PCM feed (see LibVlcRawStreamSink). Read() blocks when the ring is
    // temporarily empty rather than returning 0, so LibVLC never sees a
    // premature end-of-stream - including across a GaplessRingBuffer.Reset()
    // (manual skip/seek), which is a "no data right now" condition for this
    // stream, not an end-of-stream one, since the freshly-started decoder is
    // about to resume writing into the very same ring buffer. Read() only
    // returns 0 once MarkEnded() has been called, on final shutdown.
    public sealed class GaplessRingBufferStream : Stream
    {
        private readonly GaplessRingBuffer _ringBuffer;
        private volatile bool _ended;

        public GaplessRingBufferStream(GaplessRingBuffer ringBuffer)
        {
            _ringBuffer = ringBuffer;
        }

        public void MarkEnded() => _ended = true;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;

        // StreamMediaInput.Open reads Length directly (size = (ulong)stream.Length)
        // to report the media's byte size to LibVLC - it does not guard this on
        // CanSeek the way it guards Seek()/Position. -1 here becomes
        // ulong.MaxValue once cast, which is LibVLC's own documented convention
        // for "unknown length" (see libvlc_media_new_callbacks), exactly right
        // for this never-ending virtual stream. Throwing NotSupportedException
        // here (the usual Stream contract for a non-seekable stream) crashes
        // StreamMediaInput.Open instead of being caught.
        public override long Length => -1;

        public override long Position
        {
            get => -1;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var dest = buffer.AsSpan(offset, count);

            while (!_ended)
            {
                var read = _ringBuffer.ReadBlocking(dest);
                if (read > 0)
                    return read;
            }

            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush()
        {
        }
    }
}
