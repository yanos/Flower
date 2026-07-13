using System;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Media.Imaging;

using Flower.Models;
using Flower.Services;

namespace Flower.ViewModels.Mobile;

// One tile in mobile's album grids - both the "Recently Added" grid (grouped
// by (Album, Artist), ordered by recency - see RecentlyAddedAlbumsBuilder) and
// the Albums tab's own grid (grouped by Album name alone to match desktop's
// existing Albums sidebar logic, ordered alphabetically - see AlbumGridBuilder).
// MostRecentlyAdded is the max DateAdded among an album's tracks.
// RepresentativeTrack is whichever of the album's tracks was added most
// recently - its embedded art is what the tile shows.
public sealed class AlbumTileViewModel : ViewModelBase
{
    public required string Name { get; init; }
    public string? Artist { get; init; }
    public required Track RepresentativeTrack { get; init; }
    public DateTimeOffset MostRecentlyAdded { get; init; }

    // Desktop-only for now (multi-select + drag-to-playlist on the Albums
    // grid - see AlbumGridPanel/MainView.axaml.cs) - unused, always false, on
    // mobile, same as TrackRowViewModel.IsSelected is a plain mutable
    // property on a per-rebuild-fresh instance, not something tracked
    // separately by the view.
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
    }

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
