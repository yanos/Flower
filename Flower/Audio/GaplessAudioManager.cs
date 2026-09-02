using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Timers;

using Timer = System.Timers.Timer;

using Microsoft.Extensions.Logging;

using LibVLCSharp.Shared;

using Flower.Models;

namespace Flower.Audio
{
    // The one IAudioManager implementation used on every platform: decode
    // (via GaplessCoordinator/TrackDecoder) and render (via the injected
    // IAudioSink) are fully decoupled, so gapless playback itself needs
    // nothing platform-specific - and today no platform even differs: the
    // sink is MiniaudioSink everywhere, since Apple output routing turned out
    // to be a session concern rather than a sink one (see
    // docs/AIRPLAY-BLUETOOTH-PLAN.md Phase 2).
    public sealed class GaplessAudioManager : IAudioManager, IDisposable
    {
        // ~2s of canonical PCM - just enough headroom between the decoder
        // and the render sink to absorb normal scheduling jitter; the real
        // decode-ahead buffering for gapless transitions happens in each
        // TrackDecoder's own staging ring (see GaplessCoordinator), not here.
        private readonly GaplessRingBuffer _sharedRing;
        private readonly GaplessCoordinator _coordinator;
        private readonly IAudioSink _sink;
        private readonly IPlatformAudioSession? _platformAudioSession;
        private readonly Timer _positionTimer;
        private readonly ILogger<GaplessAudioManager> _logger;
        private long _lastDiagnosticTimestamp = Stopwatch.GetTimestamp();

        public event EventHandler? Paused;
        public event EventHandler? Stopped;
        public event EventHandler? Playing;
        public event EventHandler? PositionChanged;
        public event EventHandler? VolumeChanged;
        public event EventHandler? EndReached;
        public event EventHandler<TrackFailedEventArgs>? TrackFailed;

        // libVLC is a dependency, not owned/created here, because a
        // LibVlcRawStreamSink needs to share the exact same LibVLC core for
        // its own render MediaPlayer - see App.axaml.cs's composition root.
        public GaplessAudioManager(
            LibVLC libVLC,
            IAudioSink sink,
            ILogger<GaplessAudioManager> logger,
            ILogger<GaplessCoordinator>? coordinatorLogger = null,
            ILogger<TrackDecoder>? trackDecoderLogger = null)
            : this(StartSink(sink), libVLC, sink, logger, coordinatorLogger, trackDecoderLogger)
        {
        }

        private GaplessAudioManager(
            GaplessRingBuffer sharedRing,
            LibVLC libVLC,
            IAudioSink sink,
            ILogger<GaplessAudioManager> logger,
            ILogger<GaplessCoordinator>? coordinatorLogger,
            ILogger<TrackDecoder>? trackDecoderLogger)
            : this(sharedRing, new GaplessCoordinator(libVLC, sharedRing, coordinatorLogger, trackDecoderLogger), sink, logger, sinkAlreadyStarted: true)
        {
        }

        private static GaplessRingBuffer StartSink(IAudioSink sink)
        {
            // The device tells MiniaudioSink its native rate during Start.
            // Allocate for the conservative 48k fallback first; 44.1k gets a
            // little more than two seconds of headroom, higher-rate devices a
            // little less, without delaying the native-rate negotiation.
            var ring = new GaplessRingBuffer(2 * (int)GaplessFormat.DefaultSampleRate * GaplessFormat.BytesPerFrame);
            sink.Start(ring);
            return ring;
        }

        // Lets tests substitute a GaplessCoordinator built against a fake
        // ITrackDecoder factory, so this class's own glue logic (Time/
        // Position math, event forwarding, Play/Resume/Pause/Stop
        // delegation) can be exercised without a real LibVLC - mirrors
        // GaplessCoordinator's own fake-decoder-factory constructor, which
        // exists for exactly the same reason.
        public GaplessAudioManager(
            GaplessRingBuffer sharedRing,
            GaplessCoordinator coordinator,
            IAudioSink sink,
            ILogger<GaplessAudioManager> logger,
            IPlatformAudioSession? platformAudioSession = null,
            bool sinkAlreadyStarted = false)
        {
            _sink = sink;
            _logger = logger;
            _platformAudioSession = platformAudioSession ?? PlatformAudioSession.Current;

            _sharedRing = sharedRing;
            _coordinator = coordinator;
            _coordinator.EndReached += track =>
            {
                // Trace: PlaylistControlViewModel logs the same event with the
                // track title and the queue decision it led to, which is the
                // version worth reading. This one is the plumbing underneath it.
                _logger.LogTrace("EndReached: {Path}", track.Path);
                EndReached?.Invoke(this, EventArgs.Empty);
            };
            _coordinator.TrackFailed += track =>
            {
                // Warning, not Information: this is a file the user asked for
                // and did not get. It's also the only signal they have until
                // the UI grows a real "couldn't play this" surface - the Log
                // window is reachable, a silently-skipped track is not.
                _logger.LogWarning("Playback failed for {Path} - decode error, skipping", track.Path);
                TrackFailed?.Invoke(this, new TrackFailedEventArgs(track));
            };

            // Unsubscribed in Dispose: the platform session is a process-wide
            // singleton set once at startup, so it outlives every manager built
            // against it - a test that builds and drops one must not leave it
            // still pausing a disposed sink.
            if (_platformAudioSession != null)
            {
                _platformAudioSession.OutputDeviceLost += OnOutputDeviceLost;
                _platformAudioSession.PlaybackInterrupted += OnPlaybackInterrupted;
                _platformAudioSession.PlaybackInterruptionEnded += OnPlaybackInterruptionEnded;
            }

            // The same fact from the other direction, and deliberately the
            // same handler: on iOS only the AVAudioSession can see a route
            // vanish, and everywhere else only the sink's own backend can. The
            // two never both fire, and what to do about it is one decision, so
            // it is written once.
            _sink.OutputDeviceLost += OnOutputDeviceLost;

            _sink.Playing += (_, e) => Playing?.Invoke(this, e);
            _sink.Paused += (_, e) => Paused?.Invoke(this, e);
            _sink.Stopped += (_, e) => Stopped?.Invoke(this, e);
            _userVolume = _sink.Volume;
            if (!sinkAlreadyStarted)
                _sink.Start(_sharedRing);

            // The former VlcAudioManager's single MediaPlayer used to raise
            // PositionChanged on its own as it played; splitting decode from
            // render means nothing does that automatically anymore, so this
            // polls CurrentlyPlayingControlViewModel's seek bar/elapsed-time
            // dependency at the same cadence VLC's own event fired at.
            _positionTimer = new Timer(250);
            _positionTimer.Elapsed += (_, _) =>
            {
                if (IsPlaying)
                {
                    PositionChanged?.Invoke(this, EventArgs.Empty);
                    LogDiagnosticSnapshotIfDue();
                }
            };
            _positionTimer.Start();
        }

        public bool IsPlaying => _sink.IsPlaying;

        // The user's own volume, kept here rather than read back off the sink:
        // the sink carries Volume + VolumeOffset, so once a track with its own
        // adjustment is playing the sink no longer knows what the user asked
        // for. Seeded from the sink so the slider starts where the backend
        // actually is.
        private int _userVolume;
        private int _volumeOffset;

        public int Volume
        {
            get => _userVolume;
            set
            {
                _userVolume = Math.Clamp(value, 0, 100);
                ApplyVolume();
                VolumeChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public int VolumeOffset
        {
            get => _volumeOffset;
            set
            {
                if (_volumeOffset == value)
                    return;

                _volumeOffset = value;
                ApplyVolume();
            }
        }

        private void ApplyVolume() => _sink.Volume = Math.Clamp(_userVolume + _volumeOffset, 0, 100);

        public float Position
        {
            get
            {
                var length = Length;
                return length > 0 ? (float)Time / length : 0f;
            }
            set => _coordinator.Seek(value);
        }

        public long Time
        {
            get
            {
                var bytesProduced = _coordinator.CurrentTrackBytesProduced;
                return (long)(bytesProduced / (double)GaplessFormat.BytesPerFrame / GaplessFormat.SampleRate * 1000);
            }
        }

        public long Length => (long)(_coordinator.CurrentTrack?.Duration.TotalMilliseconds ?? 0);

        public void Play(Track track, bool immediate = true)
        {
            _logger.LogInformation(
                "Playback requested: {Title} ({Path}), tagged duration {DurationMs}ms, immediate={Immediate}",
                track.Title, track.Path, track.Duration.TotalMilliseconds, immediate);
            _coordinator.Play(track, immediate);
            _platformAudioSession?.ActivateForPlayback();
            _sink.Resume();
        }

        public void SetUpcoming(Track? next) => _coordinator.SetUpcoming(next);

        public void Resume()
        {
            _logger.LogInformation("Playback resumed at {ElapsedMs}ms", Time);
            _platformAudioSession?.ActivateForPlayback();
            _sink.Resume();
        }

        public void Pause()
        {
            _logger.LogInformation("Playback paused at {ElapsedMs}ms; IsPlaying={IsPlaying}", Time, IsPlaying);
            var wasPlaying = _sink.IsPlaying;
            _sink.Pause();
            if (wasPlaying)
                _platformAudioSession?.DeactivateAfterPlayback();
        }

        public void Stop()
        {
            _logger.LogInformation("Playback stopped at {ElapsedMs}ms; IsPlaying={IsPlaying}", Time, IsPlaying);
            // Sink first: its stop fades the output down over
            // TransportFadeMs, and the coordinator's Reset() would leave it
            // nothing but silence to fade.
            var wasPlaying = _sink.IsPlaying;
            _sink.Stop();
            _coordinator.Stop();
            if (wasPlaying)
                _platformAudioSession?.DeactivateAfterPlayback();
        }

        public void ApplyEqualizer(Equalizer? equalizer) => _sink.ApplyEqualizer(equalizer);

        public void ApplyAudioTiming(AudioTimingSettings timing) => _sink.ApplyTiming(timing);

        public IReadOnlyList<AudioOutputDevice> GetOutputDevices() => _sink.GetOutputDevices();

        public string? OutputDeviceId => _sink.OutputDeviceId;

        public void SetOutputDevice(string? deviceId)
        {
            _logger.LogInformation("Output device changed to {DeviceId}", deviceId ?? "the system default");
            _sink.SetOutputDevice(deviceId);
        }

        private void LogDiagnosticSnapshotIfDue()
        {
            var now = Stopwatch.GetTimestamp();
            var previous = Volatile.Read(ref _lastDiagnosticTimestamp);
            if (Stopwatch.GetElapsedTime(previous, now) < TimeSpan.FromSeconds(10))
                return;

            if (Interlocked.CompareExchange(ref _lastDiagnosticTimestamp, now, previous) != previous)
                return;

            _coordinator.LogDiagnosticSnapshot(renderStarted: _sink.IsPlaying);
        }

        // The user's headphones/Bluetooth output disappeared. Pausing is what
        // every music app on the platform does; the alternative is Flower
        // carrying on at full volume through the handset speaker, or the
        // laptop's, in a quiet room. Routed through Pause() rather than
        // straight at the sink so the audio session is released too, exactly
        // as a tapped pause button would.
        //
        // Reached from either reporter - IPlatformAudioSession on iOS,
        // IAudioSink everywhere else - because the decision is the same one
        // whichever of them noticed. Both arrive on the UI thread by contract.
        private void OnOutputDeviceLost(object? sender, EventArgs e)
        {
            if (!IsPlaying)
                return;

            _logger.LogInformation("Output device disappeared; pausing playback");
            Pause();
        }

        // Unlike a lost output device, an interruption is expected to end, and
        // whether Flower was playing when it began is the whole question at
        // that point: a call arriving while the app sits paused must not leave
        // it playing when the call ends.
        private bool _wasPlayingWhenInterrupted;

        private void OnPlaybackInterrupted(object? sender, EventArgs e)
        {
            _wasPlayingWhenInterrupted = IsPlaying;
            if (!IsPlaying)
                return;

            _logger.LogInformation("Audio interrupted; pausing playback");
            Pause();
        }

        private void OnPlaybackInterruptionEnded(object? sender, PlaybackInterruptionEndedEventArgs e)
        {
            var wasPlaying = _wasPlayingWhenInterrupted;
            _wasPlayingWhenInterrupted = false;

            if (!wasPlaying || !e.ShouldResume)
            {
                _logger.LogInformation(
                    "Audio interruption ended; staying paused (was playing: {WasPlaying}, may resume: {ShouldResume})",
                    wasPlaying, e.ShouldResume);
                return;
            }

            // Resume rather than Play: the coordinator still holds the track and
            // its position, and this is the same road the play button takes -
            // including re-activating the session the OS took away.
            _logger.LogInformation("Audio interruption ended; resuming playback");
            Resume();
        }

        public void Dispose()
        {
            if (_platformAudioSession != null)
            {
                _platformAudioSession.OutputDeviceLost -= OnOutputDeviceLost;
                _platformAudioSession.PlaybackInterrupted -= OnPlaybackInterrupted;
                _platformAudioSession.PlaybackInterruptionEnded -= OnPlaybackInterruptionEnded;
            }

            _sink.OutputDeviceLost -= OnOutputDeviceLost;

            _positionTimer.Dispose();
            _coordinator.Dispose();
            _sink.Dispose();
        }
    }
}
