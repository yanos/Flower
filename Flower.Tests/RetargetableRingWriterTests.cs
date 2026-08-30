using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Flower.Manager;

using Xunit;

namespace Flower.Tests;

// The write half of a gapless handover: an armed decoder writes into its
// private staging ring, and PromoteTarget has to be able to swap that ring
// out from under a write that is already in flight. Every test here is a
// regression test for one deadlock - a decode-ahead decoder that filled its
// staging ring parked inside the ring's blocking Write while holding the
// retarget lock, so the handover that would have drained that ring could
// never take the lock, and the next track played as silence forever.
public class RetargetableRingWriterTests
{
    // Every call that takes the writer's gate is made off the test thread
    // and waited on with a timeout, never inline: the bug being covered is
    // a deadlock, and calling into it directly would hang the whole test
    // run instead of failing this one test.
    private static void AssertCompletes(Action action, string because)
    {
        var task = Task.Run(action);
        Assert.True(task.Wait(TimeSpan.FromSeconds(5)), because);
    }

    private static byte[] Ramp(int count, int start = 0) =>
        Enumerable.Range(start, count).Select(i => (byte)(i % 251)).ToArray();

    private static byte[] DrainAll(GaplessRingBuffer ring)
    {
        var buffer = new byte[ring.Capacity];
        var total = 0;
        int read;
        while ((read = ring.Read(buffer.AsSpan(total))) > 0)
            total += read;

        return buffer[..total];
    }

    [Fact]
    public void Write_fills_the_current_target()
    {
        var ring = new GaplessRingBuffer(64);
        var writer = new RetargetableRingWriter(ring);

        writer.Write(Ramp(16));

        Assert.Equal(Ramp(16), DrainAll(ring));
    }

    [Fact]
    public void PromoteTarget_completes_while_a_write_is_parked_on_a_full_target()
    {
        // Nothing ever reads the staging ring before the handover does -
        // exactly like the real decode-ahead path - so this write cannot
        // finish on its own. It has to be unblocked by the promotion.
        var staging = new GaplessRingBuffer(32);
        var shared = new GaplessRingBuffer(1024);
        var writer = new RetargetableRingWriter(staging);

        var parked = Task.Run(() => writer.Write(Ramp(64)));
        Assert.False(parked.Wait(TimeSpan.FromMilliseconds(200)), "expected the write to still be parked on the full staging ring");

        AssertCompletes(() => writer.PromoteTarget(shared), "PromoteTarget deadlocked behind the parked write");
        Assert.True(parked.Wait(TimeSpan.FromSeconds(5)), "the parked write never resumed against the new target");
    }

    [Fact]
    public void Retarget_preserves_byte_order_across_the_staged_backlog_and_the_parked_write()
    {
        // The whole point of draining the backlog before the in-flight
        // write resumes: a splice that reorders or drops bytes here is a
        // click or a repeated fragment of audio at the track boundary.
        var staging = new GaplessRingBuffer(32);
        var shared = new GaplessRingBuffer(1024);
        var writer = new RetargetableRingWriter(staging);

        var parked = Task.Run(() => writer.Write(Ramp(64)));
        Assert.False(parked.Wait(TimeSpan.FromMilliseconds(200)));

        AssertCompletes(() => writer.PromoteTarget(shared), "PromoteTarget deadlocked behind the parked write");
        Assert.True(parked.Wait(TimeSpan.FromSeconds(5)));

        // Whatever fit in staging first, then the rest, in one unbroken
        // sequence - and all 64 bytes, none dropped for want of room.
        Assert.Equal(Ramp(64), DrainAll(shared));
    }

    [Fact]
    public void Writes_after_a_promotion_go_to_the_new_target()
    {
        var staging = new GaplessRingBuffer(64);
        var shared = new GaplessRingBuffer(1024);
        var writer = new RetargetableRingWriter(staging);

        writer.Write(Ramp(8));
        writer.PromoteTarget(shared);
        writer.Write(Ramp(8, start: 8));

        Assert.Equal(Ramp(16), DrainAll(shared));
        Assert.Same(shared, writer.Target);
    }

    [Fact]
    public void A_parked_write_gives_up_when_the_decoder_is_abandoned()
    {
        // Retire() while a write is parked - the decoder is being thrown
        // away (manual skip, flush), and nothing will ever drain this ring.
        var staging = new GaplessRingBuffer(32);
        var abandoned = false;
        var writer = new RetargetableRingWriter(staging);

        var parked = Task.Run(() => writer.Write(Ramp(64), () => Volatile.Read(ref abandoned)));
        Assert.False(parked.Wait(TimeSpan.FromMilliseconds(200)));

        Volatile.Write(ref abandoned, true);

        Assert.True(parked.Wait(TimeSpan.FromSeconds(5)), "the parked write ignored its abandonment check");
    }

    [Fact]
    public void A_parked_write_drops_the_rest_of_its_chunk_when_the_target_is_flushed()
    {
        // A seek/flush means the bytes still in hand are from before the
        // flush, so they must not be written on top of the new position -
        // the same contract GaplessRingBuffer.Write has on a generation
        // change.
        var staging = new GaplessRingBuffer(32);
        var writer = new RetargetableRingWriter(staging);

        var parked = Task.Run(() => writer.Write(Ramp(64)));
        Assert.False(parked.Wait(TimeSpan.FromMilliseconds(200)));

        AssertCompletes(() => writer.ResetTarget(), "the flush deadlocked behind the parked write");

        Assert.True(parked.Wait(TimeSpan.FromSeconds(5)), "the parked write kept waiting through a flush");

        // Only what had already gone in before the flush: the flush freed
        // the room the remaining 32 bytes were waiting for, and they must
        // not take it. (GaplessRingBuffer.Reset only bumps its generation -
        // reader and writer rebase lazily - so the bytes written before the
        // flush are still readable here; what matters is that no more
        // arrived after it.)
        Assert.Equal(32, DrainAll(staging).Length);
    }

    [Fact]
    public void PromoteTarget_reports_what_it_moved()
    {
        var staging = new GaplessRingBuffer(64);
        var shared = new GaplessRingBuffer(64);
        var writer = new RetargetableRingWriter(staging);

        writer.Write(Ramp(40));

        PromotionSplice splice = default;
        AssertCompletes(() => splice = writer.PromoteTarget(shared), "promotion should not block");

        Assert.True(splice.MovedAnything);
        Assert.Equal(40, splice.BytesMoved);
        Assert.Equal(40, splice.StagedBytes);
        Assert.True(splice.MillisecondsToFirstByte >= 0, "a splice that moved bytes has a first byte to time");
        Assert.True(splice.TotalMilliseconds >= splice.MillisecondsToFirstByte);
    }

    [Fact]
    public void PromoteTarget_counts_only_the_destination_underruns_up_to_the_first_byte()
    {
        var staging = new GaplessRingBuffer(64);
        var shared = new GaplessRingBuffer(64);
        var writer = new RetargetableRingWriter(staging);

        writer.Write(Ramp(40));

        // Three reads against an empty shared ring - the render callback
        // asking for PCM that isn't there yet. This is exactly the state a
        // gap is made of, and the splice has to attribute it.
        var scratch = new byte[8];
        for (var i = 0; i < 3; i++)
            shared.Read(scratch);

        var splice = writer.PromoteTarget(shared);

        Assert.Equal(3, splice.DestinationUnderrunsAtFirstByte);

        // Underruns after the first byte landed are the new track playing,
        // not the seam, so they must not move the reported figure.
        DrainAll(shared);
        for (var i = 0; i < 5; i++)
            shared.Read(scratch);

        Assert.Equal(3, splice.DestinationUnderrunsAtFirstByte);
    }

    [Fact]
    public void PromoteTarget_with_nothing_staged_reports_no_first_byte()
    {
        var staging = new GaplessRingBuffer(64);
        var shared = new GaplessRingBuffer(64);
        var writer = new RetargetableRingWriter(staging);

        var splice = writer.PromoteTarget(shared);

        Assert.False(splice.MovedAnything);
        Assert.Equal(0, splice.BytesMoved);
        Assert.Equal(-1, splice.MillisecondsToFirstByte);
        Assert.Equal(-1, splice.DestinationUnderrunsAtFirstByte);
    }
}
