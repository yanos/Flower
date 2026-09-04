using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Media.Imaging;

using Flower.Models;
using Flower.Services;
using Flower.ViewModels;

namespace Flower.ViewModels.Mobile;

// Which tile a grid is talking about, for the one thing that has to name a
// single tile rather than a single album: the inline expansion (see
// LibraryBrowserViewModel.ExpandedAlbumKey).
//
// An album name alone will not do it, and that is not a corner case. Recently
// Added groups by (Album, Artist), so a various-artists compilation is one tile
// per contributor - a dozen tiles reading "Virtual Dreams II", differing only
// in the artist underneath. Keyed by name, clicking any one of them expanded
// every one of them at once, each showing the whole compilation.
//
// Artist is part of the key rather than a tiebreaker because it is exactly what
// the grouping that produced the tile split on. The Albums grid, which groups
// by name alone, gives every tile a distinct name anyway, so including the
// artist there costs nothing and keeps one notion of tile identity instead of
// one per grid.
public readonly record struct AlbumTileKey(string Name, string? Artist);

// One tile in mobile's album grids - both the "Recently Added" grid (grouped
// by (Album, Artist), ordered by recency - see RecentlyAddedAlbumsBuilder) and
// the Albums tab's own grid (grouped by Album name alone to match desktop's
// existing Albums sidebar logic, ordered alphabetically - see AlbumGridBuilder).
// MostRecentlyAdded is the max DateAdded among an album's tracks.
// RepresentativeTrack is whichever of the album's tracks was added most
// recently - its embedded art is what the tile shows.
public sealed class AlbumTileViewModel : DownloadIndicatorViewModel
{
    public required string Name { get; init; }
    public string? Artist { get; init; }

    // Settable rather than init-only, and only by ApplyBuilt below: a rebuild
    // reuses the tile already on screen instead of allocating a fresh one (see
    // AlbumTileMerge), so the three things a rebuild can actually change about
    // an album of a given name and artist have to be writable. Same trade
    // TrackRowViewModel.Track makes, for the same reason.
    public required Track RepresentativeTrack { get; set; }
    public DateTimeOffset MostRecentlyAdded { get; set; }

    // What identifies this tile among its grid's tiles - see AlbumTileKey.
    // Derived rather than stored so it cannot drift from the two fields it is
    // made of.
    public AlbumTileKey Key => new(Name, Artist);

    // Every track this tile stands for - carried (as references; the Track
    // instances are the library's own) purely so IsUnavailable below can be
    // recomputed whenever the paired server comes or goes, without rebuilding
    // the grid and throwing away art that is already loaded.
    public required IReadOnlyList<Track> Tracks { get; set; }

    // Takes on what a freshly built tile for the same album says, so the
    // instance the grid is bound to survives the rebuild with its art, its
    // expansion, its selection and above all its in-flight download intact -
    // an album download's own spinner lives here, and a download completing is
    // itself what triggers the rebuild that used to replace this tile
    // mid-flight. Name and Artist are not copied because they are the key the
    // reuse matched on.
    internal void ApplyBuilt(AlbumTileViewModel built)
    {
        if (!ReferenceEquals(RepresentativeTrack, built.RepresentativeTrack))
        {
            var previous = RepresentativeTrack;
            RepresentativeTrack = built.RepresentativeTrack;
            OnPropertyChanged(nameof(RepresentativeTrack));

            // The point of reuse is that the decoded bitmap survives; it only
            // stops being the right image when what AlbumArtLoader keys on
            // changed - see TrackRowViewModel.ArtSourceMatches.
            if (!TrackRowViewModel.ArtSourceMatches(previous, built.RepresentativeTrack))
                ResetAlbumArt();
        }

        if (MostRecentlyAdded != built.MostRecentlyAdded)
        {
            MostRecentlyAdded = built.MostRecentlyAdded;
            OnPropertyChanged(nameof(MostRecentlyAdded));
        }

        Tracks = built.Tracks;
    }

    // Greyed-out state for the tile: true only when *none* of Tracks can be
    // played right now - see TrackAvailability.IsAlbumUnavailable for why one
    // downloaded track is enough to keep a mostly-not-downloaded album at full
    // strength. Set by TrackAvailability.Apply, both at build time and again
    // on every reachability change; false by default, so a tile never flashes
    // as unavailable before it is known to be.
    private bool _isUnavailable;
    public bool IsUnavailable
    {
        get => _isUnavailable;
        set { if (_isUnavailable != value) { _isUnavailable = value; OnPropertyChanged(); } }
    }

    // Desktop-only for now (multi-select + drag-to-playlist on the Albums/
    // Recently Added grids - see AlbumGridView/MainView.axaml.cs) - unused,
    // always false, on mobile, same as TrackRowViewModel.IsSelected is a
    // plain mutable property on a per-rebuild-fresh instance, not something
    // tracked separately by the view.
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
    }

    // Desktop-only - drives AlbumTileControl's expanded-state visual cue.
    // The actual expanded content (that album's tracks) lives on the
    // AlbumGridRowViewModel containing this tile, not here - see
    // AlbumGridView.ApplyExpansion.
    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded != value) { _isExpanded = value; OnPropertyChanged(); } }
    }

    // Same lazy-load-on-first-bind pattern as TrackRowViewModel.AlbumArt, kept
    // as a separate small copy rather than a shared base: the concurrency-safe
    // gate (Interlocked + one bool field) is only a few lines, and the two
    // types differ enough (a Track row vs. an album tile) that sharing state
    // through a common base would add more indirection than it would save.
    private Bitmap? _albumArt;
    private int _artState; // 0=idle, 1=loading, 2=done
    private int _artCacheGeneration;
    // Bumped by ResetAlbumArt so a load already in flight for the previous
    // representative track can tell it has been superseded and drop its
    // result - same guard, and same reason, as TrackRowViewModel's.
    private int _artGeneration;

    public Bitmap? AlbumArt
    {
        get
        {
            // Same artwork-replaced-on-disk re-read as TrackRowViewModel's own
            // getter - see its comment for why this polls a generation counter
            // instead of subscribing to the loader.
            if (Volatile.Read(ref _artState) == 2 && Volatile.Read(ref _artCacheGeneration) != AlbumArtLoader.CacheGeneration)
                Interlocked.Exchange(ref _artState, 0);

            if (Interlocked.CompareExchange(ref _artState, 1, 0) == 0)
                _ = LoadArtAsync();
            return _albumArt;
        }
        private set { _albumArt = value; OnPropertyChanged(); }
    }

    // Back to idle rather than straight to a reload, like the row's: nothing
    // may ever read AlbumArt on this tile again, and the getter is what
    // decides that.
    private void ResetAlbumArt()
    {
        Interlocked.Increment(ref _artGeneration);
        Interlocked.Exchange(ref _artState, 0);
        AlbumArt = null;
    }

    private async Task LoadArtAsync()
    {
        var generation = Volatile.Read(ref _artGeneration);
        var cacheGeneration = AlbumArtLoader.CacheGeneration;
        var bmp = await AlbumArtLoader.Current.LoadAsync(RepresentativeTrack);
        if (Volatile.Read(ref _artGeneration) != generation)
            return;
        Volatile.Write(ref _artCacheGeneration, cacheGeneration);
        Interlocked.Exchange(ref _artState, 2);
        AlbumArt = bmp;
    }
}
