using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.JavaScript;
using System.Timers;

using Flower.Models;

namespace Flower.Manager
{
    // Browser's IAudioManager - see App.axaml.cs's Bootstrap() for the
    // OperatingSystem.IsBrowser() fork that constructs this instead of
    // GaplessAudioManager. LibVLC/Miniaudio ship no WASM build, so unlike
    // every other platform this doesn't go through IAudioSink/
    // GaplessRingBuffer/GaplessCoordinator at all - it drives a single
    // browser <audio> element (wwwroot/webaudio.js) via [JSImport] and lets
    // the browser's own decoder handle the file, the same way a <audio src>
    // tag would in plain HTML. Accepted v1 scope call (see SYNC-PLAN.md's
    // Flower.Web section): no gapless handover, no EQ, no decode-ahead -
    // "browser playback works, gaplessness may lag the native engine."
    //
    // Position/Time/Length are polled on a timer (mirrors
    // GaplessAudioManager's own 250ms PositionChanged timer) rather than
    // wired to JS callbacks - avoids needing [JSExport] and the
    // module-export lookup that would require, at the cost of up to one
    // poll interval of staleness, same tradeoff MiniaudioSink accepts for
    // its own diagnostic watchdog.
    public sealed partial class WebAudioManager : IAudioManager, IDisposable
    {
        // Must match the module name Program.cs imports webaudio.js under
        // (JSHost.ImportAsync(ModuleName, "./webaudio.js")) - imported once,
        // before Bootstrap() runs, so every [JSImport] call below resolves
        // against an already-loaded module.
        public const string ModuleName = "webaudio";

        private static partial class Interop
        {
            [JSImport("setSrc", ModuleName)]
            public static partial void SetSrc(string url);

            [JSImport("play", ModuleName)]
            public static partial void Play();

            [JSImport("pause", ModuleName)]
            public static partial void Pause();

            [JSImport("stop", ModuleName)]
            public static partial void Stop();

            [JSImport("seek", ModuleName)]
            public static partial void Seek(double seconds);

            [JSImport("setVolume", ModuleName)]
            public static partial void SetVolume(double volume);

            [JSImport("getCurrentTime", ModuleName)]
            public static partial double GetCurrentTime();

            [JSImport("getDuration", ModuleName)]
            public static partial double GetDuration();

            [JSImport("getPaused", ModuleName)]
            public static partial bool GetPaused();

            [JSImport("getEnded", ModuleName)]
            public static partial bool GetEnded();
        }

        private readonly Timer _positionTimer;
        private int _volume = 100;
        private bool _isPlaying;
        private bool _endReachedFired;

        public event EventHandler? Paused;
        public event EventHandler? Stopped;
        public event EventHandler? Playing;
        public event EventHandler? PositionChanged;
        public event EventHandler? VolumeChanged;
        public event EventHandler? EndReached;

        // Declared to satisfy IAudioManager, never raised: this drives a
        // browser <audio> element rather than the decode pipeline that produces
        // fault signals, and its "error" event isn't wired through yet. A
        // browser-side decode failure therefore still just stops, the same as
        // before - #pragma because "never assigned" is exactly the point.
#pragma warning disable CS0067
        public event EventHandler<TrackFailedEventArgs>? TrackFailed;
#pragma warning restore CS0067

        public WebAudioManager()
        {
            _positionTimer = new Timer(250);
            _positionTimer.Elapsed += (_, _) => Poll();
            _positionTimer.Start();
        }

        public bool IsPlaying => _isPlaying;

        public int Volume
        {
            get => _volume;
            set
            {
                _volume = Math.Clamp(value, 0, 100);
                Interop.SetVolume(_volume / 100.0);
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
            set
            {
                var length = Length;
                if (length > 0)
                    Interop.Seek(value * length / 1000.0);
            }
        }

        public long Time => (long)(Interop.GetCurrentTime() * 1000);

        public long Length => (long)(Interop.GetDuration() * 1000);

        public void Play(Track track)
        {
            _endReachedFired = false;
            Interop.SetSrc(track.Path ?? string.Empty);
            Interop.SetVolume(_volume / 100.0);
            Interop.Play();
            _isPlaying = true;
            Playing?.Invoke(this, EventArgs.Empty);
        }

        // No decode-ahead in the browser v1 - the <audio> element only
        // knows its current src. A future pass could preload the next
        // track's URL into a second, hidden <audio> element to shave the
        // gap, but that's real gapless engineering, not this milestone.
        public void SetUpcoming(Track? next)
        {
        }

        public void Resume()
        {
            Interop.Play();
            _isPlaying = true;
            Playing?.Invoke(this, EventArgs.Empty);
        }

        public void Pause()
        {
            Interop.Pause();
            _isPlaying = false;
            Paused?.Invoke(this, EventArgs.Empty);
        }

        public void Stop()
        {
            Interop.Stop();
            _isPlaying = false;
            Stopped?.Invoke(this, EventArgs.Empty);
        }

        // No in-browser EQ in v1 - see class remarks.
        public void ApplyEqualizer(Equalizer? equalizer)
        {
        }

        // A page cannot choose the machine's output device the way a native
        // app can - the browser routes <audio> wherever the OS and the user's
        // own browser-level output setting say. Reporting no devices is what
        // keeps the picker hidden in Flower.Web.
        public IReadOnlyList<AudioOutputDevice> GetOutputDevices() => [];

        public string? OutputDeviceId => null;

        public void SetOutputDevice(string? deviceId)
        {
        }

        private void Poll()
        {
            _isPlaying = !Interop.GetPaused();
            PositionChanged?.Invoke(this, EventArgs.Empty);

            if (!_endReachedFired && Interop.GetEnded())
            {
                _endReachedFired = true;
                _isPlaying = false;
                EndReached?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Dispose()
        {
            _positionTimer.Dispose();
        }
    }
}
