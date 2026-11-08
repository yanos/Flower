using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Media.Imaging;

using Flower.Models;
using Flower.Services;

namespace Flower.ViewModels;

// One row in an expanded album's two-column track list (AlbumGridRowViewModel.
// Column1Tracks/Column2Tracks) - wraps the raw Track with the click-to-select
// state MusicListView's own rows already have (TrackRowViewModel.IsSelected),
// which a bare Track binding had no room for. Only one album is ever expanded
// at a time (see MainViewModel.ExpandedAlbumName), so selection only needs to
// be tracked within whichever one AlbumGridRowViewModel currently owns these -
// see AlbumGridRowViewModel.SelectTrack.
public sealed class ExpandedTrackRowViewModel : DownloadIndicatorViewModel, AlbumArtLoader.IShowsAlbumArt
{
    public required Track Track { get; init; }

    // "Disc 2" over the first song of each disc, in an album of more than one
    // (DiscHeaders); null on every other row.
    public string? DiscHeader { get; init; }

    // "Artist — Album", under the title where a list holds songs from more
    // than one album (AlbumTrackList.ShowsArtist).
    public string ArtistAndAlbumText =>
        string.Join(" \u2014 ", new[] { Track.Artists, Track.Album }.Where(s => !string.IsNullOrEmpty(s)));

    // The song's cover at the left of the row, where the list holds songs
    // from more than one album (AlbumTracksViewModel.ShowsArt) - one album's
    // own list has its cover beside it already.
    public bool ShowsArt { get; init; }

    // Loaded on first read, as TrackRowViewModel.AlbumArt is, and never for a
    // row that does not show it. Track is fixed for the row's life, so unlike
    // there nothing can make a load stale but the cover itself being replaced
    // (AlbumArtLoader.CacheGeneration).
    private Bitmap? _albumArt;
    private int     _artState; // 0=idle, 1=loading, 2=done
    private int     _artCacheGeneration;

    public Bitmap? AlbumArt
    {
        get
        {
            if (!ShowsArt)
                return null;
            if (Volatile.Read(ref _artState) == 2 && Volatile.Read(ref _artCacheGeneration) != AlbumArtLoader.CacheGeneration)
                Interlocked.Exchange(ref _artState, 0);
            if (Interlocked.CompareExchange(ref _artState, 1, 0) == 0)
                _ = LoadArtAsync();
            return _albumArt;
        }
        private set { _albumArt = value; OnPropertyChanged(); }
    }

    private async Task LoadArtAsync()
    {
        var cacheGeneration = AlbumArtLoader.CacheGeneration;
        var bmp = await AlbumArtLoader.Current.LoadThumbnailAsync(Track);
        Volatile.Write(ref _artCacheGeneration, cacheGeneration);
        Interlocked.Exchange(ref _artState, 2);
        AlbumArt = bmp;
        AlbumArtLoader.Shown(this);
    }

    // A cover replaced since this one was loaded - see AlbumArtLoader.Shown.
    public void AlbumArtReplaced()
    {
        if (Volatile.Read(ref _artState) == 2 && Volatile.Read(ref _artCacheGeneration) != AlbumArtLoader.CacheGeneration)
            OnPropertyChanged(nameof(AlbumArt));
    }

    // Greyed-out state, same rule and same look as the main track list's
    // TrackRowViewModel.IsUnavailable: a not-yet-downloaded track with nobody
    // to stream it from right now. Set at construction and kept live by
    // AlbumGridRowViewModel.Availability.
    private bool _isUnavailable;
    public bool IsUnavailable
    {
        get => _isUnavailable;
        set { if (_isUnavailable != value) { _isUnavailable = value; OnPropertyChanged(); } }
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
    }

    // Driven directly from AlbumGridRowControl.axaml.cs's PointerEntered/
    // PointerExited, not a XAML :pointerover Style selector - an earlier
    // attempt at a Style-selector-driven hover overlay didn't visibly work,
    // and the row Border's own Background="Transparent" *local* value (needed
    // for full-row hit-testing) is one plausible reason why (a local value
    // outranks a Style setter of the same property regardless of pseudo-
    // class). Routing hover through this bound property instead - the same
    // mechanism IsSelected already uses successfully - sidesteps that
    // question entirely rather than betting on the same approach again.
    private bool _isHovered;
    public bool IsHovered
    {
        get => _isHovered;
        set { if (_isHovered != value) { _isHovered = value; OnPropertyChanged(); } }
    }

    // Picked or pointed at - the rows that draw a background.
    public bool IsLit => IsSelected || IsHovered;

    // Whether the lit row above or below touches this one, so their
    // backgrounds square the corners between them and a run of lit rows
    // reads as one shape rounded only at its ends - the main track list's
    // look (MusicListPanel.RefreshHighlightShape). Set by
    // AlbumTracksViewModel, which knows the neighbours.
    private bool _joinsAbove;
    public bool JoinsAbove
    {
        get => _joinsAbove;
        set { if (_joinsAbove != value) { _joinsAbove = value; OnPropertyChanged(); } }
    }

    private bool _joinsBelow;
    public bool JoinsBelow
    {
        get => _joinsBelow;
        set { if (_joinsBelow != value) { _joinsBelow = value; OnPropertyChanged(); } }
    }

    // Bolds the row - see AlbumGridRowControl.axaml's Grid.playingTrack style,
    // the same mark TrackRowControl puts on its own playing row
    // (TrackRowViewModel.IsCurrentlyPlaying), just wired through
    // AlbumGridRowViewModel.CurrentlyPlayingTrackId instead since this row has
    // no equivalent of TrackListBuilder.BuildRows to compute it centrally.
    private bool _isCurrentlyPlaying;
    public bool IsCurrentlyPlaying
    {
        get => _isCurrentlyPlaying;
        set { if (_isCurrentlyPlaying != value) { _isCurrentlyPlaying = value; OnPropertyChanged(); } }
    }
}
