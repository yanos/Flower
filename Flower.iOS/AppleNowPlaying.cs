using System;

using CoreGraphics;

using Foundation;

using MediaPlayer;

using UIKit;

using Flower.Services;

namespace Flower.iOS;

// iOS's half of docs/MEDIA-KEYS-PLAN.md Phase 2: publishes the currently
// playing track to MPNowPlayingInfoCenter (Lock Screen + Control Center
// "Now Playing" card) and registers MPRemoteCommandCenter handlers so the
// card's own transport buttons, AirPods, and Bluetooth head units can drive
// playback back into the app.
public sealed class AppleNowPlaying : IPlatformNowPlaying
{
    public event EventHandler<NowPlayingCommand>? CommandReceived;

    public AppleNowPlaying()
    {
        var commandCenter = MPRemoteCommandCenter.Shared;

        commandCenter.PlayCommand.Enabled = true;
        commandCenter.PlayCommand.AddTarget(HandlePlayPause);

        commandCenter.PauseCommand.Enabled = true;
        commandCenter.PauseCommand.AddTarget(HandlePlayPause);

        commandCenter.TogglePlayPauseCommand.Enabled = true;
        commandCenter.TogglePlayPauseCommand.AddTarget(HandlePlayPause);

        commandCenter.NextTrackCommand.Enabled = true;
        commandCenter.NextTrackCommand.AddTarget(HandleNext);

        commandCenter.PreviousTrackCommand.Enabled = true;
        commandCenter.PreviousTrackCommand.AddTarget(HandlePrevious);
    }

    private MPRemoteCommandHandlerStatus HandlePlayPause(MPRemoteCommandEvent e)
    {
        CommandReceived?.Invoke(this, NowPlayingCommand.PlayPause);
        return MPRemoteCommandHandlerStatus.Success;
    }

    private MPRemoteCommandHandlerStatus HandleNext(MPRemoteCommandEvent e)
    {
        CommandReceived?.Invoke(this, NowPlayingCommand.Next);
        return MPRemoteCommandHandlerStatus.Success;
    }

    private MPRemoteCommandHandlerStatus HandlePrevious(MPRemoteCommandEvent e)
    {
        CommandReceived?.Invoke(this, NowPlayingCommand.Previous);
        return MPRemoteCommandHandlerStatus.Success;
    }

    public void UpdateMetadata(NowPlayingMetadata metadata)
    {
        var info = new MPNowPlayingInfo
        {
            Title = metadata.Title ?? string.Empty,
            Artist = metadata.Artist,
            AlbumTitle = metadata.Album,
            PlaybackDuration = metadata.Duration.TotalSeconds
        };

        if (metadata.ArtworkData is { Length: > 0 } bytes)
        {
            using var data = NSData.FromArray(bytes);
            var image = UIImage.LoadFromData(data);
            if (image != null)
                info.Artwork = new MPMediaItemArtwork(image.Size, _ => image);
        }

        MPNowPlayingInfoCenter.DefaultCenter.NowPlaying = info;
    }

    // No separate MPNowPlayingInfoCenter.PlaybackState in this SDK binding -
    // MPNowPlayingInfo.PlaybackRate (0.0 = paused, 1.0 = playing) is Apple's
    // own documented way to convey play/pause state to the Lock Screen/
    // Control Center card.
    public void UpdatePlaybackState(bool isPlaying, TimeSpan elapsed)
    {
        var center = MPNowPlayingInfoCenter.DefaultCenter;
        var info = center.NowPlaying;
        if (info == null)
            return;

        info.ElapsedPlaybackTime = elapsed.TotalSeconds;
        info.PlaybackRate = isPlaying ? 1.0 : 0.0;
        center.NowPlaying = info;
    }

    public void Clear()
    {
        MPNowPlayingInfoCenter.DefaultCenter.NowPlaying = null;
    }
}
