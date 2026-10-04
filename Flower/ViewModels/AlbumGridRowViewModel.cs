using System.Collections.Generic;

using Flower.ViewModels.Mobile;

namespace Flower.ViewModels;

// One row of tiles in AlbumGridView's row-chunked grid (desktop's own N-per-row
// generalization of mobile's fixed-2-per-row AlbumGridRow - see AlbumGridView.
// RebuildRows). Rebuilt whenever the tile list or column count changes, but
// IsExpanded/ExpandedTracks are pushed in afterward by AlbumGridView.
// ApplyExpansion rather than being part of that rebuild, so expanding a row
// doesn't require rebuilding every row's Tiles list too. The expanded album's
// songs - selection, availability, the playing mark - are AlbumTracksViewModel's.
public sealed class AlbumGridRowViewModel : AlbumTracksViewModel
{
    public required IReadOnlyList<AlbumTileViewModel> Tiles { get; init; }

    // How many tiles a full row holds - the same on every row, a short last
    // one included, so its tiles are as wide as the rest.
    public required int Columns { get; init; }

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded != value) { _isExpanded = value; OnPropertyChanged(); } }
    }
}
