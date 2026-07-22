using System;

using Microsoft.Extensions.Logging;

using Flower.Manager;
using Flower.ViewModels;

namespace Flower.Services
{
    // Bridges PlaylistControlViewModel/IAudioManager state to whatever
    // IPlatformNowPlaying the platform entry point registered (see
    // PlatformNowPlaying.cs) and routes commands it raises back into the same
    // PlaylistControlViewModel methods the in-app transport controls use.
    // A no-op everywhere PlatformNowPlaying.Current is left null.
    public sealed class NowPlayingIntegrationService
    {
        private readonly PlaylistControlViewModel _playlistControl;
        private readonly IAudioManager _audioManager;
        private readonly ILogger<NowPlayingIntegrationService> _logger;
        private readonly IPlatformNowPlaying? _platform;

        public NowPlayingIntegrationService(
            PlaylistControlViewModel playlistControl,
            IAudioManager audioManager,
            ILogger<NowPlayingIntegrationService> logger)
        {
            _playlistControl = playlistControl;
            _audioManager = audioManager;
            _logger = logger;
            _platform = PlatformNowPlaying.Current;

            if (_platform == null)
                return;

            _platform.CommandReceived += OnCommandReceived;

            _playlistControl.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PlaylistControlViewModel.CurrentlyPlayingTrack))
                    PushMetadata();
            };

            _audioManager.Playing += (_, _) => PushPlaybackState();
            _audioManager.Paused += (_, _) => PushPlaybackState();
            _audioManager.PositionChanged += (_, _) => PushPlaybackState();
            _audioManager.Stopped += (_, _) => _platform.Clear();
        }

        private void OnCommandReceived(object? sender, NowPlayingCommand command)
        {
            _logger.LogDebug("Now Playing command received: {Command}", command);
            switch (command)
            {
                case NowPlayingCommand.PlayPause:
                    _playlistControl.PlayOrPause();
                    break;
                case NowPlayingCommand.Next:
                    _playlistControl.Next();
                    break;
                case NowPlayingCommand.Previous:
                    _playlistControl.Previous();
                    break;
            }
        }

        private void PushMetadata()
        {
            if (_platform == null)
                return;

            var track = _playlistControl.CurrentlyPlayingTrack;
            if (track == null)
            {
                _platform.Clear();
                return;
            }

            byte[]? artwork = null;
            try
            {
                artwork = AlbumArtLoader.TryGetLocalArtBytes(track);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not load art for now-playing metadata ({Path})", track.Path);
            }

            _platform.UpdateMetadata(new NowPlayingMetadata
            {
                Title = track.Title,
                Artist = track.Artists,
                Album = track.Album,
                Duration = track.Duration,
                ArtworkData = artwork
            });

            PushPlaybackState();
        }

        private void PushPlaybackState()
        {
            if (_platform == null || _playlistControl.CurrentlyPlayingTrack == null)
                return;

            var elapsed = TimeSpan.FromMilliseconds(_audioManager.Time);
            _platform.UpdatePlaybackState(_playlistControl.IsPlaying, elapsed);
        }
    }
}
