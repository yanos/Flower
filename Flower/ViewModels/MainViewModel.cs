using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

using Flower.Models;

namespace Flower.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    public Library Library { get; private set; }
    public ObservableCollection<Track> Tracks => new (Library.Tracks);

    public MainViewModel() { }

    public MainViewModel(Library library)
    {
        Library = library;
    }

    public string Greeting => Library.Tracks.FirstOrDefault().Name;


}
