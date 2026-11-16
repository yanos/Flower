using System;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace Flower.Services;

// Reads a seekable stream ahead of its reader, on a thread of its own, into a
// bounded buffer - so the reader is served from memory and the source's
// latency stops being the reader's.
//
// Built for a streamed track. The output ring holds two seconds of audio, and
// the decoder reads from the network no further ahead than that ring lets it,
// so a track had two seconds of cover when the network went away - and a phone
// leaving Wi-Fi takes five to fifteen to come back on cellular. Walking out of
// the house was a gap every time, however fast the reroute (see
// docs/REMOTE-ACCESS.md). Deepening the output ring instead would have moved
// every track boundary: GaplessCoordinator hands over when a track's *decode*
// ends, so a thirty-second ring would show, count and skip the next song half
// a minute before it is heard. Buffering the file rather than the PCM leaves
// the audio pipeline exactly as it was and costs a tenth of the memory.
//
// Everything the source does about failure - reopening, rerouting, waiting
// out an unreachable server - it still does, on this class's thread instead
// of the decoder's. A failure is handed to the reader where it happened, after
// every byte before it: that audio is still worth hearing.
//
// A seek inside what is buffered costs nothing. Anywhere else it discards the
// buffer and the source is repositioned, as it would have been without this.
public sealed class ReadAheadStream : Stream
{
    // One read from the source. Small enough that a reader waiting on the
    // first bytes after a seek gets them as they arrive.
    private const int ReadBytes = 64 * 1024;

    private readonly Stream _inner;
    private readonly object _gate = new();

    // A circular buffer of what lies ahead of the reader: _count bytes
    // starting at _head, which is _position in the source.
    private byte[] _ring;
    private int _head;
    private int _count;
    private int _limit;

    private long _position;
    private long _fillAt;
    private long? _repositionTo;
    private int _generation;
    private bool _endOfStream;
    private ExceptionDispatchInfo? _failure;
    private bool _closed;
    private Thread? _filler;

    public ReadAheadStream(Stream inner, int capacityBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityBytes);
        _inner = inner;
        _ring = new byte[capacityBytes];
        _limit = capacityBytes;
        _position = _fillAt = inner.Position;
    }

    // How far ahead of the reader to fetch. Raising it keeps what is
    // buffered; lowering it lets the buffer drain to the new size.
    public int CapacityBytes
    {
        get
        {
            lock (_gate)
                return _limit;
        }
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            lock (_gate)
            {
                if (value > _ring.Length)
                {
                    var larger = new byte[value];
                    CopyOut(larger, _count);
                    _ring = larger;
                    _head = 0;
                }
                _limit = value;
                Monitor.PulseAll(_gate);
            }
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;

    public override long Position
    {
        get
        {
            lock (_gate)
                return _position;
        }
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
            return 0;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            StartFilling();

            while (_count == 0 && !_endOfStream && _failure == null && !_closed)
                Monitor.Wait(_gate);

            if (_count > 0)
            {
                var n = Math.Min(buffer.Length, _count);
                CopyOut(buffer, n);
                Consume(n);
                return n;
            }

            ObjectDisposedException.ThrowIf(_closed, this);
            _failure?.Throw();
            return 0;
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _inner.Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            if (target < 0)
                throw new IOException("Cannot seek before the start of the stream");

            var ahead = target - _position;
            if (ahead >= 0 && ahead <= _count)
            {
                Consume((int)ahead);
                return _position;
            }

            unchecked
            {
                _generation++;
            }
            _head = 0;
            _count = 0;
            _position = target;
            _repositionTo = target;
            _endOfStream = false;
            _failure = null;
            Monitor.PulseAll(_gate);
            return _position;
        }
    }

    // Caller holds _gate.
    private void StartFilling()
    {
        if (_filler != null)
            return;

        _filler = new Thread(Fill)
        {
            IsBackground = true,
            Name = "flower-read-ahead",
        };
        _filler.Start();
    }

    private void Fill()
    {
        var scratch = new byte[ReadBytes];
        while (true)
        {
            int generation;
            long at;
            int want;
            lock (_gate)
            {
                while (!_closed && _repositionTo == null
                       && (_endOfStream || _failure != null || _count >= _limit))
                    Monitor.Wait(_gate);
                if (_closed)
                    return;

                if (_repositionTo is { } target)
                {
                    _repositionTo = null;
                    _fillAt = target;
                }
                generation = _generation;
                at = _fillAt;
                want = Math.Min(scratch.Length, _limit - _count);
            }

            var read = 0;
            Exception? failure = null;
            try
            {
                if (_inner.CanSeek && _inner.Position != at)
                    _inner.Seek(at, SeekOrigin.Begin);
                read = _inner.Read(scratch, 0, want);
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            lock (_gate)
            {
                if (_closed)
                    return;
                if (generation != _generation)
                    continue;

                if (failure != null)
                    _failure = ExceptionDispatchInfo.Capture(failure);
                else if (read == 0)
                    _endOfStream = true;
                else
                {
                    CopyIn(scratch.AsSpan(0, read));
                    _fillAt += read;
                }
                Monitor.PulseAll(_gate);
            }
        }
    }

    // The three below: caller holds _gate.
    private void CopyIn(ReadOnlySpan<byte> data)
    {
        var tail = (_head + _count) % _ring.Length;
        var first = Math.Min(data.Length, _ring.Length - tail);
        data[..first].CopyTo(_ring.AsSpan(tail));
        data[first..].CopyTo(_ring);
        _count += data.Length;
    }

    private void CopyOut(Span<byte> dest, int n)
    {
        var first = Math.Min(n, _ring.Length - _head);
        _ring.AsSpan(_head, first).CopyTo(dest);
        _ring.AsSpan(0, n - first).CopyTo(dest[first..]);
    }

    private void Consume(int n)
    {
        _head = (_head + n) % _ring.Length;
        _count -= n;
        _position += n;
        Monitor.PulseAll(_gate);
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    // Releases the reader and the filling thread, then disposes the source -
    // unless the filler is still inside a read of it after five seconds, in
    // which case the source is left to it rather than closed underneath. The
    // caller cancels the source first (SeekableHttpStream.Cancel), so that
    // only happens to a source that ignores cancellation.
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Thread? filler;
            lock (_gate)
            {
                if (_closed)
                {
                    base.Dispose(disposing);
                    return;
                }
                _closed = true;
                filler = _filler;
                Monitor.PulseAll(_gate);
            }

            if (filler == null || filler.Join(TimeSpan.FromSeconds(5)))
                _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
