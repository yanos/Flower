using System;
using System.Threading.Tasks;

using Avalonia.Threading;

using Flower.Models;

using Microsoft.Extensions.Logging;

using Flower.Audio;
using Flower.Logging;
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
        private readonly AlbumArtLoader _artLoader;
        private readonly ILogger<NowPlayingIntegrationService> _logger;
        private readonly IPlatformNowPlaying? _platform;

        public NowPlayingIntegrationService(
            PlaylistControlViewModel playlistControl,
            IAudioManager audioManager,
            AlbumArtLoader artLoader,
            ILogger<NowPlayingIntegrationService> logger)
        {
            _playlistControl = playlistControl;
            _audioManager = audioManager;
            _artLoader = artLoader;
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

            // Whatever art is already on this device: the file's own for a
            // song that is here, the disk cache for one streamed from the
            // server. It used to be the file's alone, so a streamed song - no
            // file, and a stream URL for a Path - showed a blank card.
            var artwork = ArtOnThisDevice(track);
            PublishMetadata(track, artwork);

            // A streamed song whose album has never been on screen has nothing
            // in the cache yet. Fetch it the way a tile would, and fill the
            // card in if the song is still the one playing when it arrives.
            if (artwork == null && !AlbumArtLoader.IsLocalFile(track))
                FillInRemoteArtAsync(track).Forget(_logger, "Now-playing art fetch");
        }

        private async Task FillInRemoteArtAsync(Track track)
        {
            if (await _artLoader.LoadAsync(track) == null)
                return;

            Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(_playlistControl.CurrentlyPlayingTrack, track))
                    return;
                if (ArtOnThisDevice(track) is { } artwork)
                    PublishMetadata(track, artwork);
            });
        }

        private byte[]? ArtOnThisDevice(Track track)
        {
            try
            {
                return AlbumArtLoader.TryGetArt(track)?.Bytes;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not load art for now-playing metadata ({Path})", LogPath.Short(track.Path));
                return null;
            }
        }

        private void PublishMetadata(Track track, byte[]? artwork)
        {
            _platform!.UpdateMetadata(new NowPlayingMetadata
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
