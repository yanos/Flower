using System;

using Avalonia.Threading;

using Microsoft.Extensions.Logging;

using Flower.Audio;
using Flower.ViewModels;

namespace Flower.Services
{
    // Bridges PlaylistControlViewModel/IAudioManager state to whatever
    // IPlatformNowPlaying the platform entry point registered (see
    // PlatformNowPlaying.cs) and routes commands it raises back into the same
    // PlaylistControlViewModel methods the in-app transport controls use.
    // A no-op everywhere PlatformNowPlaying.Current is left null.
    public sealed class NowPlayingIntegrationService : IDisposable
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

            _subscriptions.Add<EventHandler<NowPlayingCommand>>(OnCommandReceived,
                h => _platform.CommandReceived += h, h => _platform.CommandReceived -= h);

            _subscriptions.Add<System.ComponentModel.PropertyChangedEventHandler>((_, e) =>
            {
                if (e.PropertyName == nameof(PlaylistControlViewModel.CurrentlyPlayingTrack))
                    PushMetadata();
            },
                h => _playlistControl.PropertyChanged += h, h => _playlistControl.PropertyChanged -= h);

            _subscriptions.Add<EventHandler>((_, _) => PushPlaybackState(),
                h => _audioManager.Playing += h, h => _audioManager.Playing -= h);
            _subscriptions.Add<EventHandler>((_, _) => PushPlaybackState(),
                h => _audioManager.Paused += h, h => _audioManager.Paused -= h);
            _subscriptions.Add<EventHandler>((_, _) => PushPlaybackState(),
                h => _audioManager.PositionChanged += h, h => _audioManager.PositionChanged -= h);
            _subscriptions.Add<EventHandler>((_, _) => _platform.Clear(),
                h => _audioManager.Stopped += h, h => _audioManager.Stopped -= h);
        }

        // Every event this class attaches to in its constructor, paired with
        // its teardown - see SubscriptionBag, and docs/ARCHITECTURE-REVIEW.md
        // Tier 2.3.
        private readonly SubscriptionBag _subscriptions = new();

        public void Dispose() => _subscriptions.Dispose();

        // Onto the UI thread: a hardware media key or an OS transport control
        // arrives on whichever thread the platform's remote-command centre
        // uses, and everything below it touches observable ViewModel state the
        // view is bound to. It was being called straight off that thread.
        private void OnCommandReceived(object? sender, NowPlayingCommand command)
        {
            _logger.LogDebug("Now Playing command received: {Command}", command);
            Dispatcher.UIThread.Post(() =>
            {
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
            });
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
