using System;
using System.Threading;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Manager;
using Flower.Models;
using Flower.Tests.TestSupport;

namespace Flower.Tests;

// Exercises GaplessAudioManager's own glue logic - Time/Position math,
// Volume passthrough, Play/Resume/Pause/Stop delegation, event forwarding -
// against a GaplessCoordinator built with a fake ITrackDecoder factory (via
// the internal test seam constructor), so none of this touches real LibVLC
// decode. GaplessCoordinator's own handover/idempotency/generation state
// machine is already covered in GaplessCoordinatorTests.cs and isn't
// re-tested here.
public class GaplessAudioManagerTests
{
    private static Track T(string title, TimeSpan duration) =>
        new() { Title = title, Path = $"/music/{title}.mp3", Duration = duration };

    private static (GaplessAudioManager Manager, GaplessCoordinator Coordinator, FakeAudioSink Sink) Make(
        IPlatformAudioSession? platformAudioSession = null)
    {
        var ring = new GaplessRingBuffer(4096);
        var coordinator = new GaplessCoordinator(ring, (track, r) => new FakeTrackDecoder(track));
        var sink = new FakeAudioSink();
        var manager = new GaplessAudioManager(ring, coordinator, sink, NullLogger<GaplessAudioManager>.Instance, platformAudioSession);
        return (manager, coordinator, sink);
    }

    private static (GaplessAudioManager Manager, FakeTrackDecoder Decoder, FakeAudioSink Sink, GaplessRingBuffer Ring) PlayTrack(Track track)
    {
        // Large enough to hold several seconds of canonical PCM in one
        // TryWrite() call - Time/Position tests below write/read whole
        // multi-second chunks at once rather than looping in small pieces.
        var ring = new GaplessRingBuffer(10 * (int)GaplessFormat.SampleRate * GaplessFormat.BytesPerFrame);
        FakeTrackDecoder? created = null;
        var coordinator = new GaplessCoordinator(ring, (t, r) =>
        {
            created = new FakeTrackDecoder(t);
            return created;
        });
        var sink = new FakeAudioSink();
        var manager = new GaplessAudioManager(ring, coordinator, sink, NullLogger<GaplessAudioManager>.Instance);

        manager.Play(track);

        return (manager, created!, sink, ring);
    }

    [Fact]
    public void Play_starts_the_coordinator_and_resumes_the_sink()
    {
        var (manager, coordinator, sink) = Make();
        var track = T("A", TimeSpan.FromSeconds(10));

        manager.Play(track);

        Assert.Same(track, coordinator.CurrentTrack);
        Assert.True(sink.IsPlaying);
    }

    [Fact]
    public void Resume_Pause_Resume_Stop_delegate_to_the_sink()
    {
        var (manager, _, sink) = Make();

        manager.Resume();
        Assert.True(sink.IsPlaying);

        manager.Pause();
        Assert.False(sink.IsPlaying);

        manager.Resume();
        Assert.True(sink.IsPlaying);

        manager.Stop();
        Assert.False(sink.IsPlaying);
    }

    [Fact]
    public void Playback_acquires_and_releases_the_platform_audio_session()
    {
        var platformAudioSession = new RecordingPlatformAudioSession();
        var (manager, _, _) = Make(platformAudioSession);

        manager.Resume();
        manager.Pause();
        manager.Play(T("A", TimeSpan.FromSeconds(10)));
        manager.Stop();

        Assert.Equal(2, platformAudioSession.ActivationCount);
        Assert.Equal(2, platformAudioSession.DeactivationCount);
    }

    [Fact]
    public void Stop_also_retires_the_current_decoder()
    {
        var (manager, decoder, _, _) = PlayTrack(T("A", TimeSpan.FromSeconds(1)));

        manager.Stop();

        Assert.True(decoder.RetireCalled);
    }

    [Fact]
    public void Volume_reads_and_writes_through_to_the_sink()
    {
        var (manager, _, sink) = Make();

        manager.Volume = 42;

        Assert.Equal(42, sink.Volume);
        Assert.Equal(42, manager.Volume);
    }

    [Fact]
    public void Volume_setter_raises_VolumeChanged()
    {
        var (manager, _, _) = Make();
        var raised = false;
        manager.VolumeChanged += (_, _) => raised = true;

        manager.Volume = 50;

        Assert.True(raised);
    }

    // Time/Position are driven off the shared ring's actual consumption
    // (GaplessCoordinator.CurrentTrackBytesProduced), not
    // FakeTrackDecoder.BytesProduced directly - see GaplessCoordinatorTests
    // for why a decode-side counter can't stand in for real playback
    // position. These write/read through the real ring to simulate "the
    // decoder produced N bytes and the sink consumed them".
    [Fact]
    public void Time_is_bytes_consumed_from_the_ring_converted_to_milliseconds()
    {
        var (manager, _, _, ring) = PlayTrack(T("A", TimeSpan.FromSeconds(10)));

        // Play() resumed the sink, and the sink's pump reads the same ring
        // this test is about to read. The ring is single-consumer by
        // contract, so the pump has to be off before the test can stand in
        // for it - FakeAudioSink.Pause() does not return until it is.
        manager.Pause();

        // 2 seconds of canonical PCM, consumed by the sink.
        var bytes = GaplessFormat.SampleRate * GaplessFormat.BytesPerFrame * 2;
        ring.TryWrite(new byte[bytes]);
        ring.Read(new byte[bytes]);

        Assert.Equal(2000, manager.Time);
    }

    [Fact]
    public void Position_is_time_over_length()
    {
        var (manager, _, _, ring) = PlayTrack(T("A", TimeSpan.FromSeconds(10)));

        // The sink's pump reads this same ring - see the test above.
        manager.Pause();

        // 5 of 10 seconds, consumed by the sink.
        var bytes = GaplessFormat.SampleRate * GaplessFormat.BytesPerFrame * 5;
        ring.TryWrite(new byte[bytes]);
        ring.Read(new byte[bytes]);

        Assert.Equal(0.5f, manager.Position);
    }

    [Fact]
    public void Position_setter_delegates_to_the_coordinators_seek()
    {
        var (manager, decoder, _, _) = PlayTrack(T("A", TimeSpan.FromSeconds(10)));

        manager.Position = 0.25f;

        Assert.Equal(0.25f, decoder.LastSeekPosition);
    }

    [Fact]
    public void Sink_Playing_Paused_Stopped_events_are_forwarded()
    {
        var (manager, _, _) = Make();
        var playingRaised = false;
        var pausedRaised = false;
        var stoppedRaised = false;
        manager.Playing += (_, _) => playingRaised = true;
        manager.Paused += (_, _) => pausedRaised = true;
        manager.Stopped += (_, _) => stoppedRaised = true;

        manager.Resume();
        manager.Pause();
        manager.Resume();
        manager.Stop();

        Assert.True(playingRaised);
        Assert.True(pausedRaised);
        Assert.True(stoppedRaised);
    }

    [Fact]
    public void EndReached_is_forwarded_from_the_coordinator()
    {
        var (manager, decoder, _, _) = PlayTrack(T("A", TimeSpan.FromSeconds(1)));
        var raised = false;
        manager.EndReached += (_, _) => raised = true;

        decoder.RaiseDrained();

        Assert.True(raised);
    }

    // A decode error - e.g. a network outage partway through a streamed
    // track - reaches the coordinator as Faulted rather than Drained (see
    // TrackDecoder.EncounteredError). The pipeline handles both the same way
    // (advance or stop), but they surface as different events all the way up:
    // PlaylistControlViewModel counts a play on EndReached, so a track that
    // failed must not arrive there.
    [Fact]
    public void TrackFailed_is_forwarded_from_the_coordinator_when_the_decoder_faults()
    {
        var track = T("A", TimeSpan.FromSeconds(1));
        var (manager, decoder, _, _) = PlayTrack(track);
        Track? failed = null;
        var endReached = false;
        manager.TrackFailed += (_, e) => failed = e.Track;
        manager.EndReached += (_, _) => endReached = true;

        decoder.RaiseFaulted();

        Assert.Same(track, failed);
        Assert.False(endReached);
    }

    [Fact]
    public void PositionChanged_fires_periodically_while_playing()
    {
        var (manager, _, _) = Make();
        manager.Resume();

        var raisedCount = 0;
        manager.PositionChanged += (_, _) => Interlocked.Increment(ref raisedCount);

        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref raisedCount) > 0, TimeSpan.FromSeconds(2)),
            "PositionChanged should fire at least once within the polling interval while playing");
    }

    [Fact]
    public void PositionChanged_does_not_fire_while_paused()
    {
        var (manager, _, _) = Make();

        var raisedCount = 0;
        manager.PositionChanged += (_, _) => Interlocked.Increment(ref raisedCount);

        Thread.Sleep(500);

        Assert.Equal(0, Volatile.Read(ref raisedCount));
    }

    [Fact]
    public void ApplyEqualizer_forwards_to_the_sink_unchanged()
    {
        var (manager, _, sink) = Make();
        var equalizer = Equalizer.BuildFrom(new EqualizerSettings { Enabled = true }, GaplessFormat.SampleRate);

        manager.ApplyEqualizer(equalizer);
        Assert.Same(equalizer, sink.AppliedEqualizer);

        manager.ApplyEqualizer(null);
        Assert.Null(sink.AppliedEqualizer);
    }

    private sealed class RecordingPlatformAudioSession : IPlatformAudioSession
    {
        public int ActivationCount { get; private set; }
        public int DeactivationCount { get; private set; }

        public void ActivateForPlayback() => ActivationCount++;
        public void DeactivateAfterPlayback() => DeactivationCount++;
    }
}
