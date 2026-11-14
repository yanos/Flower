using System;

namespace Flower.Services
{
    // Transport commands the OS's lock-screen/Control Center now-playing card
    // can send back to the app. Play/Pause/TogglePlayPause all collapse into
    // one PlayPause case since PlaylistControlViewModel only exposes a single
    // toggling PlayOrPause() - there is no separate no-arg Play()/Pause().
    public enum NowPlayingCommand
    {
        PlayPause,
        Next,
        Previous
    }

    public sealed class NowPlayingMetadata
    {
        public required string? Title { get; init; }
        public required string? Artist { get; init; }
        public required string? Album { get; init; }
        public required TimeSpan Duration { get; init; }
        public byte[]? ArtworkData { get; init; }
    }

    // Publishes now-playing metadata/state to the OS's lock-screen/Control
    // Center now-playing surface and receives transport commands back from it
    // (headset buttons, lock screen tap, Control Center tap, car head unit).
    // See docs/MEDIA-KEYS-PLAN.md.
    public interface IPlatformNowPlaying
    {
        event EventHandler<NowPlayingCommand>? CommandReceived;

        void UpdateMetadata(NowPlayingMetadata metadata);
        void UpdatePlaybackState(bool isPlaying, TimeSpan elapsed);
        void Clear();
    }

    // Set by the platform entry point before Avalonia starts - same
    // timing/convention as PlatformMdns.Current: AppleNowPlaying by Flower.iOS
    // and Flower.MacOS, WindowsNowPlaying or MprisNowPlaying by Flower.Desktop
    // on Windows and Linux. Left null on Android, in a browser tab and in
    // Flower.Desktop on a Mac, where NowPlayingIntegrationService just does
    // nothing.
    public static class PlatformNowPlaying
    {
        public static IPlatformNowPlaying? Current { get; set; }
    }
}
