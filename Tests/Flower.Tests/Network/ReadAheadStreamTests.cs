using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Flower.Services;

namespace Flower.Tests.Network;

// The buffer that carries a streamed track across a network change. The
// output ring holds two seconds; leaving Wi-Fi takes five to fifteen, so
// without this every walk out of the house was a gap. What matters is that it
// reads ahead while nobody is asking, that what it read is still served once
// the network has gone, and that it is otherwise indistinguishable from the
// stream it wraps.
public class ReadAheadStreamTests
{
    private static byte[] Content(int length) =>
        Enumerable.Range(0, length).Select(i => (byte)(i % 251)).ToArray();

    // A seekable source that serves its bytes in small reads, can be told to
    // go silent from some offset on (a network that has gone away: every read
    // there waits until the stream is torn down), or to fail there.
    private sealed class Source(byte[] content) : Stream
    {
        private readonly ManualResetEventSlim _torn = new();
        private long _position;

        public long? SilentFrom { get; set; }
        public long? FailsAt { get; set; }
        public int Seeks { get; private set; }
        public long FurthestRead { get; private set; }

        public void TearDown() => _torn.Set();

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => content.Length;

        public override long Position
        {
            get => _position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= SilentFrom)
            {
                _torn.Wait();
                throw new IOException("torn down");
            }
            if (_position >= FailsAt)
                throw new IOException("the server could not be reached");

            var n = (int)Math.Min(Math.Min(count, 1000), content.Length - _position);
            if (SilentFrom is { } silent)
                n = (int)Math.Min(n, silent - _position);
            if (FailsAt is { } fails)
                n = (int)Math.Min(n, fails - _position);
            if (n <= 0)
                return 0;

            Array.Copy(content, _position, buffer, offset, n);
            _position += n;
            FurthestRead = Math.Max(FurthestRead, _position);
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Seeks++;
            _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => content.Length + offset,
            };
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _torn.Set();
            base.Dispose(disposing);
        }
    }

    private static byte[] ReadExactly(Stream stream, int count)
    {
        var buffer = new byte[count];
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static void WaitUntil(Func<bool> condition, string because) =>
        Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(5)), because);

    [Fact]
    public async Task What_was_read_ahead_still_plays_after_the_network_goes()
    {
        var content = Content(100_000);
        var source = new Source(content);
        using var stream = new ReadAheadStream(source, capacityBytes: 50_000);

        var head = ReadExactly(stream, 1_000);
        WaitUntil(() => source.FurthestRead >= 51_000, "it should read ahead while nobody is asking");
        source.SilentFrom = source.FurthestRead;

        var rest = await Task.Run(() => ReadExactly(stream, 50_000))
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(content.Take(51_000), head.Concat(rest));
        source.TearDown();
    }

    [Fact]
    public void It_reads_no_further_ahead_than_its_capacity()
    {
        var source = new Source(Content(100_000));
        using var stream = new ReadAheadStream(source, capacityBytes: 20_000);

        ReadExactly(stream, 1_000);
        WaitUntil(() => source.FurthestRead >= 21_000, "it should fill its capacity");
        Thread.Sleep(100);

        Assert.True(source.FurthestRead <= 21_000, $"read to {source.FurthestRead}");
    }

    // Capacity is decided once the track's bitrate is known, which is after
    // FFmpeg has already started reading.
    [Fact]
    public void Raising_the_capacity_reads_further_ahead_and_keeps_what_was_read()
    {
        var content = Content(100_000);
        var source = new Source(content);
        using var stream = new ReadAheadStream(source, capacityBytes: 10_000);

        var head = ReadExactly(stream, 1_000);
        WaitUntil(() => source.FurthestRead >= 11_000, "it should fill its first capacity");
        stream.CapacityBytes = 60_000;
        WaitUntil(() => source.FurthestRead >= 61_000, "it should fill the larger one");

        Assert.Equal(content.Take(61_000), head.Concat(ReadExactly(stream, 60_000)));
    }

    // The failure arrives where it happened, not as soon as the reader thread
    // meets it: everything before it is audio the listener should hear.
    [Fact]
    public void A_failure_ahead_arrives_after_the_bytes_before_it()
    {
        var content = Content(30_000);
        var source = new Source(content) { FailsAt = 20_000 };
        using var stream = new ReadAheadStream(source, capacityBytes: 50_000);

        Assert.Equal(content.Take(20_000), ReadExactly(stream, 20_000));
        Assert.Throws<IOException>(() => stream.ReadByte());
    }

    [Fact]
    public void The_end_of_the_source_is_the_end_of_the_stream()
    {
        var content = Content(5_000);
        using var stream = new ReadAheadStream(new Source(content), capacityBytes: 50_000);

        Assert.Equal(content, ReadExactly(stream, 5_000));
        Assert.Equal(0, stream.Read(new byte[10], 0, 10));
    }

    // A demuxer skipping a box it does not care about, inside what is already
    // buffered, must not throw that buffer away and fetch it again.
    [Fact]
    public void Seeking_forward_into_what_is_buffered_asks_the_source_nothing()
    {
        var content = Content(100_000);
        var source = new Source(content);
        using var stream = new ReadAheadStream(source, capacityBytes: 50_000);

        ReadExactly(stream, 1_000);
        WaitUntil(() => source.FurthestRead >= 51_000, "it should fill its capacity");
        var seeks = source.Seeks;

        stream.Seek(30_000, SeekOrigin.Begin);

        Assert.Equal(content.Skip(30_000).Take(1_000), ReadExactly(stream, 1_000));
        Assert.Equal(seeks, source.Seeks);
    }

    [Fact]
    public void Seeking_anywhere_reads_what_the_source_has_there()
    {
        var content = Content(200_000);
        using var stream = new ReadAheadStream(new Source(content), capacityBytes: 20_000);
        var random = new Random(7);

        for (var i = 0; i < 200; i++)
        {
            var at = random.Next(0, content.Length - 2_000);
            Assert.Equal(at, stream.Seek(at, SeekOrigin.Begin));
            Assert.Equal(content.Skip(at).Take(1_500), ReadExactly(stream, 1_500));
            Assert.Equal(at + 1_500, stream.Position);
        }

        Assert.Equal(content.Length - 100, stream.Seek(-100, SeekOrigin.End));
        Assert.Equal(content.TakeLast(100), ReadExactly(stream, 100));
    }

    // A seek after a failure is a fresh start, not a replay of the old error:
    // the stream underneath recovers on its own (SeekableHttpStream's reopen).
    [Fact]
    public void Seeking_away_from_a_failure_reads_again()
    {
        var content = Content(30_000);
        var source = new Source(content) { FailsAt = 10_000 };
        using var stream = new ReadAheadStream(source, capacityBytes: 50_000);
        ReadExactly(stream, 10_000);
        Assert.Throws<IOException>(() => stream.ReadByte());

        source.FailsAt = null;
        stream.Seek(5_000, SeekOrigin.Begin);

        Assert.Equal(content.Skip(5_000).Take(10_000), ReadExactly(stream, 10_000));
    }

    // Retirement tears the source down, then disposes this; a reader parked
    // on a silent network must not hold that up.
    [Fact]
    public async Task Disposing_releases_a_reader_waiting_on_a_silent_source()
    {
        var source = new Source(Content(10_000)) { SilentFrom = 0 };
        var stream = new ReadAheadStream(source, capacityBytes: 50_000);
        var reading = Task.Run(() => stream.ReadByte());
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(reading.IsCompleted);

        source.TearDown();
        stream.Dispose();

        await Assert.ThrowsAnyAsync<Exception>(() => reading.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }
}
