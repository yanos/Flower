using System;
using System.Timers;

using Microsoft.Extensions.Logging;

using LibVLCSharp.Shared;

using Flower.Models;

namespace Flower.Manager
{
    // The one IAudioManager implementation used on every platform: decode
    // (via GaplessCoordinator/TrackDecoder) and render (via the injected
    // IAudioSink) are fully decoupled, so gapless playback itself needs
    // nothing platform-specific - only the sink differs (LibVlcRawStreamSink
    // by default; Flower.Apple's AppleAudioEngineSink on macOS/iOS for real
    // AirPlay/Bluetooth routing).
    public sealed class GaplessAudioManager : IAudioManager, IDisposable
    {
        // ~2s of canonical PCM - just enough headroom between the decoder
        // and the render sink to absorb normal scheduling jitter; the real
        // decode-ahead buffering for gapless transitions happens in each
        // TrackDecoder's own staging ring (see GaplessCoordinator), not here.
        private const int RingCapacityBytes = 2 * (int)GaplessFormat.SampleRate * GaplessFormat.BytesPerFrame;

        private readonly GaplessRingBuffer _sharedRing;
        private readonly GaplessCoordinator _coordinator;
        private readonly IAudioSink _sink;
        private readonly Timer _positionTimer;
        private readonly ILogger<GaplessAudioManager> _logger;

        public event EventHandler? Paused;
        public event EventHandler? Stopped;
        public event EventHandler? Playing;
        public event EventHandler? PositionChanged;
        public event EventHandler? VolumeChanged;
        public event EventHandler? EndReached;

        // libVLC is a dependency, not owned/created here, because a
        // LibVlcRawStreamSink needs to share the exact same LibVLC core for
        // its own render MediaPlayer - see App.axaml.cs's composition root.
        public GaplessAudioManager(
            LibVLC libVLC,
            IAudioSink sink,
            ILogger<GaplessAudioManager> logger,
            ILogger<GaplessCoordinator>? coordinatorLogger = null,
            ILogger<TrackDecoder>? trackDecoderLogger = null)
        {
            _sink = sink;
            _logger = logger;

            _sharedRing = new GaplessRingBuffer(RingCapacityBytes);
            _coordinator = new GaplessCoordinator(libVLC, _sharedRing, coordinatorLogger, trackDecoderLogger);
            _coordinator.EndReached += track =>
            {
                _logger.LogInformation("EndReached: {Path}", track.Path);
                EndReached?.Invoke(this, EventArgs.Empty);
            };

            _sink.Playing += (_, e) => Playing?.Invoke(this, e);
            _sink.Paused += (_, e) => Paused?.Invoke(this, e);
            _sink.Stopped += (_, e) => Stopped?.Invoke(this, e);
            _sink.Start(_sharedRing);

            // VlcAudioManager's single MediaPlayer used to raise
            // PositionChanged on its own as it played; splitting decode from
            // render means nothing does that automatically anymore, so this
            // polls CurrentlyPlayingControlViewModel's seek bar/elapsed-time
            // dependency at the same cadence VLC's own event fired at.
            _positionTimer = new Timer(250);
            _positionTimer.Elapsed += (_, _) =>
            {
                if (IsPlaying)
                    PositionChanged?.Invoke(this, EventArgs.Empty);
            };
            _positionTimer.Start();
        }

        public bool IsPlaying => _sink.IsPlaying;

        public int Volume
        {
            get => _sink.Volume;
            set
            {
                _sink.Volume = value;
                VolumeChanged?.Invoke(this, EventArgs.Empty);
            }
        }

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

        public void Play(Track track)
        {
            _logger.LogInformation("Play({Path})", track.Path);
            _coordinator.Play(track);
            _sink.Resume();
        }

        public void SetUpcoming(Track? next) => _coordinator.SetUpcoming(next);

        public void Resume()
        {
            _logger.LogInformation("Resume()");
            _sink.Resume();
        }

        public void Pause()
        {
            _logger.LogInformation("Pause() - IsPlaying was {IsPlaying}", IsPlaying);
            _sink.Pause();
        }

        public void Stop()
        {
            _logger.LogInformation("Stop()");
            _coordinator.Stop();
            _sink.Stop();
        }

        public void Dispose()
        {
            _positionTimer.Dispose();
            _coordinator.Dispose();
            _sink.Dispose();
        }
    }
}
