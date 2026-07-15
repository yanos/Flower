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
            // Fresh ExpandedTrackRowViewModel instances each time, not just a
            // re-wrap of the same rows - a set here always means the expanded
            // album (or its track list) changed, so any previous selection is
            // stale anyway (see SelectTrack).
            _trackRows = value
                .Select(t => new ExpandedTrackRowViewModel { Track = t, IsCurrentlyPlaying = t.Path == _currentlyPlayingPath })
                .ToList();
            OnPropertyChanged();
            OnPropertyChanged(nameof(Column1Tracks));
            OnPropertyChanged(nameof(Column2Tracks));
        }
    }

    // The path of whichever track is currently playing, if any - see
    // AlbumGridView.CurrentlyPlayingPath. Pushed in independently of
    // ExpandedTracks above (a track can start/stop playing without this
    // row's own expansion state changing at all), but also applied to any
    // *new* rows ExpandedTracks produces (see its setter above), so
    // whichever one becomes newly expanded still shows the right indicator
    // immediately rather than waiting for the next playback change.
    private string? _currentlyPlayingPath;
    public string? CurrentlyPlayingPath
    {
        get => _currentlyPlayingPath;
        set
        {
            _currentlyPlayingPath = value;
            foreach (var row in _trackRows)
                row.IsCurrentlyPlaying = row.Track.Path == value;
        }
    }

    // List, not IReadOnlyList, so SelectTrack's range-select below can index
    // into it directly (IReadOnlyList<T> has no IndexOf) - still only ever
    // exposed as IReadOnlyList via Column1Tracks/Column2Tracks.
    private List<ExpandedTrackRowViewModel> _trackRows = new();

    // Split top-to-bottom-then-next-column (like a phone book), not
    // interleaved row-by-row - matches how a numbered list normally reads
    // across two columns.
    private int Column1Count => (_trackRows.Count + 1) / 2;
    public IReadOnlyList<ExpandedTrackRowViewModel> Column1Tracks => _trackRows.Take(Column1Count).ToList();
    public IReadOnlyList<ExpandedTrackRowViewModel> Column2Tracks => _trackRows.Skip(Column1Count).ToList();

    // Two distinct pieces of state, mirroring MusicListView's own _anchorPath
    // (fixed) vs. _selectedRow (movable) - see that control's SelectSingleRow/
    // ToggleRow/SelectRange/MoveSelection for the identical split there.
    // _selectionAnchor is the range-select start: a plain or Ctrl/Cmd click
    // (or arrow-key move without Shift) relocates it, but a Shift-click/
    // Shift-arrow leaves it exactly where it was so repeated extends keep
    // growing/shrinking the same range. _currentRow is "wherever the active
    // end of the selection is right now" - what MoveSelection below steps
    // from, since if it read from _selectionAnchor instead, repeated
    // Shift+Down presses would keep re-selecting the same one-row range
    // instead of actually growing it.
    private ExpandedTrackRowViewModel? _selectionAnchor;
    private ExpandedTrackRowViewModel? _currentRow;

    // Click-to-select (AlbumGridRowControl.axaml.cs's TrackRow_PointerPressed)
    // - mirrors SubList/AlbumGrid's own click/Ctrl-toggle/Shift-range gesture
    // (see MainView.axaml.cs's SubList_PointerPressed doc comment for the
    // base rationale), applied across the flat top-to-bottom-then-next-column
    // order (_trackRows) both columns are split from, so a Shift-click range
    // spans across the column break exactly like reading the list normally
    // would.
    public void SelectTrack(ExpandedTrackRowViewModel target, bool toggle, bool rangeSelect)
    {
        if (rangeSelect)
        {
            var anchorIndex = _selectionAnchor != null ? _trackRows.IndexOf(_selectionAnchor) : -1;
            var targetIndex = _trackRows.IndexOf(target);
            if (anchorIndex < 0)
                anchorIndex = targetIndex;
            var lo = Math.Min(anchorIndex, targetIndex);
            var hi = Math.Max(anchorIndex, targetIndex);
            // Anchor deliberately left untouched so repeated Shift-clicks keep
            // extending/shrinking the range from the same starting point.
            for (var i = 0; i < _trackRows.Count; i++)
                _trackRows[i].IsSelected = i >= lo && i <= hi;
            _currentRow = target;
            return;
        }

        if (toggle)
        {
            target.IsSelected = !target.IsSelected;
            _selectionAnchor = target;
            _currentRow = target;
            return;
        }

        foreach (var row in _trackRows)
            row.IsSelected = ReferenceEquals(row, target);
        _selectionAnchor = target;
        _currentRow = target;
    }

    // Arrow-key navigation (AlbumGridRowControl.axaml.cs's own KeyDown
    // handler, registered the same way MusicListView.OnKeyDown is) - steps
    // _currentRow by delta across the same flat top-to-bottom-then-next-
    // column order SelectTrack's range-select uses, so Down at the bottom of
    // column 1 naturally continues into the top of column 2 instead of
    // stopping there. Shift extends the range from the existing anchor
    // (same primitive as Shift+click) instead of collapsing to one row -
    // identical semantics to MusicListView's own MoveSelection.
    public void MoveSelection(int delta, bool extend)
    {
        if (_trackRows.Count == 0)
            return;
        var current = _currentRow != null ? _trackRows.IndexOf(_currentRow) : -1;
        var next = Math.Clamp(current + delta, 0, _trackRows.Count - 1);
        if (next == current)
            return;
        SelectTrack(_trackRows[next], toggle: false, rangeSelect: extend);
    }

    public IReadOnlyList<Track> SelectedTracks =>
        _trackRows.Where(r => r.IsSelected).Select(r => r.Track).ToList();

    // The active end of the selection - what Enter (AlbumGridRowControl.
    // axaml.cs's OnKeyDown) plays. Deliberately _currentRow, not "whichever
    // selected track sorts last" - with a Shift-extended range built
    // upward (anchor below, current above), the active row is the *first*
    // one in _trackRows order, not the last, so SelectedTracks[^1] would
    // pick the wrong end.
    public Track? CurrentTrack => _currentRow?.Track;
}
