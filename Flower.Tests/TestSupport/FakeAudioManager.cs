using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Manager;
using Flower.Models;

namespace Flower.Tests.TestSupport;

// Minimal stand-in for GaplessAudioManager. Play()/SetUpcoming() just
// record the last-passed Track for assertions; Resume/Pause/Stop are
// no-ops. Events can be raised manually via RaiseEndReached() etc. for
// tests that need to drive PlaylistControlViewModel's event-handling logic
// (e.g. auto-advance on EndReached) without a real audio pipeline.
public sealed class FakeAudioManager : IAudioManager
{
    public bool IsPlaying { get; set; }
    public int Volume { get; set; }
    public int VolumeOffset { get; set; }

    // What the real managers would actually drive the sink with - the two
    // knobs summed and clamped, so a test can assert on the sound rather than
    // on the arithmetic.
    public int EffectiveVolume => Math.Clamp(Volume + VolumeOffset, 0, 100);
    public float Position { get; set; }
    public long Time { get; set; }
    public long Length { get; set; }

    public Track? LastPlayed { get; private set; }

    // Whether the last Play was a user gesture or the queue advancing - see
    // IAudioManager.Play.
    public bool LastPlayWasImmediate { get; private set; } = true;
    public Track? LastUpcoming { get; private set; }

    public Equalizer? LastAppliedEqualizer { get; private set; }

    public void Play(Track track, bool immediate = true)
    {
        LastPlayed = track;
        LastPlayWasImmediate = immediate;
    }
    public void SetUpcoming(Track? next) => LastUpcoming = next;
    public void Resume() { }
    public void Pause() { }
    public void Stop() { }
    public void ApplyEqualizer(Equalizer? equalizer) => LastAppliedEqualizer = equalizer;

    public AudioTimingSettings? LastAppliedTiming { get; private set; }

    public void ApplyAudioTiming(AudioTimingSettings timing) => LastAppliedTiming = timing;

    // Mirrors FakeAudioSink's fake routing: OutputDevices is what enumeration
    // reports, and an id that isn't in it falls back to the system default the
    // way MiniaudioSink does for a device that vanished.
    public IReadOnlyList<AudioOutputDevice> OutputDevices { get; set; } = [];

    public string? OutputDeviceId { get; private set; }

    public IReadOnlyList<AudioOutputDevice> GetOutputDevices() => OutputDevices;

    public void SetOutputDevice(string? deviceId)
    {
        OutputDeviceId = deviceId is null || OutputDevices.Any(d => d.Id == deviceId) ? deviceId : null;
    }

    public event EventHandler? Paused;
    public event EventHandler? Stopped;
    public event EventHandler? Playing;
    public event EventHandler? PositionChanged;
    public event EventHandler? VolumeChanged;
    public event EventHandler? EndReached;
    public event EventHandler<TrackFailedEventArgs>? TrackFailed;

    public void RaisePaused() => Paused?.Invoke(this, EventArgs.Empty);
    public void RaiseStopped() => Stopped?.Invoke(this, EventArgs.Empty);
    public void RaisePlaying() => Playing?.Invoke(this, EventArgs.Empty);
    public void RaisePositionChanged() => PositionChanged?.Invoke(this, EventArgs.Empty);
    public void RaiseVolumeChanged() => VolumeChanged?.Invoke(this, EventArgs.Empty);
    public void RaiseEndReached() => EndReached?.Invoke(this, EventArgs.Empty);
    public void RaiseTrackFailed(Track track) => TrackFailed?.Invoke(this, new TrackFailedEventArgs(track));
}
