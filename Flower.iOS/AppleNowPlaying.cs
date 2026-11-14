using System;

using CoreGraphics;

using Foundation;

using MediaPlayer;

#if __MACOS__
using AppKit;
#else
using UIKit;
#endif

using Flower.Services;

namespace Flower.Apple;

// The Apple half of docs/MEDIA-KEYS-PLAN.md, compiled into both Apple heads:
// Flower.iOS owns this file and Flower.MacOS links it, since MediaPlayer is
// the same framework on both and only the image type differs. Publishes the
// current track to MPNowPlayingInfoCenter (the Lock Screen and Control
// Center card on iOS; Control Center's Now Playing module and the menu bar
// on macOS) and registers MPRemoteCommandCenter handlers, so the card's own
// buttons, AirPods, Bluetooth head units and, on a Mac, the keyboard's media
// keys drive playback back into the app.
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
#if __MACOS__
            var image = new NSImage(data);
            if (image.IsValid)
                info.Artwork = new MPMediaItemArtwork(image.Size, _ => image);
#else
            var image = UIImage.LoadFromData(data);
            if (image != null)
                info.Artwork = new MPMediaItemArtwork(image.Size, _ => image);
#endif
        }

        MPNowPlayingInfoCenter.DefaultCenter.NowPlaying = info;
    }

    // MPNowPlayingInfo.PlaybackRate (0.0 = paused, 1.0 = playing) is how iOS
    // learns the play/pause state; it infers the rest from the app's active
    // audio session. macOS has no audio session to infer from, so it also
    // needs PlaybackState set outright: without it, Control Center shows the
    // card but never treats Flower as the app playing, and the keyboard's
    // play key goes to whatever app it last went to instead.
    public void UpdatePlaybackState(bool isPlaying, TimeSpan elapsed)
    {
        var center = MPNowPlayingInfoCenter.DefaultCenter;
#if __MACOS__
        center.PlaybackState = isPlaying ? MPNowPlayingPlaybackState.Playing : MPNowPlayingPlaybackState.Paused;
#endif
        var info = center.NowPlaying;
        if (info == null)
            return;

        info.ElapsedPlaybackTime = elapsed.TotalSeconds;
        info.PlaybackRate = isPlaying ? 1.0 : 0.0;
        center.NowPlaying = info;
    }

    public void Clear()
    {
        // Apple's documented way to clear the card is to set NowPlaying to
        // nil - the .NET binding just doesn't mark the setter nullable.
        MPNowPlayingInfoCenter.DefaultCenter.NowPlaying = null!;
#if __MACOS__
        MPNowPlayingInfoCenter.DefaultCenter.PlaybackState = MPNowPlayingPlaybackState.Stopped;
#endif
    }
}
