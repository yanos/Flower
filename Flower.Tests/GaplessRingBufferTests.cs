using System;
using System.Threading;
using System.Threading.Tasks;
using Flower.Manager;

namespace Flower.Tests;

public class GaplessRingBufferTests
{
    [Fact]
    public void Read_on_empty_buffer_returns_zero()
    {
        var ring = new GaplessRingBuffer(16);

        var read = ring.Read(new byte[8]);

        Assert.Equal(0, read);
    }

    [Fact]
    public void Write_then_read_round_trips_the_same_bytes()
    {
        var ring = new GaplessRingBuffer(64);
        byte[] written = [1, 2, 3, 4, 5];

        ring.TryWrite(written);
        var dest = new byte[5];
        var read = ring.Read(dest);

        Assert.Equal(5, read);
        Assert.Equal(written, dest);
    }

    [Fact]
    public void Read_returns_only_as_many_bytes_as_are_available()
    {
        var ring = new GaplessRingBuffer(64);
        ring.TryWrite([1, 2, 3]);

        var dest = new byte[10];
        var read = ring.Read(dest);

        Assert.Equal(3, read);
    }

    [Fact]
    public void TryWrite_returns_zero_once_the_buffer_is_full()
    {
        var ring = new GaplessRingBuffer(4);

        var firstWrite = ring.TryWrite([1, 2, 3, 4]);
        var secondWrite = ring.TryWrite([5]);

        Assert.Equal(4, firstWrite);
        Assert.Equal(0, secondWrite);
    }

    [Fact]
    public void TryWrite_writes_a_partial_chunk_when_only_some_of_it_fits()
    {
        var ring = new GaplessRingBuffer(4);
        ring.TryWrite([1, 2]);

        var written = ring.TryWrite([3, 4, 5, 6]);

        Assert.Equal(2, written);
    }

    [Fact]
    public void Wraparound_reads_and_writes_stay_correct_across_the_buffer_boundary()
    {
        var ring = new GaplessRingBuffer(4);

        // Fill, drain, then write again so the internal cursor wraps past
        // the end of the underlying array.
        ring.TryWrite([1, 2, 3, 4]);
        ring.Read(new byte[4]);
        ring.TryWrite([5, 6, 7, 8]);

        var dest = new byte[4];
        var read = ring.Read(dest);

        Assert.Equal(4, read);
        Assert.Equal(new byte[] { 5, 6, 7, 8 }, dest);
    }

    [Fact]
    public void Reset_discards_buffered_data_and_lets_new_writes_start_from_empty()
    {
        var ring = new GaplessRingBuffer(16);
        ring.TryWrite([1, 2, 3, 4]);

        ring.Reset();

        Assert.Equal(0, ring.AvailableBytes);
        ring.TryWrite([9, 9]);
        var dest = new byte[2];
        Assert.Equal(2, ring.Read(dest));
        Assert.Equal(new byte[] { 9, 9 }, dest);
    }

    [Fact]
    public async Task Write_blocks_until_space_frees_up_then_completes()
    {
        var ring = new GaplessRingBuffer(4);
        ring.TryWrite([1, 2, 3, 4]); // fill it

        var writeCompleted = false;
        var writer = Task.Run(() =>
        {
            ring.Write([5, 6]);
            writeCompleted = true;
        }, TestContext.Current.CancellationToken);

        // Give the writer a moment to actually block on the full buffer.
        Thread.Sleep(100);
        Assert.False(writeCompleted);

        ring.Read(new byte[2]); // frees 2 bytes, should unblock the writer
        await writer.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(writeCompleted);
    }

    [Fact]
    public async Task Write_abandons_remaining_data_early_if_Reset_happens_mid_write()
    {
        var ring = new GaplessRingBuffer(4);
        ring.TryWrite([1, 2, 3, 4]); // fill it

        var writer = Task.Run(() => ring.Write([5, 6]), TestContext.Current.CancellationToken);

        Thread.Sleep(100);
        ring.Reset();

        // Must return promptly instead of blocking forever for space that
        // will never come from the pre-reset write's perspective.
        await writer.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReadBlocking_wakes_up_once_a_write_arrives()
    {
        var ring = new GaplessRingBuffer(16);
        var dest = new byte[4];
        int read = -1;

        var reader = Task.Run(() => read = ring.ReadBlocking(dest), TestContext.Current.CancellationToken);

        Thread.Sleep(100);
        ring.TryWrite([1, 2, 3, 4]);
        await reader.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(4, read);
    }

    [Fact]
    public async Task Concurrent_producer_and_consumer_never_lose_or_corrupt_bytes()
    {
        var ring = new GaplessRingBuffer(256);
        const int total = 200_000;

        var producer = Task.Run(() =>
        {
            var chunk = new byte[97]; // deliberately not a divisor of capacity
            var next = (byte)0;
            var written = 0;
            while (written < total)
            {
                var thisChunk = Math.Min(chunk.Length, total - written);
                for (var i = 0; i < thisChunk; i++)
                    chunk[i] = next++;
                ring.Write(chunk.AsSpan(0, thisChunk));
                written += thisChunk;
            }
        }, TestContext.Current.CancellationToken);

        var expected = (byte)0;
        var readTotal = 0;
        var buffer = new byte[63]; // also not a divisor of capacity
        while (readTotal < total)
        {
            var read = ring.ReadBlocking(buffer, TestContext.Current.CancellationToken);
            if (read == 0)
                continue;

            for (var i = 0; i < read; i++)
            {
                Assert.Equal(expected, buffer[i]);
                expected++;
            }

            readTotal += read;
        }

        await producer.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }
}
