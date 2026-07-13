using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;
using Flower.ViewModels.Mobile;

namespace Flower.ViewModels;

// One row of tiles in AlbumGridView's row-chunked grid (desktop's own N-per-row
// generalization of mobile's fixed-2-per-row AlbumGridRow - see AlbumGridView.
// RebuildRows). Rebuilt whenever the tile list or column count changes, but
// IsExpanded/ExpandedTracks are pushed in afterward by AlbumGridView.
// ApplyExpansion rather than being part of that rebuild, so expanding a row
// doesn't require rebuilding every row's Tiles list too.
public sealed class AlbumGridRowViewModel : ViewModelBase
{
    public required IReadOnlyList<AlbumTileViewModel> Tiles { get; init; }

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded != value) { _isExpanded = value; OnPropertyChanged(); } }
    }

    private IReadOnlyList<Track> _expandedTracks = Array.Empty<Track>();
    public IReadOnlyList<Track> ExpandedTracks
    {
        get => _expandedTracks;
        set
        {
            _expandedTracks = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Column1Tracks));
            OnPropertyChanged(nameof(Column2Tracks));
        }
    }

    // Split top-to-bottom-then-next-column (like a phone book), not
    // interleaved row-by-row - matches how a numbered list normally reads
    // across two columns.
    private int Column1Count => (_expandedTracks.Count + 1) / 2;
    public IReadOnlyList<Track> Column1Tracks => _expandedTracks.Take(Column1Count).ToList();
    public IReadOnlyList<Track> Column2Tracks => _expandedTracks.Skip(Column1Count).ToList();
}
