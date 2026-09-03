using System;
using System.Linq;
using System.Threading;

using Flower.Audio;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests
{
    // The feeder is the managed half of the native hand-off (see AudioFeeder
    // and native/miniaudio/flower_audio_bridge.h): the thread that moves PCM
    // out of the shared ring, through the output stage, into a buffer a pure-C
    // render callback drains. None of that logic is platform-specific, so all
    // of it is testable here against FakeAudioBridge, which keeps the same
    // refuse-writes-until-a-flush-is-acknowledged contract the C side has.
    //
    // What these are really guarding is byte conservation. Bytes read out of
    // the shared ring are gone from it, so every path that reads and then
    // fails to hand over - a short write, a write into an unacknowledged
    // flush - is a silent dropout with nothing left to diagnose it by.
    public class AudioFeederTests
    {
        // Transparent settings: no fades, no ramp, no prebuffer, so what
        // arrives at the bridge is exactly what was in the ring and any
        // difference is the feeder's doing rather than the output stage's.
        private static AudioTimingSettings Transparent() => new()
        {
            PrebufferMs = 0,
            TransportFadeMs = 0,
            DeclickFadeMs = 0,
            GainRampMs = 0,
            FadeOutWaitMs = 0,
        };

        private static AudioFeeder Feeder(GaplessRingBuffer ring, IAudioBridge bridge, AudioTimingSettings? timing = null)
        {
            var stage = new OutputStage(GaplessFormat.SampleRate) { Timing = timing ?? Transparent() };
            return new AudioFeeder(ring, bridge, stage);
        }

        // The output stage's declick envelope always starts a stream from
        // silence, so the very first frame is zeroed however short the fade is
        // configured to be. That is the stage's business, not the feeder's;
        // what the feeder owes is every byte, in order, exactly once.
        private static void AssertHandedOver(byte[] expected, FakeAudioBridge bridge)
        {
            Assert.Equal(expected.Length, bridge.Drained.Count);
            Assert.Equal(expected[GaplessFormat.BytesPerFrame..], bridge.Drained.Skip(GaplessFormat.BytesPerFrame));
        }

        private static byte[] Ramp(int byteCount)
        {
            var data = new byte[byteCount];
            for (var i = 0; i < byteCount; i++)
                data[i] = (byte)(i % 251);
            return data;
        }

        [Fact]
        public void Everything_written_to_the_ring_reaches_the_bridge_in_order()
        {
            var ring = new GaplessRingBuffer(64 * 1024);
            var bridge = new FakeAudioBridge(64 * 1024);
            using var feeder = Feeder(ring, bridge);

            var pcm = Ramp(16 * 1024);
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

            var pcm = Ramp(32 * 1024);
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

            ring.TryWrite(Ramp(32 * 1024));
            feeder.Tick();

            Assert.Equal(4096, bridge.Available);
            Assert.Equal(32 * 1024 - 4096, ring.AvailableBytes);
        }

        [Fact]
        public void A_ring_flush_is_carried_through_to_the_bridge()
        {
            var ring = new GaplessRingBuffer(64 * 1024);
            var bridge = new FakeAudioBridge(64 * 1024);
            using var feeder = Feeder(ring, bridge);

            ring.TryWrite(Ramp(8192));
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

            ring.TryWrite(Ramp(8192));
            feeder.Tick();

            var buffered = bridge.Available;

            ring.Reset();
            var second = Ramp(4096);
            ring.TryWrite(second);

            feeder.Tick();
            feeder.Tick();

            Assert.Equal(4096, ring.AvailableBytes);
            Assert.Equal(buffered, bridge.Available);
        }

        [Fact]
        public void Post_flush_audio_is_never_mixed_with_what_preceded_it()
        {
            var ring = new GaplessRingBuffer(64 * 1024);
            var bridge = new FakeAudioBridge(64 * 1024);
            using var feeder = Feeder(ring, bridge);

            var before = new byte[8192];
            Array.Fill(before, (byte)0x11);
            ring.TryWrite(before);
            feeder.Tick();

            ring.Reset();
            var after = Ramp(4096);
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

            ring.TryWrite(Ramp(8192));
            feeder.Tick();

            ring.Reset();
            feeder.Tick();
            Assert.False(bridge.FlushedWithoutConsumer);

            var pcm = Ramp(4096);
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
            var required = 100 * (long)GaplessFormat.SampleRate * GaplessFormat.BytesPerFrame / 1000;

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
            var required = (int)(100 * (long)GaplessFormat.SampleRate * GaplessFormat.BytesPerFrame / 1000);

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

        [Fact]
        public void The_buffered_depth_is_what_the_device_has_not_played_yet()
        {
            var ring = new GaplessRingBuffer(64 * 1024);
            var bridge = new FakeAudioBridge(64 * 1024);
            using var feeder = Feeder(ring, bridge);

            ring.TryWrite(Ramp(8192));
            feeder.Tick();
            Assert.Equal(8192, feeder.BufferedBytes);

            bridge.Drain(2048);
            Assert.Equal(6144, feeder.BufferedBytes);
        }

        [Fact]
        public void The_output_stage_runs_here_rather_than_on_the_render_thread()
        {
            // Half volume, no ramp: proof that processing happens on the way
            // into the bridge, which is the whole point of the hand-off - the
            // callback is left with a copy and a fade.
            var ring = new GaplessRingBuffer(64 * 1024);
            var bridge = new FakeAudioBridge(64 * 1024);
            var stage = new OutputStage(GaplessFormat.SampleRate) { Timing = Transparent(), TargetGain = 0.5f };
            using var feeder = new AudioFeeder(ring, bridge, stage);

            var samples = Enumerable.Repeat((short)10000, 2048).ToArray();
            var pcm = new byte[samples.Length * sizeof(short)];
            Buffer.BlockCopy(samples, 0, pcm, 0, pcm.Length);
            ring.TryWrite(pcm);

            feeder.Tick();
            bridge.Drain(bridge.Available);

            var rendered = new short[bridge.Drained.Count / sizeof(short)];
            Buffer.BlockCopy(bridge.Drained.ToArray(), 0, rendered, 0, bridge.Drained.Count);
            Assert.All(rendered[(int)GaplessFormat.Channels..], sample => Assert.InRange(sample, 4999, 5001));
        }
    }
}
