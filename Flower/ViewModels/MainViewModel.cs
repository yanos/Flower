using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

using Flower.Models;

namespace Flower.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly PlaylistControlViewModel _playlistControlViewModel;

    public Library Library { get; private set; }

    public Track? SelectedTrack
    {
        get => _playlistControlViewModel.SelectedTrack;
        set => _playlistControlViewModel.SelectedTrack = value;
    }

    public ObservableCollection<Track> Tracks => new (Library.Tracks);

    public MainViewModel() 
    {
        
    }

    public MainViewModel(
        PlaylistControlViewModel playlistControlViewModel, 
        Library library)
    {
        Library = library;
        _playlistControlViewModel = playlistControlViewModel;

        _playlistControlViewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(_playlistControlViewModel.SelectedTrack))
            {
                OnPropertyChanged(nameof(SelectedTrack));
            }
        };
    }
}
