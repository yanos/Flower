using Flower.Models;

namespace Flower.ViewModels;

// One row in an expanded album's two-column track list (AlbumGridRowViewModel.
// Column1Tracks/Column2Tracks) - wraps the raw Track with the click-to-select
// state MusicListView's own rows already have (TrackRowViewModel.IsSelected),
// which a bare Track binding had no room for. Only one album is ever expanded
// at a time (see MainViewModel.ExpandedAlbumName), so selection only needs to
// be tracked within whichever one AlbumGridRowViewModel currently owns these -
// see AlbumGridRowViewModel.SelectTrack.
public sealed class ExpandedTrackRowViewModel : ViewModelBase
{
    public required Track Track { get; init; }

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

    // Drives the little "now playing" arrow - see AlbumGridRowControl.axaml,
    // same "▶" indicator TrackRowControl already shows for its own rows
    // (TrackRowViewModel.IsCurrentlyPlaying), just wired through
    // AlbumGridRowViewModel.CurrentlyPlayingPath instead since this row has
    // no equivalent of TrackListBuilder.BuildRows to compute it centrally.
    private bool _isCurrentlyPlaying;
    public bool IsCurrentlyPlaying
    {
        get => _isCurrentlyPlaying;
        set { if (_isCurrentlyPlaying != value) { _isCurrentlyPlaying = value; OnPropertyChanged(); } }
    }
}
