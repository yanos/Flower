using System;
using System.Linq;

using Avalonia.Threading;

using Microsoft.Extensions.Logging;

using Flower.Manager;
using Flower.Models;
using Flower.Persistence;

namespace Flower.ViewModels
{
    public class PlaylistControlViewModel : ViewModelBase
    {
        private readonly ILogger<PlaylistControlViewModel> _logger;

        private Playlist _currentPlaylist;
        private Track? _currentlyPlayingTrack;
        private Track? _selectedTrack;
        private bool _isRepeatEnabled;
        private bool _isShuffleEnabled;
        private readonly Random _random = new();
        private readonly Library _library;
        private readonly AppSettings _appSettings;
        private readonly LibraryStore _libraryStore;
        private readonly AppSettingsStore _appSettingsStore;

        private IAudioManager _audioManager { get; }

        // Loops the currently playing track instead of advancing when it ends.
        // Only applies to natural end-of-track auto-advance; manual Next()/Previous() still move.
        public bool IsRepeatEnabled
        {
            get => _isRepeatEnabled;
            set
            {
                _isRepeatEnabled = value;
                OnPropertyChanged();
            }
        }

        // Picks a random track (instead of the next one in order) whenever the
        // queue advances, whether that's auto-advance on end-of-track or a manual Next().
        public bool IsShuffleEnabled
        {
            get => _isShuffleEnabled;
            set
            {
                _isShuffleEnabled = value;
                OnPropertyChanged();
            }
        }

        public Track? SelectedTrack
        { 
            get => _selectedTrack;
            set
            { 
                _selectedTrack = value;
                OnPropertyChanged();
            }
        }

        public Track? CurrentlyPlayingTrack 
        { 
            get => _currentlyPlayingTrack;
            private set
            {
                _currentlyPlayingTrack = value;
                OnPropertyChanged();
            }
        }

        public bool IsPlaying => _audioManager.IsPlaying;

        public bool CanResume => CurrentlyPlayingTrack != null;

        public PlaylistControlViewModel(
            IAudioManager audioManager,
            MainPlaylist playlist,
            Library library,
            AppSettings appSettings,
            LibraryStore libraryStore,
            AppSettingsStore appSettingsStore,
            ILogger<PlaylistControlViewModel> logger)
        {
            _audioManager = audioManager;
            _currentPlaylist = playlist;
            _library = library;
            _appSettings = appSettings;
            _libraryStore = libraryStore;
            _appSettingsStore = appSettingsStore;
            _logger = logger;
            _isRepeatEnabled = appSettings.IsRepeatEnabled;
            _isShuffleEnabled = appSettings.IsShuffleEnabled;

            _audioManager.Playing += (s, e) =>
            {
                OnPropertyChanged(nameof(IsPlaying));
            };

            _audioManager.Stopped += (s, e) =>
            {
                OnPropertyChanged(nameof(IsPlaying));
                CurrentlyPlayingTrack = null;
            };

            _audioManager.Paused += (s, e) =>
            {
                OnPropertyChanged(nameof(IsPlaying));
            };

            _audioManager.EndReached += async (s, e) =>
            {
                if (CurrentlyPlayingTrack != null)
                {
                    var finishedTrack = CurrentlyPlayingTrack;
                    _logger.LogDebug("EndReached: {Title} ({Path})", finishedTrack.Title, finishedTrack.Path);

                    // finishedTrack can be a stale reference: every launch kicks off a
                    // background rescan (see App.axaml.cs) that replaces _library.Tracks
                    // wholesale with brand-new Track instances, even for files that didn't
                    // change. If that rescan lands while this track is still playing (easily
                    // enough time if the user alt-tabs away for a bit - confirmed via a real
                    // repro), CurrentlyPlayingTrack still points at the old, now-orphaned
                    // object. IncrementPlayCount resolves the current object and applies the
                    // increment atomically under Library's own lock, so a rescan racing on
                    // another thread (EndReached fires on a LibVLC callback thread, the
                    // rescan runs on a threadpool thread - see Library._lock) can't land
                    // between "resolve" and "increment" and silently discard it the way a
                    // plain find-then-increment here already proved it could.
                    _library.IncrementPlayCount(finishedTrack);
                    // NotifyTrackChanged, not UpdateTracks(_library.Tracks) - see
                    // MainViewModel.SyncITunesPlayCountAsync's comment on why
                    // passing Tracks back into UpdateTracks as a "fresh scan"
                    // silently doubles every placeholder track.
                    _library.NotifyTrackChanged();
                    await _libraryStore.SaveAsync(_library.Tracks);

                    var nextTrack = IsRepeatEnabled ? finishedTrack : GetNextTrack(finishedTrack);
                    if (nextTrack != null)
                    {
                        _logger.LogDebug("Auto-advancing to {Title} (repeat={Repeat}, shuffle={Shuffle})", nextTrack.Title, IsRepeatEnabled, IsShuffleEnabled);
                        Dispatcher.UIThread.Post(() => Play(nextTrack));
                    }
                }
            };
        }

        public void SetCurrentPlaylist(Playlist playlist)
        {
            _currentPlaylist = playlist;
        }

        public void ToggleRepeat()
        {
            IsRepeatEnabled = !IsRepeatEnabled;
            _appSettings.IsRepeatEnabled = IsRepeatEnabled;
            _ = _appSettingsStore.SaveAsync(_appSettings);
        }

        public void ToggleShuffle()
        {
            IsShuffleEnabled = !IsShuffleEnabled;
            _appSettings.IsShuffleEnabled = IsShuffleEnabled;
            _ = _appSettingsStore.SaveAsync(_appSettings);
        }

        private Track? GetNextTrack(Track currentTrack)
        {
            if (IsShuffleEnabled && _currentPlaylist.Tracks.Count > 1)
            {
                Track candidate;
                do
                {
                    candidate = _currentPlaylist.Tracks[_random.Next(_currentPlaylist.Tracks.Count)];
                } while (candidate == currentTrack);
                return candidate;
            }

            return _currentPlaylist.GetNextTrack(currentTrack);
        }

        public void PlayOrPause()
        {
            var trackToPlay = SelectedTrack ?? _currentPlaylist.Tracks.FirstOrDefault();

            if (trackToPlay != null)
            {
                PlayOrPause(trackToPlay);
            }
        }

        public void Play(Track track)
        {
            _logger.LogInformation("Playing {Title} by {Artist} ({Path})", track.Title, track.Artists, track.Path);
            SelectedTrack = track;
            CurrentlyPlayingTrack = track;
            _audioManager.Play(track);
        }

        public void PlayOrPause(Track track)
        {
            if (_audioManager.IsPlaying)
            {
                _audioManager.Pause();
            }
            else
            {
                if (CanResume)
                {
                    _audioManager.Resume();
                }
                else
                {
                    Play(track);
                }
            }
        }

        public void Next()
        {
            if (CurrentlyPlayingTrack != null)
            {
                var nextTrack = GetNextTrack(CurrentlyPlayingTrack);
                if (nextTrack != null)
                {
                    Play(nextTrack);
                }
            }
        }

        public void Previous()
        {
            if (CurrentlyPlayingTrack != null)
            {
                var previousTrack = _currentPlaylist.GetPreviousTrack(CurrentlyPlayingTrack);
                if (previousTrack != null)
                {
                    Play(previousTrack);
                }
            }
        }
    }
}
