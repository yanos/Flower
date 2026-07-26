using System;

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
    public float Position { get; set; }
    public long Time { get; set; }
    public long Length { get; set; }

    public Track? LastPlayed { get; private set; }
    public Track? LastUpcoming { get; private set; }

    public void Play(Track track) => LastPlayed = track;
    public void SetUpcoming(Track? next) => LastUpcoming = next;
    public void Resume() { }
    public void Pause() { }
    public void Stop() { }

    public event EventHandler? Paused;
    public event EventHandler? Stopped;
    public event EventHandler? Playing;
    public event EventHandler? PositionChanged;
    public event EventHandler? VolumeChanged;
    public event EventHandler? EndReached;

    public void RaisePaused() => Paused?.Invoke(this, EventArgs.Empty);
    public void RaiseStopped() => Stopped?.Invoke(this, EventArgs.Empty);
    public void RaisePlaying() => Playing?.Invoke(this, EventArgs.Empty);
    public void RaisePositionChanged() => PositionChanged?.Invoke(this, EventArgs.Empty);
    public void RaiseVolumeChanged() => VolumeChanged?.Invoke(this, EventArgs.Empty);
    public void RaiseEndReached() => EndReached?.Invoke(this, EventArgs.Empty);
}
