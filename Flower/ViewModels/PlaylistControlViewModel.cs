using System.Linq;

using Avalonia.Threading;

using Flower.Manager;
using Flower.Models;

namespace Flower.ViewModels
{
    public class PlaylistControlViewModel : ViewModelBase
    {
        private Playlist _currentPlaylist;
        private Track? _currentlyPlayingTrack;
        private Track? _selectedTrack;

        private IAudioManager _audioManager { get; }
        
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
            MainPlaylist playlist)
        {
            _audioManager = audioManager;
            _currentPlaylist = playlist;

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

            _audioManager.EndReached += (s, e) =>
            {
                if (CurrentlyPlayingTrack != null)
                {
                    var nextTrack = _currentPlaylist.GetNextTrack(CurrentlyPlayingTrack);
                    if (nextTrack != null)
                    {
                        Dispatcher.UIThread.Post(() => Play(nextTrack));
                    }
                }
            };
        }

        public void SetCurrentPlaylist(Playlist playlist)
        {
            _currentPlaylist = playlist;
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
                var nextTrack = _currentPlaylist.GetNextTrack(CurrentlyPlayingTrack);
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
