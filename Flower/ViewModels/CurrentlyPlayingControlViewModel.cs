using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Flower.Models;

namespace Flower.ViewModels
{
    public class CurrentlyPlayingControlViewModel : ViewModelBase
    {
        private readonly PlaylistControlViewModel _playlistControlViewModel;

        public Track? CurrentlyPlayingTrack => _playlistControlViewModel.CurrentlyPlayingTrack;
        public TimeSpan? ElapsedTime => TimeSpan.FromMinutes(2);
        public TimeSpan? TotalTime => TimeSpan.FromMinutes(3);

        public CurrentlyPlayingControlViewModel(PlaylistControlViewModel playlistControlViewModel)
        {
            _playlistControlViewModel = playlistControlViewModel;
            _playlistControlViewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(_playlistControlViewModel.CurrentlyPlayingTrack))
                {
                    OnPropertyChanged(nameof(CurrentlyPlayingTrack));
                }
            };
        }
    }
}
