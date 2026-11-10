using System.Diagnostics;

using Flower.Audio;

using Xunit;

namespace Flower.Tests.Audio
{
    // When the render path may play: after a flush, and again after running
    // dry mid-track - the second being what turns a slow stream into a pause
    // rather than a blip every second or two. Time is passed in, so none of
    // this waits on a clock.
    public class PrimeLatchTests
    {
        private const uint SampleRate = GaplessFormat.DefaultSampleRate;
        private const long Capacity = 64L * 1024 * 1024;

        private static long Frames(int milliseconds) => milliseconds * (long)SampleRate / 1000;

        private static long Ms(int milliseconds) => (long)(Stopwatch.Frequency * (milliseconds / 1000.0));

        private static AudioTimingSettings Timing(int prebufferMs = 200, int rebufferMs = 1500) =>
            new() { PrebufferMs = prebufferMs, RebufferMs = rebufferMs };

        // Opened after a flush and then played from, the state every
        // rebuffer starts from.
        private static PrimeLatch Playing(AudioTimingSettings timing)
        {
            var latch = new PrimeLatch();
            latch.ArmAfterFlush(0);
            Assert.True(latch.TryRelease(Frames(timing.PrebufferMs), Capacity, timing, SampleRate, 0));
            latch.Rendered(1024);
            return latch;
        }

        [Fact]
        public void After_a_flush_it_holds_until_the_prebuffer_is_met()
        {
            var timing = Timing();
            var latch = new PrimeLatch();
            latch.ArmAfterFlush(0);

            Assert.False(latch.TryRelease(Frames(100), Capacity, timing, SampleRate, Ms(10)));
            Assert.True(latch.TryRelease(Frames(200), Capacity, timing, SampleRate, Ms(20)));
            Assert.True(latch.IsPrimed);
        }

        [Fact]
        public void After_a_flush_the_deadline_opens_it_even_with_nothing_buffered()
        {
            var timing = Timing();
            var latch = new PrimeLatch();
            latch.ArmAfterFlush(0);

            Assert.True(latch.TryRelease(0, Capacity, timing, SampleRate, Ms(PrimeLatch.FlushDeadlineMs)));
        }

        [Fact]
        public void Running_dry_mid_track_holds_output_until_the_rebuffer_is_met()
        {
            var timing = Timing();
            var latch = Playing(timing);

            Assert.True(latch.RanDry(timing, Ms(1000)));
            Assert.True(latch.IsRebuffering);

            // A prebuffer's worth is no longer enough - that is the blip.
            Assert.False(latch.TryRelease(Frames(200), Capacity, timing, SampleRate, Ms(1100)));
            Assert.True(latch.TryRelease(Frames(1500), Capacity, timing, SampleRate, Ms(3000)));
            Assert.Equal(1, latch.RebufferCount);
        }

        [Fact]
        public void A_rebuffer_that_never_fills_plays_what_it_has_at_the_deadline()
        {
            var timing = Timing();
            var latch = Playing(timing);
            latch.RanDry(timing, 0);

            Assert.False(latch.TryRelease(Frames(300), Capacity, timing, SampleRate, Ms(PrimeLatch.RebufferDeadlineMs - 1)));
            Assert.True(latch.TryRelease(Frames(300), Capacity, timing, SampleRate, Ms(PrimeLatch.RebufferDeadlineMs)));
        }

        // The last song in the queue ending looks exactly like running dry.
        // Reopening on nothing would close again at once, every few seconds,
        // for as long as the device stayed up.
        [Fact]
        public void A_rebuffer_with_nothing_to_play_stays_closed_past_its_deadline()
        {
            var timing = Timing();
            var latch = Playing(timing);
            latch.RanDry(timing, 0);

            Assert.False(latch.TryRelease(0, Capacity, timing, SampleRate, Ms(PrimeLatch.RebufferDeadlineMs * 10)));
            Assert.Equal(1, latch.RebufferCount);
        }

        [Fact]
        public void Running_dry_before_anything_played_is_not_a_rebuffer()
        {
            var timing = Timing();
            var latch = new PrimeLatch();
            latch.ArmAfterFlush(0);
            latch.TryRelease(0, Capacity, timing, SampleRate, Ms(PrimeLatch.FlushDeadlineMs));

            Assert.False(latch.RanDry(timing, Ms(PrimeLatch.FlushDeadlineMs + 10)));
            Assert.True(latch.IsPrimed);
            Assert.Equal(0, latch.RebufferCount);
        }

        [Fact]
        public void A_rebuffer_setting_of_zero_turns_it_off()
        {
            var timing = Timing(rebufferMs: 0);
            var latch = Playing(timing);

            Assert.False(latch.RanDry(timing, Ms(1000)));
            Assert.True(latch.IsPrimed);
        }

        // A ring smaller than the rebuffer could only ever be released by the
        // deadline; asking for nearly all of it is the most that can be met.
        [Fact]
        public void A_rebuffer_larger_than_the_buffer_is_met_by_nearly_filling_it()
        {
            var timing = Timing();
            var latch = Playing(timing);
            latch.RanDry(timing, 0);
            var smallCapacity = Frames(1000);

            Assert.True(latch.TryRelease(smallCapacity - smallCapacity / 10, smallCapacity, timing, SampleRate, Ms(10)));
        }
    }
}
