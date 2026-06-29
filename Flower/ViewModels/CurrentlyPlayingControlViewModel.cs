using System;

using Avalonia.Threading;

using Flower.Manager;
using Flower.Models;

namespace Flower.ViewModels
{
    public class CurrentlyPlayingControlViewModel : ViewModelBase
    {
        private readonly PlaylistControlViewModel _playlistControlViewModel;
        private readonly IAudioManager _audioManager;

        private double _seekPosition;
        private bool _isUpdatingFromAudio;

        public Track? CurrentlyPlayingTrack => _playlistControlViewModel.CurrentlyPlayingTrack;

        public double SeekPosition
        {
            get => _seekPosition;
            set
            {
                _seekPosition = value;
                OnPropertyChanged();
                if (!_isUpdatingFromAudio && _audioManager.IsPlaying)
                    _audioManager.Position = (float)value;
            }
        }

        public string? ElapsedTime => _audioManager.Time > 0
            ? FormatDuration(TimeSpan.FromMilliseconds(_audioManager.Time))
            : null;

        public string? TotalTime
        {
            get
            {
                var track = CurrentlyPlayingTrack;
                TimeSpan ts;
                if (track != null && track.Duration > TimeSpan.Zero)
                    ts = track.Duration;
                else if (_audioManager.Length > 0)
                    ts = TimeSpan.FromMilliseconds(_audioManager.Length);
                else
                    return null;
                return FormatDuration(ts);
            }
        }

        private static string FormatDuration(TimeSpan ts)
            => (int)ts.TotalHours > 0 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");

        public CurrentlyPlayingControlViewModel(
            PlaylistControlViewModel playlistControlViewModel,
            IAudioManager audioManager)
        {
            _playlistControlViewModel = playlistControlViewModel;
            _audioManager = audioManager;

            _playlistControlViewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(_playlistControlViewModel.CurrentlyPlayingTrack))
                {
                    OnPropertyChanged(nameof(CurrentlyPlayingTrack));
                    OnPropertyChanged(nameof(TotalTime));
                }
            };

            _audioManager.PositionChanged += (s, e) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _isUpdatingFromAudio = true;
                    SeekPosition = _audioManager.Position;
                    _isUpdatingFromAudio = false;
                    OnPropertyChanged(nameof(ElapsedTime));
                    OnPropertyChanged(nameof(TotalTime));
                });
            };

            _audioManager.Stopped += (s, e) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _isUpdatingFromAudio = true;
                    SeekPosition = 0;
                    _isUpdatingFromAudio = false;
                    OnPropertyChanged(nameof(ElapsedTime));
                    OnPropertyChanged(nameof(TotalTime));
                });
            };
        }
    }
}
