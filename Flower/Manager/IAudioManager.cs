using System;
using System.Collections.Generic;

using Flower.Models;

namespace Flower.Manager
{
    // Raised instead of EndReached when a track stops because it could not be
    // decoded rather than because it finished - see IAudioManager.TrackFailed.
    public sealed class TrackFailedEventArgs(Track track) : EventArgs
    {
        public Track Track { get; } = track;
    }

    public interface IAudioManager
    {
        bool IsPlaying { get; }
        //bool CanResume { get; }

        int Volume { get; set; }

        // Percentage points added to Volume on the way to the output, for a
        // track whose own VolumeAdjustment says it should play louder or
        // quieter than everything around it (see Track.VolumeAdjustment). The
        // sum, clamped to 0..100, is what the sink is driven with.
        //
        // A separate knob rather than "set Volume and put it back afterwards"
        // because Volume is the user's setting and the slider shows it: an
        // offset must move the sound without moving the slider, and must not
        // fight a drag that happens mid-track (the drag sets Volume, the offset
        // still applies on top of the new value). Changing it raises no
        // VolumeChanged for the same reason - nothing user-visible changed.
        int VolumeOffset { get; set; }
        float Position { get; set; }
        long Time { get; }
        long Length { get; }

        // immediate distinguishes a user gesture (Next/Previous/activating a
        // row) from the queue advancing on its own. A gapless implementation
        // uses it to decide what happens to the audio the outgoing track
        // still has buffered but unplayed: a manual skip cuts it off (with a
        // fade, so the cut is inaudible), an auto-advance lets it finish
        // first. See GaplessCoordinator.Play.
        void Play(Track track, bool immediate = true);

        // Tells the manager what should play after the current track, so a
        // gapless implementation can decode it ahead of time. Called by
        // PlaylistControlViewModel right after Play() and after
        // ToggleRepeat()/ToggleShuffle() change what "next" would resolve
        // to. null means nothing should follow (e.g. end of playlist).
        void SetUpcoming(Track? next);

        // Hands the render path its latency/declick tuning - see
        // AudioTimingSettings. Applied at startup from AppSettings, the same
        // way the equalizer is.
        void ApplyAudioTiming(AudioTimingSettings timing);

        void Resume();
        void Pause();
        void Stop();

        // Applies (or, passing null, true-bypasses) the EQ - see
        // IAudioSink.ApplyEqualizer for exact bypass semantics. Every change
        // rebuilds and swaps the whole processor; there is no partial-update
        // path.
        void ApplyEqualizer(Equalizer? equalizer);

        // Output routing, forwarded straight to the underlying IAudioSink -
        // see IAudioSink.GetOutputDevices/OutputDeviceId/SetOutputDevice for
        // what the Ids mean and what a swap costs. Managers with no device
        // concept (WebAudioManager, whose <audio> element follows the
        // browser's own routing) report no devices and ignore the setter.
        IReadOnlyList<AudioOutputDevice> GetOutputDevices();
        string? OutputDeviceId { get; }
        void SetOutputDevice(string? deviceId);

        public event EventHandler? Paused;
        public event EventHandler? Stopped;
        public event EventHandler? Playing;
        public event EventHandler? PositionChanged;
        public event EventHandler? VolumeChanged;
        public event EventHandler? EndReached;

        // The track stopped because decoding failed (corrupt file, missing
        // codec, unreadable path), not because it played to the end. Raised
        // *instead of* EndReached for that track: they used to be the same
        // event, so a file that couldn't be decoded was indistinguishable from
        // one the user had listened all the way through - it silently skipped
        // and picked up a play count on the way past. Subscribers should
        // advance the queue but must not count a play. See
        // docs/AUDIOPHILE-PLAN.md's DSD/APE section, which needs this same seam
        // for its "unsupported format" messaging.
        public event EventHandler<TrackFailedEventArgs>? TrackFailed;
    }
}