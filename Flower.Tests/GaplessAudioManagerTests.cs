using System;
using System.Threading;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Audio;
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

        // A drain that produced nothing is reported as TrackFailed instead -
        // see GaplessCoordinator.HandleDrainedOrFaulted - so a track standing
        // in for one the listener heard has to have made some audio.
        decoder.BytesProduced = 4096;
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

    // docs/AIRPLAY-BLUETOOTH-PLAN.md Phase 2: iOS reports a vanished output
    // (AirPods out, Bluetooth speaker off, headphones unplugged) through the
    // platform session, every other platform reports the same thing through
    // the sink, and the response - pause rather than carry on out loud - is
    // shared logic that lives here, not on either of them.
    [Fact]
    public void A_vanished_output_device_pauses_playback()
    {
        var platformAudioSession = new RecordingPlatformAudioSession();
        var (manager, _, sink) = Make(platformAudioSession);
        var paused = 0;
        manager.Paused += (_, _) => paused++;

        manager.Play(T("A", TimeSpan.FromSeconds(10)));
        platformAudioSession.LoseOutputDevice();

        Assert.False(sink.IsPlaying);
        Assert.Equal(1, paused);

        // Routed through Pause() rather than straight at the sink, so the
        // audio session is released exactly as a tapped pause button would.
        Assert.Equal(1, platformAudioSession.DeactivationCount);
    }

    [Fact]
    public void A_vanished_output_device_is_ignored_when_nothing_is_playing()
    {
        var platformAudioSession = new RecordingPlatformAudioSession();
        var (manager, _, _) = Make(platformAudioSession);
        var paused = 0;
        manager.Paused += (_, _) => paused++;

        platformAudioSession.LoseOutputDevice();

        Assert.Equal(0, paused);
        Assert.Equal(0, platformAudioSession.DeactivationCount);
    }

    // The platform session is a process-wide singleton set once at startup, so
    // it outlives every manager built against it.
    [Fact]
    public void A_disposed_manager_stops_listening_for_output_device_loss()
    {
        var platformAudioSession = new RecordingPlatformAudioSession();
        var (manager, _, _) = Make(platformAudioSession);
        var paused = 0;
        manager.Paused += (_, _) => paused++;

        manager.Play(T("A", TimeSpan.FromSeconds(10)));
        manager.Dispose();
        platformAudioSession.LoseOutputDevice();

        Assert.Equal(0, paused);
        Assert.Equal(0, platformAudioSession.DeactivationCount);
    }

    // The desktop half of the same policy. There is no IPlatformAudioSession
    // on macOS/Windows/Linux at all; MiniaudioSink notices its own device
    // disappearing and reports it here instead, and lands on exactly the same
    // handler.
    [Fact]
    public void A_sink_reported_device_loss_pauses_playback_too()
    {
        var (manager, _, sink) = Make();
        var paused = 0;
        manager.Paused += (_, _) => paused++;

        manager.Play(T("A", TimeSpan.FromSeconds(10)));
        sink.RaiseOutputDeviceLost();

        Assert.False(sink.IsPlaying);
        Assert.Equal(1, paused);
    }

    [Fact]
    public void A_sink_reported_device_loss_is_ignored_when_nothing_is_playing()
    {
        var (manager, _, sink) = Make();
        var paused = 0;
        manager.Paused += (_, _) => paused++;

        sink.RaiseOutputDeviceLost();

        Assert.Equal(0, paused);
    }

    [Fact]
    public void A_disposed_manager_stops_listening_to_the_sink_for_device_loss()
    {
        var (manager, _, sink) = Make();
        var paused = 0;
        manager.Paused += (_, _) => paused++;

        manager.Play(T("A", TimeSpan.FromSeconds(10)));
        manager.Dispose();
        sink.RaiseOutputDeviceLost();

        Assert.Equal(0, paused);
    }

    // A phone call, Siri, an alarm: iOS has already silenced the app by the
    // time it says so, and the state has to follow or the transport controls
    // and the Lock Screen card go on claiming a track is playing in silence.
    [Fact]
    public void An_interruption_pauses_playback()
    {
        var platformAudioSession = new RecordingPlatformAudioSession();
        var (manager, _, _) = Make(platformAudioSession);
        var paused = 0;
        manager.Paused += (_, _) => paused++;

        manager.Play(T("A", TimeSpan.FromSeconds(10)));
        platformAudioSession.Interrupt();

        Assert.Equal(1, paused);
        Assert.False(manager.IsPlaying);
    }

    [Fact]
    public void Playback_resumes_when_the_interruption_ends_and_the_system_allows_it()
    {
        var platformAudioSession = new RecordingPlatformAudioSession();
        var (manager, _, _) = Make(platformAudioSession);

        manager.Play(T("A", TimeSpan.FromSeconds(10)));
        platformAudioSession.Interrupt();
        platformAudioSession.EndInterruption(shouldResume: true);

        Assert.True(manager.IsPlaying);
        // The OS took the session away with the interruption, so coming back
        // has to ask for it again rather than assume it is still held.
        Assert.Equal(2, platformAudioSession.ActivationCount);
    }

    // The flag is the OS's own answer to "may this app start again by itself",
    // and honouring it is what keeps a Flower that was interrupted into silence
    // from bursting into song in someone's pocket.
    [Fact]
    public void Playback_stays_paused_when_the_system_does_not_offer_to_resume()
    {
        var platformAudioSession = new RecordingPlatformAudioSession();
        var (manager, _, _) = Make(platformAudioSession);

        manager.Play(T("A", TimeSpan.FromSeconds(10)));
        platformAudioSession.Interrupt();
        platformAudioSession.EndInterruption(shouldResume: false);

        Assert.False(manager.IsPlaying);
    }

    // A call arriving while Flower sits paused must not leave it playing when
    // the call ends - ShouldResume means "you may", not "you were".
    [Fact]
    public void An_interruption_while_paused_does_not_start_playback_when_it_ends()
    {
        var platformAudioSession = new RecordingPlatformAudioSession();
        var (manager, _, _) = Make(platformAudioSession);

        manager.Play(T("A", TimeSpan.FromSeconds(10)));
        manager.Pause();
        platformAudioSession.Interrupt();
        platformAudioSession.EndInterruption(shouldResume: true);

        Assert.False(manager.IsPlaying);
    }

    [Fact]
    public void A_disposed_manager_stops_listening_for_interruptions()
    {
        var platformAudioSession = new RecordingPlatformAudioSession();
        var (manager, _, _) = Make(platformAudioSession);
        var paused = 0;
        manager.Paused += (_, _) => paused++;

        manager.Play(T("A", TimeSpan.FromSeconds(10)));
        manager.Dispose();
        platformAudioSession.Interrupt();

        Assert.Equal(0, paused);
    }

    private sealed class RecordingPlatformAudioSession : IPlatformAudioSession
    {
        public int ActivationCount { get; private set; }
        public int DeactivationCount { get; private set; }

        public event EventHandler? OutputDeviceLost;
        public event EventHandler? PlaybackInterrupted;
        public event EventHandler<PlaybackInterruptionEndedEventArgs>? PlaybackInterruptionEnded;

        public void ActivateForPlayback() => ActivationCount++;
        public void DeactivateAfterPlayback() => DeactivationCount++;

        public void LoseOutputDevice() => OutputDeviceLost?.Invoke(this, EventArgs.Empty);

        public void Interrupt() => PlaybackInterrupted?.Invoke(this, EventArgs.Empty);

        public void EndInterruption(bool shouldResume) =>
            PlaybackInterruptionEnded?.Invoke(this, new PlaybackInterruptionEndedEventArgs(shouldResume));
    }
}
