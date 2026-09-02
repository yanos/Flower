using System;
using System.Threading;
using System.Threading.Tasks;
using Flower.Audio;

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

    // The bug behind the "a fragment plays in a loop" symptom. Reset() only
    // bumps the generation; each side rebases its own index on its next call.
    // Read() used to rebase its own index to 0 and then compare it against
    // the writer's still-*pre-flush* _writeIndex, concluding that a whole
    // ring of audio was available and handing back the pre-flush contents -
    // wrapping around and replaying them for as long as it took the decoder
    // to produce its first post-flush byte, which on a track change is the
    // whole media-open latency.
    //
    // Reader-first is the case that matters and the case a test has to go out
    // of its way to construct: the render callback pulls every few
    // milliseconds, so in the app it is almost always the first of the two to
    // notice a flush.
    [Fact]
    public void A_read_before_the_writer_has_rebased_after_a_reset_returns_nothing()
    {
        var ring = new GaplessRingBuffer(16);
        ring.TryWrite([1, 2, 3, 4, 5, 6, 7, 8]);

        // Drain some of it, so the reader's index is non-zero and the ring's
        // backing array holds real pre-flush bytes at offset 0.
        Assert.Equal(4, ring.Read(new byte[4]));

        ring.Reset();

        var dest = new byte[8];
        Assert.Equal(0, ring.Read(dest));
        Assert.Equal(new byte[8], dest);
        Assert.Equal(0, ring.AvailableBytes);
    }

    [Fact]
    public void A_read_after_a_reset_never_returns_pre_reset_bytes()
    {
        var ring = new GaplessRingBuffer(16);
        ring.TryWrite([1, 2, 3, 4, 5, 6, 7, 8]);
        ring.Read(new byte[4]);

        ring.Reset();
        Assert.Equal(0, ring.Read(new byte[16]));

        ring.TryWrite([9, 9]);

        var dest = new byte[16];
        var read = ring.Read(dest);
        Assert.Equal(2, read);
        Assert.Equal(new byte[] { 9, 9 }, dest[..2]);
    }

    // Reset() is called from a third thread (GaplessCoordinator's Play/Seek,
    // TrackDecoder's flush) while both sides are running, so the guard has to
    // hold under a real race, not just in the ordered cases above. The
    // contract asserted here is the one the render callback depends on: every
    // byte it is handed was written after the most recent flush it observed,
    // and within one epoch the stream is a gap-free, repeat-free prefix of
    // what was written.
    [Fact]
    public async Task Bytes_read_after_a_racing_reset_are_always_from_the_current_epoch()
    {
        var ring = new GaplessRingBuffer(256);
        using var done = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Each epoch writes a strictly increasing ramp from 0, so a reader
        // that sees a value it has already passed within one epoch is looking
        // at replayed pre-flush data.
        var producer = Task.Run(() =>
        {
            var chunk = new byte[97];
            while (!done.IsCancellationRequested)
            {
                var generation = ring.Generation;
                for (var i = 0; i < chunk.Length; i++)
                    chunk[i] = (byte)i;

                ring.TryWrite(chunk);
                if (ring.Generation != generation)
                    continue;
            }
        });

        var flusher = Task.Run(() =>
        {
            while (!done.IsCancellationRequested)
            {
                Thread.Sleep(1);
                ring.Reset();
            }
        });

        var buffer = new byte[63];
        var reads = 0;
        while (!done.IsCancellationRequested)
        {
            var generation = ring.Generation;
            var read = ring.Read(buffer);
            if (read == 0)
                continue;

            reads++;

            // Nothing in the ring is ever written by anyone but the producer
            // above, and it only ever writes values below chunk.Length, so a
            // byte outside that range is uninitialised or torn memory.
            for (var i = 0; i < read; i++)
                Assert.InRange(buffer[i], (byte)0, (byte)96);

            if (ring.Generation == generation)
            {
                // Same epoch throughout: the bytes must be a contiguous run
                // of the producer's ramp, never a wrapped replay of it.
                for (var i = 1; i < read; i++)
                {
                    if (buffer[i] != 0)
                        Assert.Equal(buffer[i - 1] + 1, buffer[i]);
                }
            }
        }

        await Task.WhenAll(producer, flusher);
        Assert.True(reads > 0, "the reader never got any data, so nothing was actually exercised");
    }

    // Read() runs on the real-time render thread, where taking a lock at the
    // gapless seam - which is exactly when a waiter exists on the ring, since
    // RetargetableRingWriter.PromoteTarget writes through the blocking
    // Write() - was an audible click. A blocked writer therefore polls rather
    // than waiting to be signalled, and this asserts the reader isn't
    // signalling anything: a reader-driven drain still unblocks it.
    [Fact]
    public async Task A_blocked_write_completes_once_the_reader_drains_without_being_signalled()
    {
        var ring = new GaplessRingBuffer(8);
        ring.TryWrite([1, 2, 3, 4, 5, 6, 7, 8]);

        var writer = Task.Run(() => ring.Write([9, 9, 9, 9]), TestContext.Current.CancellationToken);
        Assert.False(writer.Wait(TimeSpan.FromMilliseconds(100)), "the write should be parked on a full ring");

        Assert.Equal(8, ring.Read(new byte[8]));

        await writer.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(4, ring.AvailableBytes);
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
