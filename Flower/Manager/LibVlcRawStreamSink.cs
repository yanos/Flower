using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using LibVLCSharp.Shared;

namespace Flower.Manager
{
    // Former default, cross-platform IAudioSink: plays the shared
    // GaplessRingBuffer back out through LibVLC's own aout (WASAPI/
    // PulseAudio/ALSA/AudioTrack/CoreAudio, whichever the platform provides)
    // via a second, persistent MediaPlayer given a virtual, never-ending
    // raw-PCM stream as its input. From that player's point of view there is
    // only ever been one continuous stream, so it never stops-and-restarts
    // at a track boundary - that's what makes the transitions
    // GaplessCoordinator produces audible as gapless.
    //
    // Superseded on every platform by MiniaudioSink (see App.axaml.cs) after
    // this proved to have a real playback bug: a decode-side LibVLC seek
    // could freeze the render side solid for several seconds, because this
    // sink piggybacked on LibVLC's own rawaud demuxer/aout, which has a
    // decode/demux state machine that turned out to be shared/wedgeable
    // across MediaPlayer instances. MiniaudioSink's plain ma_device callback
    // has no such state machine to wedge. Kept here, unreferenced, as a
    // fallback for one release cycle rather than deleted outright - remove
    // once MiniaudioSink has had a full release cycle of real-world use on
    // every platform with no regressions.
    public sealed class LibVlcRawStreamSink : IAudioSink
    {
        private readonly LibVLC _libVLC;
        private readonly ILogger<LibVlcRawStreamSink> _logger;
        private readonly object _restartGate = new();
        private GaplessRingBuffer? _ringBuffer;
        private MediaPlayer? _renderPlayer;
        private GaplessRingBufferStream? _stream;
        private Media? _media;
        private volatile bool _disposed;

        // Diagnostic-only, temporary: logs the render player's own reported
        // state/position alongside the shared ring's underrun counter every
        // second. Event handlers (EncounteredError/EndReached below) only
        // catch a wedge if LibVLC actually signals one; this watchdog is
        // here to also catch a *silent* wedge - State staying "Playing"
        // while Time stops advancing - which the recovery hook wouldn't see
        // at all. Remove once the actual failure mode is identified.

        public event EventHandler? Playing;
        public event EventHandler? Paused;
        public event EventHandler? Stopped;

        public LibVlcRawStreamSink(LibVLC libVLC, ILogger<LibVlcRawStreamSink> logger)
        {
            _libVLC = libVLC;
            _logger = logger;
        }

        public bool IsPlaying => _renderPlayer?.IsPlaying ?? false;

        public int Volume
        {
            get => _renderPlayer?.Volume ?? 0;
            set
            {
                if (_renderPlayer != null)
                    _renderPlayer.Volume = value;
            }
        }

        // No-op: this sink is kept only as an unreferenced one-release-cycle
        // fallback (see class remarks) - EQ was never implemented against
        // LibVLC's rawaud render path and isn't worth adding now that
        // MiniaudioSink is the sole production sink.
        public void ApplyEqualizer(Equalizer? equalizer)
        {
        }

        // No EQ and no output stage here either - LibVLC's own aout owns the
        // render path in this fallback sink, so there is nothing to tune.
        public void ApplyTiming(AudioTimingSettings timing)
        {
        }

        // Output routing was never wired up here: this sink was already
        // superseded by MiniaudioSink before the picker existed, and adding
        // LibVLC's AudioOutputDeviceEnum/Set plumbing to a class kept only as
        // a one-release fallback would be work aimed at deletion. Reporting no
        // devices leaves the picker hidden if this sink is ever put back in.
        public IReadOnlyList<AudioOutputDevice> GetOutputDevices() => [];

        public string? OutputDeviceId => null;

        public void SetOutputDevice(string? deviceId)
        {
        }

        // Never raised, for the same reason: LibVLC's aout hides which device
        // it landed on, so there is nothing here that could notice one going
        // away. Declared only to satisfy IAudioSink.
#pragma warning disable CS0067
        public event EventHandler? OutputDeviceLost;
#pragma warning restore CS0067

        public void Start(GaplessRingBuffer ringBuffer)
        {
            _ringBuffer = ringBuffer;
            StartRenderPlayer();
        }

        // Builds (or, via RecoverAsync, rebuilds) the render MediaPlayer
        // against whatever the current shared ring is. Rebuilding is safe
        // even mid-playback: the ring itself (owned by GaplessCoordinator,
        // still being fed by the decode side) is untouched here, so a fresh
        // render player just resumes reading it from wherever the old one's
        // demux thread left off (or self-resets, if a
        // GaplessRingBuffer.Reset() happened in between).
        private void StartRenderPlayer()
        {
            lock (_restartGate)
            {
                if (_disposed || _ringBuffer == null)
                    return;

                TeardownRenderPlayer();

                _logger.LogDebug("Render player (re)starting");

                _stream = new GaplessRingBufferStream(_ringBuffer);
                _renderPlayer = new MediaPlayer(_libVLC);
                _renderPlayer.Playing += (_, e) =>
                {
                    _logger.LogTrace("Render player: Playing");
                    Playing?.Invoke(this, e);
                };
                _renderPlayer.Paused += (_, e) =>
                {
                    _logger.LogTrace("Render player: Paused");
                    Paused?.Invoke(this, e);
                };
                _renderPlayer.Stopped += (_, e) =>
                {
                    _logger.LogTrace("Render player: Stopped");
                    Stopped?.Invoke(this, e);
                };

                // This virtual stream never legitimately reaches
                // end-of-stream while running - Length reports "unknown"
                // and Read() only ever returns 0 after MarkEnded() on final
                // shutdown. If LibVLC's rawaud pipeline decides otherwise
                // anyway, or hits an internal error, the pipeline has been
                // observed (in practice, after a rapid scrub) to wedge into
                // a state where Play/Pause/Stop calls silently no-op from
                // then on with only a stale buffer left looping at the
                // driver level. Nothing on the decode/coordinator side can
                // detect or fix a wedged render player, so the only way
                // back is rebuilding it from scratch here.
                _renderPlayer.EncounteredError += (_, _) =>
                {
                    _logger.LogWarning("Render player encountered an error - rebuilding");
                    RecoverAsync();
                };
                _renderPlayer.EndReached += (_, _) =>
                {
                    _logger.LogWarning("Render player unexpectedly reached end of stream - rebuilding");
                    RecoverAsync();
                };

                _media = new Media(
                    _libVLC,
                    new StreamMediaInput(_stream),
                    ":demux=rawaud",
                    $":rawaud-channels={GaplessFormat.Channels}",
                    $":rawaud-samplerate={GaplessFormat.SampleRate}",
                    // s16l, not GaplessFormat.LibVlcFourCc ("S16N") - rawaud
                    // builds a raw fourcc straight from these 4 bytes rather
                    // than matching against a format-name table, so it needs
                    // the explicit-endianness spelling. Every platform this
                    // runs on is little-endian, matching the host-endian S16N
                    // the decode side actually receives (see GaplessFormat).
                    ":rawaud-fourcc=s16l");

                _renderPlayer.Play(_media);
            }
        }

        private void RecoverAsync() => Task.Run(StartRenderPlayer);

        private void TeardownRenderPlayer()
        {
            _stream?.MarkEnded();
            _renderPlayer?.Stop();
            _renderPlayer?.Dispose();
            _media?.Dispose();
        }

        public void Resume()
        {
                        _renderPlayer?.SetPause(false);
        }

        public void Pause()
        {
                        _renderPlayer?.SetPause(true);
        }

        public void Stop()
        {
                        _renderPlayer?.Stop();
        }

        public void Dispose()
        {
            _disposed = true;
            lock (_restartGate)
                TeardownRenderPlayer();
        }
    }
}
