using System;
using System.Linq;
using System.Threading;

using Flower.Audio;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests.Audio
{
    // The feeder is the managed half of the native hand-off (see AudioFeeder
    // and native/miniaudio/flower_audio_bridge.h): the thread that moves PCM
    // out of the shared ring into a buffer a pure-C render callback drains.
    // None of that logic is platform-specific, so all of it is testable here
    // against FakeAudioBridge, which keeps the same
    // refuse-writes-until-a-flush-is-acknowledged contract the C side has.
    //
    // What these are really guarding is sample conservation. Samples read out
    // of the shared ring are gone from it, so every path that reads and then
    // fails to hand over - a short write, a write into an unacknowledged
    // flush - is a silent dropout with nothing left to diagnose it by.
    //
    // Both sides hold the pipeline's float: the output stage runs in the
    // callback, on the far side of the bridge, so what the feeder hands over
    // is the ring's audio byte for byte.
    public class AudioFeederTests
    {
        private const int BridgeBytesPerFrame = GaplessFormat.BytesPerFrame;

        // No prebuffer or rebuffer, so the latch opens on the first audio and
        // what a test sees is the hand-off alone.
        private static AudioTimingSettings Transparent() => new()
        {
            PrebufferMs = 0,
            RebufferMs = 0,
            TransportFadeMs = 0,
            DeclickFadeMs = 0,
            GainRampMs = 0,
            FadeOutWaitMs = 0,
        };

        private static AudioFeeder Feeder(GaplessRingBuffer ring, IAudioBridge bridge, AudioTimingSettings? timing = null)
        {
            var settled = (timing ?? Transparent()).Clamped();
            return new AudioFeeder(ring, bridge, PcmFormat.Default.SampleRate, () => settled);
        }

        // What the feeder owes: every sample, in order, exactly once, and
        // untouched - the very first frame included, now that no declick runs
        // on this side.
        private static void AssertHandedOver(byte[] expected, FakeAudioBridge bridge) =>
            Assert.Equal(expected, bridge.Drained);

        private static byte[] Ramp(int frames)
        {
            var samples = new short[frames * (int)GaplessFormat.Channels];
            for (var i = 0; i < samples.Length; i++)
                samples[i] = (short)((i % 251) * 97 - 12000);

            return Pcm.FromS16(samples);
        }

        [Fact]
        public void Everything_written_to_the_ring_reaches_the_bridge_in_order()
        {
            var ring = new GaplessRingBuffer(64 * 1024);
            var bridge = new FakeAudioBridge(64 * 1024);
            using var feeder = Feeder(ring, bridge);

            var pcm = Ramp(4096);
            ring.TryWrite(pcm);

            feeder.Tick();
            bridge.Drain(bridge.Available);

            AssertHandedOver(pcm, bridge);
        }

        [Fact]
        public void A_bridge_smaller_than_the_ring_loses_nothing_it_was_handed()
        {
            // The failure this exists for: reading a chunk out of the ring and
            // then finding the bridge has room for only part of it. Those
            // bytes are already consumed, so a short write drops audio
            // outright. The feeder must clamp its read to the room available.
            var ring = new GaplessRingBuffer(64 * 1024);
            var bridge = new FakeAudioBridge(3000);
            using var feeder = Feeder(ring, bridge);

            var pcm = Ramp(6144);
            ring.TryWrite(pcm);

            for (var i = 0; i < 200; i++)
            {
                feeder.Tick();
                bridge.Drain(512);
            }

            bridge.Drain(bridge.Available);
            AssertHandedOver(pcm, bridge);
        }

        [Fact]
        public void A_full_bridge_leaves_the_rest_in_the_ring()
        {
            var ring = new GaplessRingBuffer(64 * 1024);
            var bridge = new FakeAudioBridge(4096);
            using var feeder = Feeder(ring, bridge);

            ring.TryWrite(Ramp(4096));
            feeder.Tick();

            Assert.Equal(4096, bridge.Available);
            Assert.Equal((4096 - 4096 / BridgeBytesPerFrame) * GaplessFormat.BytesPerFrame, ring.AvailableBytes);
        }

        [Fact]
        public void A_ring_flush_is_carried_through_to_the_bridge()
        {
            var ring = new GaplessRingBuffer(64 * 1024);
            var bridge = new FakeAudioBridge(64 * 1024);
            using var feeder = Feeder(ring, bridge);

            ring.TryWrite(Ramp(2048));
            feeder.Tick();
            Assert.Equal(0, bridge.RequestedFlushes);

            ring.Reset();
            feeder.Tick();

            Assert.Equal(1, bridge.RequestedFlushes);
        }

        [Fact]
        public void Nothing_is_handed_over_while_a_flush_is_unacknowledged()
        {
            // Post-flush audio written before the consumer has dropped what it
            // holds would be dropped along with it - the flush drops
            // everything queued, and cannot tell the two apart. So the feeder
            // must not even read from the ring until the acknowledgement lands.
            var ring = new GaplessRingBuffer(64 * 1024);
            var bridge = new FakeAudioBridge(64 * 1024);
            using var feeder = Feeder(ring, bridge);

            ring.TryWrite(Ramp(2048));
            feeder.Tick();

            var buffered = bridge.Available;

            ring.Reset();
            var second = Ramp(1024);
            ring.TryWrite(second);

            feeder.Tick();
            feeder.Tick();

            Assert.Equal(second.Length, ring.AvailableBytes);
            Assert.Equal(buffered, bridge.Available);
        }

        [Fact]
        public void Post_flush_audio_is_never_mixed_with_what_preceded_it()
        {
            var ring = new GaplessRingBuffer(64 * 1024);
            var bridge = new FakeAudioBridge(64 * 1024);
            using var feeder = Feeder(ring, bridge);

            var before = Pcm.FromS16(Enumerable.Repeat((short)0x1111, 2048 * (int)GaplessFormat.Channels).ToArray());
            ring.TryWrite(before);
            feeder.Tick();

            ring.Reset();
            var after = Ramp(1024);
            ring.TryWrite(after);

            // The flush request, then the callback acknowledging it, then the
            // pass that is finally allowed to move the new stream.
            feeder.Tick();
            bridge.Drain(bridge.Available);
            feeder.Tick();
            bridge.Drain(bridge.Available);

            AssertHandedOver(after, bridge);
        }

        [Fact]
        public void A_flush_with_no_consumer_running_is_applied_rather_than_waited_on()
        {
            // A device stopped between the request and now will never run a
            // callback to acknowledge anything. Without the timeout the feeder
            // would refuse to move a byte for the rest of the session.
            var ring = new GaplessRingBuffer(64 * 1024);
            var bridge = new FakeAudioBridge(64 * 1024);
            using var feeder = Feeder(ring, bridge);

            ring.TryWrite(Ramp(2048));
            feeder.Tick();

            ring.Reset();
            feeder.Tick();
            Assert.False(bridge.FlushedWithoutConsumer);

            var pcm = Ramp(1024);
            ring.TryWrite(pcm);

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!bridge.FlushedWithoutConsumer && DateTime.UtcNow < deadline)
            {
                feeder.Tick();
                Thread.Sleep(5);
            }

            Assert.True(bridge.FlushedWithoutConsumer, "the feeder never gave up waiting for an acknowledgement");

            feeder.Tick();
            bridge.Drain(bridge.Available);
            AssertHandedOver(pcm, bridge);
        }

        [Fact]
        public void The_device_renders_silence_until_the_prebuffer_is_met()
        {
            // 100ms of prebuffer at the canonical format.
            var timing = Transparent();
            timing.PrebufferMs = 100;
            var required = 100 * (long)PcmFormat.Default.SampleRate * PcmFormat.Default.BytesPerFrame / 1000;

            var ring = new GaplessRingBuffer(1024 * 1024);
            var bridge = new FakeAudioBridge(1024 * 1024);
            using var feeder = Feeder(ring, bridge, timing);

            ring.TryWrite(new byte[(int)required / 2]);
            feeder.Tick();
            Assert.False(bridge.Primed);

            ring.TryWrite(new byte[(int)required]);
            feeder.Tick();
            Assert.True(bridge.Primed);
        }

        [Fact]
        public void A_flush_puts_the_prime_latch_back()
        {
            var timing = Transparent();
            timing.PrebufferMs = 100;
            var required = (int)(100 * (long)PcmFormat.Default.SampleRate * PcmFormat.Default.BytesPerFrame / 1000);

            var ring = new GaplessRingBuffer(1024 * 1024);
            var bridge = new FakeAudioBridge(1024 * 1024);
            using var feeder = Feeder(ring, bridge, timing);

            ring.TryWrite(new byte[required * 2]);
            feeder.Tick();
            Assert.True(bridge.Primed);

            ring.Reset();
            feeder.Tick();

            Assert.False(bridge.Primed);
        }

        // The phone's half of rebuffering. A stream arriving slower than it
        // plays empties the bridge; un-primed, the native callback renders
        // silence until there is a stretch of music to play rather than each
        // scrap as it lands.
        [Fact]
        public void Running_dry_mid_track_unprimes_the_bridge_until_the_rebuffer_is_met()
        {
            var timing = Transparent();
            timing.RebufferMs = 100;
            var required = (int)(100 * (long)PcmFormat.Default.SampleRate * PcmFormat.Default.BytesPerFrame / 1000);

            var ring = new GaplessRingBuffer(1024 * 1024);
            var bridge = new FakeAudioBridge(1024 * 1024);
            using var feeder = Feeder(ring, bridge, timing);

            ring.TryWrite(new byte[4096]);
            feeder.Tick();
            Assert.True(bridge.Primed);

            bridge.Drain(bridge.Available);
            feeder.Tick();
            Assert.False(bridge.Primed);
            Assert.Equal(1, feeder.RebufferCount);

            ring.TryWrite(new byte[required / 2]);
            feeder.Tick();
            Assert.False(bridge.Primed);

            ring.TryWrite(new byte[required]);
            feeder.Tick();
            Assert.True(bridge.Primed);
        }

        [Fact]
        public void The_buffered_depth_is_what_the_device_has_not_played_yet()
        {
            var ring = new GaplessRingBuffer(64 * 1024);
            var bridge = new FakeAudioBridge(64 * 1024);
            using var feeder = Feeder(ring, bridge);

            ring.TryWrite(Ramp(2048));
            feeder.Tick();
            Assert.Equal(2048, feeder.BufferedFrames);

            bridge.Drain(512 * BridgeBytesPerFrame);
            Assert.Equal(1536, feeder.BufferedFrames);
        }

        // ── Following the device ──────────────────────────────────────────

        // The regression this pair exists for.
        //
        // The feeder was started when the device was opened and disposed only
        // when it was closed, so a pause or a stop left it running: a thread at
        // the highest priority in the process, waking 400-odd times a second to
        // read a ring nothing was filling, for as long as the app stayed open.
        // GaplessRingBuffer.Read counts every read of an empty ring as an
        // underrun, so each of those ticks scored one - which is how an idle
        // phone came to log "underrun(s) detected - Started=False ... (+435)"
        // every second, all night, with nothing playing.
        private static bool WaitUntil(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                    return true;
                Thread.Sleep(5);
            }

            return condition();
        }

        [Fact]
        public void A_feeder_whose_device_never_started_does_not_touch_the_ring()
        {
            var ring = new GaplessRingBuffer(64 * 1024);
            var bridge = new FakeAudioBridge(64 * 1024);
            using var feeder = Feeder(ring, bridge);

            var pcm = Ramp(1024);
            ring.TryWrite(pcm);
            feeder.Start();
            Thread.Sleep(100);

            // Nothing moved, and - the part that matters - nothing was counted
            // as an underrun on the way to not moving.
            Assert.Equal(0, bridge.Available);
            Assert.Equal(pcm.Length, ring.AvailableBytes);
            Assert.Equal(0, ring.UnderrunCount);
        }

        [Fact]
        public void A_paused_device_stops_the_feeder_polling_the_ring()
        {
            // An empty ring behind a running device: the shape a track ending
            // leaves behind, and what the feeder used to sit in forever.
            var ring = new GaplessRingBuffer(64 * 1024);
            var bridge = new FakeAudioBridge(64 * 1024);
            using var feeder = Feeder(ring, bridge);

            feeder.Start();
            feeder.Resume();
            Assert.True(WaitUntil(() => ring.UnderrunCount > 0), "a running device should be polling the ring");

            feeder.Pause();
            Thread.Sleep(50);
            var settled = ring.UnderrunCount;
            Thread.Sleep(200);

            Assert.Equal(settled, ring.UnderrunCount);
        }

        [Fact]
        public void A_started_device_wakes_the_feeder()
        {
            var ring = new GaplessRingBuffer(64 * 1024);
            var bridge = new FakeAudioBridge(64 * 1024);
            using var feeder = Feeder(ring, bridge);

            ring.TryWrite(Ramp(1024));
            feeder.Start();
            feeder.Resume();

            Assert.True(WaitUntil(() => bridge.Available == 1024 * BridgeBytesPerFrame), "a running device should be fed");
            feeder.Pause();
        }

        // A pause is not a teardown: the thread parks and comes back, however
        // many times the transport is worked.
        [Fact]
        public void The_feeder_survives_being_parked_and_woken_repeatedly()
        {
            var ring = new GaplessRingBuffer(64 * 1024);
            var bridge = new FakeAudioBridge(64 * 1024);
            using var feeder = Feeder(ring, bridge);

            feeder.Start();

            for (var i = 0; i < 5; i++)
            {
                feeder.Resume();
                ring.TryWrite(Ramp(256));
                Assert.True(WaitUntil(() => bridge.Available >= 256 * BridgeBytesPerFrame * (i + 1)), $"pass {i} was never fed");
                feeder.Pause();
            }
        }

        // The sink frees the bridge once the feeder has stopped, so a feeder
        // that is still inside a Write when its join gives up has to say so:
        // freeing then would let the write land in freed memory
        // (NATIVE-SECURITY-PLAN.md, finding 1).
        [Fact]
        public void Stopping_a_feeder_stalled_inside_a_write_reports_that_it_is_still_running()
        {
            var ring = new GaplessRingBuffer(64 * 1024);
            var bridge = new FakeAudioBridge(64 * 1024);
            using var entered = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            bridge.OnWrite = () =>
            {
                entered.Set();
                release.Wait();
            };
            var feeder = Feeder(ring, bridge);

            ring.TryWrite(Ramp(1024));
            feeder.Start();
            feeder.Resume();
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "the feeder never reached the bridge");

            Assert.False(feeder.Stop());

            bridge.OnWrite = null;
            release.Set();
        }

        [Fact]
        public void Stopping_an_idle_feeder_reports_that_it_has_exited()
        {
            var ring = new GaplessRingBuffer(64 * 1024);
            var bridge = new FakeAudioBridge(64 * 1024);
            var feeder = Feeder(ring, bridge);

            feeder.Start();
            feeder.Resume();

            Assert.True(feeder.Stop());
        }
}
}
