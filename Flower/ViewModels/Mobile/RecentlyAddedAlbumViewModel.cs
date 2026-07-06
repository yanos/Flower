using System;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Media.Imaging;

using Flower.Models;
using Flower.Services;

namespace Flower.ViewModels.Mobile;

// One tile in mobile's "Recently Added" album grid (the app's default view -
// see MobileMainViewModel). Groups this device's tracks by (Album, Artist);
// MostRecentlyAdded is the max DateAdded among them, which is what the grid is
// ordered by. RepresentativeTrack is whichever of the album's tracks was added
// most recently - its embedded art is what the tile shows.
public sealed class RecentlyAddedAlbumViewModel : ViewModelBase
{
    public required string Name { get; init; }
    public string? Artist { get; init; }
    public required Track RepresentativeTrack { get; init; }
    public DateTimeOffset MostRecentlyAdded { get; init; }

    // Same lazy-load-on-first-bind pattern as TrackRowViewModel.AlbumArt, kept
    // as a separate small copy rather than a shared base: the concurrency-safe
    // gate (Interlocked + one bool field) is only a few lines, and the two
    // types differ enough (a Track row vs. an album tile) that sharing state
    // through a common base would add more indirection than it would save.
    private Bitmap? _albumArt;
    private int _artState; // 0=idle, 1=loading, 2=done

    public Bitmap? AlbumArt
    {
        get
        {
            if (Interlocked.CompareExchange(ref _artState, 1, 0) == 0)
                _ = LoadArtAsync();
            return _albumArt;
        }
        private set { _albumArt = value; OnPropertyChanged(); }
    }

    private async Task LoadArtAsync()
    {
        var bmp = await AlbumArtLoader.LoadAsync(RepresentativeTrack);
        Interlocked.Exchange(ref _artState, 2);
        AlbumArt = bmp;
    }
}
