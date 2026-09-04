using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using Flower.Audio;
using Flower.Models;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests;

// End-to-end coverage of what actually comes out of the render path, driven by
// a decoder that produces real PCM and read by RenderPumpSink, which behaves
// like MiniaudioSink's callback rather than politely waiting for data.
//
// These are the three reported symptoms, each reduced to an assertion about
// samples: a song that stops before it should is a missing tail, a fragment
// playing in a loop is a captured stream that goes backwards, and a click on
// seek is pre-seek audio still arriving after the seek. No LibVLC, so all of
// this runs in CI.
public class RenderStarvationTests
{
    private static readonly int RingBytes = 100 * (int)GaplessFormat.DefaultSampleRate * GaplessFormat.BytesPerFrame / 1000; // 100ms

    private static Track T(string title) =>
        new() { Title = title, Path = $"/music/{title}.mp3", Duration = TimeSpan.FromSeconds(1) };

    // Writes a known, identifiable PCM stream into whatever ring it is pointed
    // at, through the same RetargetableRingWriter the real TrackDecoder uses,
    // so promotion and flush behave identically.
    private sealed class PcmTrackDecoder : ITrackDecoder
    {
        private readonly RetargetableRingWriter _writer;
        private readonly Func<int, short> _value;
        private readonly int _totalFrames;
        private readonly int _framesPerWrite;
        private readonly int _writeDelayMs;
        private volatile bool _retired;
        private Task? _decode;

        public PcmTrackDecoder(
            Track track,
            GaplessRingBuffer ring,
            Func<int, short> value,
            int totalFrames,
            int framesPerWrite = 240,
            int writeDelayMs = 0)
        {
            Track = track;
            _writer = new RetargetableRingWriter(ring);
            _value = value;
            _totalFrames = totalFrames;
            _framesPerWrite = framesPerWrite;
            _writeDelayMs = writeDelayMs;
        }

        public Track Track { get; }
        public long BytesProduced { get; private set; }

        // Swapped in mid-decode by SeekTo below, standing in for LibVLC
        // landing somewhere else and flushing.
        private Func<int, short>? _postSeekValue;

        public event Action? Drained;
        public event Action? Faulted;
        public event Action<long>? SeekSettled;

        public Task<DecodePrepareResult> PrepareAsync(CancellationToken cancellationToken = default) => Task.FromResult(DecodePrepareResult.Ready);

        public void StartDecoding() => _decode = Task.Run(Decode);

        private void Decode()
        {
            var buffer = new byte[_framesPerWrite * GaplessFormat.BytesPerFrame];
            var samples = MemoryMarshal.Cast<byte, short>(buffer.AsSpan());

            for (var frame = 0; frame < _totalFrames && !_retired; frame += _framesPerWrite)
            {
                var value = _postSeekValue ?? _value;
                for (var i = 0; i < _framesPerWrite; i++)
                {
                    samples[i * 2] = value(frame + i);
                    samples[i * 2 + 1] = value(frame + i);
                }

                _writer.Write(buffer, () => _retired);
                BytesProduced += buffer.Length;

                if (_writeDelayMs > 0)
                    Thread.Sleep(_writeDelayMs);
            }

            if (!_retired)
                Drained?.Invoke();
        }

        public void Seek(float position)
        {
        }

        // What a real seek looks like from the coordinator's side, minus the
        // asynchrony: the decoder starts producing different audio. The ring
        // flush is GaplessCoordinator.Seek's job, which is the thing under
        // test.
        public void SeekTo(Func<int, short> value)
        {
            _postSeekValue = value;
            SeekSettled?.Invoke(0);
        }

        public PromotionSplice PrimeTarget(GaplessRingBuffer newTarget) => _writer.PrimeTarget(newTarget);

        public PromotionSplice PromoteTarget(GaplessRingBuffer newTarget) => _writer.PromoteTarget(newTarget);

        public void Retire()
        {
            _retired = true;
            _decode?.Wait(TimeSpan.FromSeconds(5));
        }

        public void Dispose() => Retire();
    }

    private sealed class Harness : IDisposable
    {
        private readonly Dictionary<string, PcmTrackDecoder> _decoders = [];

        public GaplessRingBuffer Ring { get; } = new(RingBytes);
        public RenderPumpSink Sink { get; } = new();
        public GaplessCoordinator Coordinator { get; }

        public Func<Track, GaplessRingBuffer, PcmTrackDecoder> Factory { get; set; } = null!;

        public Harness()
        {
            Coordinator = new GaplessCoordinator(Ring, (track, ring) =>
            {
                var decoder = Factory(track, ring);
                _decoders[track.Path!] = decoder;
                return decoder;
            });

            Sink.Start(Ring);
        }

        public PcmTrackDecoder DecoderFor(Track track) => _decoders[track.Path!];

        public void Dispose()
        {
            Sink.Dispose();
            Coordinator.Dispose();
        }
    }

    private static void WaitUntil(Func<bool> condition, string because) =>
        Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(10)), because);

    // Every frame in the capture whose value is non-zero, in order. Silence is
    // dropped because starvation legitimately produces it; what must never
    // happen is the *content* going backwards or repeating.
    private static List<short> NonSilentValues(byte[] captured)
    {
        var samples = Pcm.AsSamples(captured);
        var values = new List<short>();
        for (var frame = 0; frame < samples.Length / 2; frame++)
        {
            if (samples[frame * 2] != 0)
                values.Add(samples[frame * 2]);
        }

        return values;
    }

    // The "songs stop before they should" symptom. EndReached fires when
    // decode is exhausted, which is up to a full ring before the last sample
    // has been heard - so an auto-advance that flushed at that moment threw
    // the end of every non-gapless track away.
    [Fact]
    public void An_auto_advance_plays_out_the_finished_tracks_buffered_tail()
    {
        const int frames = 9600; // 200ms, twice the ring
        using var harness = new Harness();
        var a = T("A");
        var b = T("B");

        harness.Factory = (track, ring) => track.Path == a.Path
            ? new PcmTrackDecoder(track, ring, frame => (short)(frame + 1), frames)
            : new PcmTrackDecoder(track, ring, _ => -1, frames);

        var drained = new ManualResetEventSlim(false);
        harness.Coordinator.EndReached += _ => drained.Set();

        harness.Coordinator.Play(a);
        harness.Sink.Resume();

        Assert.True(drained.Wait(TimeSpan.FromSeconds(10)), "A never finished decoding");

        // The queue advancing, not the user skipping.
        harness.Coordinator.Play(b, immediate: false);

        WaitUntil(() => Array.IndexOf(Pcm.AsSamples(harness.Sink.Captured).ToArray(), (short)-1) >= 0, "B never played");
        harness.Sink.Pause();

        var values = NonSilentValues(harness.Sink.Captured);
        var lastOfA = values.LastIndexOf((short)frames);

        Assert.True(lastOfA >= 0, $"A's final frame never reached the sink (highest value seen: {MaxOfA(values)})");

        // Nothing of B before A had finished.
        for (var i = 0; i < lastOfA; i++)
            Assert.NotEqual(-1, values[i]);
    }

    private static int MaxOfA(List<short> values)
    {
        var max = 0;
        foreach (var value in values)
        {
            if (value > max)
                max = value;
        }

        return max;
    }

    // The contrast case, and the reason Play takes the flag at all: a user
    // pressing Next means now, not in two seconds.
    [Fact]
    public void A_manual_skip_cuts_the_tail_off_immediately()
    {
        const int frames = 9600;
        using var harness = new Harness();
        var a = T("A");
        var b = T("B");

        harness.Factory = (track, ring) => track.Path == a.Path
            ? new PcmTrackDecoder(track, ring, frame => (short)(frame + 1), frames)
            : new PcmTrackDecoder(track, ring, _ => -1, frames);

        var drained = new ManualResetEventSlim(false);
        harness.Coordinator.EndReached += _ => drained.Set();

        harness.Coordinator.Play(a);
        harness.Sink.Resume();
        Assert.True(drained.Wait(TimeSpan.FromSeconds(10)), "A never finished decoding");

        harness.Coordinator.Play(b, immediate: true);

        WaitUntil(() => Array.IndexOf(Pcm.AsSamples(harness.Sink.Captured).ToArray(), (short)-1) >= 0, "B never played");
        harness.Sink.Pause();

        var values = NonSilentValues(harness.Sink.Captured);
        Assert.DoesNotContain((short)frames, values);
    }

    // The "a fragment plays in a loop" symptom, at the level it is actually
    // heard: a flush must never let already-played audio back out, and a
    // starved ring must produce silence rather than whatever it happens to
    // still hold.
    [Fact]
    public void A_starved_ring_renders_silence_and_never_replays_what_it_already_gave()
    {
        using var harness = new Harness();
        var a = T("A");

        // Deliberately slower than the sink drains, so the ring is empty far
        // more often than not.
        harness.Factory = (track, ring) =>
            new PcmTrackDecoder(track, ring, frame => (short)(frame + 1), totalFrames: 9600, framesPerWrite: 240, writeDelayMs: 15);

        harness.Coordinator.Play(a);
        harness.Sink.Resume();

        WaitUntil(() => NonSilentValues(harness.Sink.Captured).Count > 4000, "the decoder never produced enough");
        harness.Sink.Pause();

        Assert.True(harness.Sink.SilentPeriods > 0, "this test is meant to starve the sink and did not");

        // The one thing that must hold: content only ever moves forward. A
        // repeat or a replay of pre-flush audio shows up here immediately.
        var values = NonSilentValues(harness.Sink.Captured);
        for (var i = 1; i < values.Count; i++)
            Assert.True(values[i] > values[i - 1], $"the stream went backwards at index {i}: {values[i - 1]} -> {values[i]}");
    }

    // Seek used to leave a whole ring of pre-seek audio in front of the render
    // callback, because the flush only happened when LibVLC got round to it.
    [Fact]
    public void A_seek_flushes_pre_seek_audio_before_it_can_be_heard()
    {
        using var harness = new Harness();
        var a = T("A");

        harness.Factory = (track, ring) =>
            new PcmTrackDecoder(track, ring, _ => 1000, totalFrames: 480000, framesPerWrite: 240);

        harness.Coordinator.Play(a);
        harness.Sink.Resume();

        WaitUntil(() => NonSilentValues(harness.Sink.Captured).Count > 2400, "pre-seek audio never played");

        var capturedAtSeek = harness.Sink.CapturedCount;
        harness.Coordinator.Seek(0.5f);
        harness.DecoderFor(a).SeekTo(_ => 2000);

        WaitUntil(() => Array.IndexOf(Pcm.AsSamples(harness.Sink.Captured).ToArray(), (short)2000) >= 0, "post-seek audio never played");
        harness.Sink.Pause();

        // One render period may already have been in flight when Seek
        // returned; beyond that, nothing from before the seek may appear.
        var after = harness.Sink.Captured.AsSpan((int)capturedAtSeek);
        var preSeekFrames = 0;
        var samples = Pcm.AsSamples(after);
        for (var frame = 0; frame < samples.Length / 2; frame++)
        {
            if (samples[frame * 2] == 1000)
                preSeekFrames++;
        }

        Assert.True(preSeekFrames <= 480, $"{preSeekFrames} frames of pre-seek audio played after the seek");
    }
}
