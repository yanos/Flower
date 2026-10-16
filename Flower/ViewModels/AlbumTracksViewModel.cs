using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;
using Flower.Services;

namespace Flower.ViewModels;

// One album's songs as a short list of their own, outside the main track list:
// the Albums grid's expanded album (AlbumGridRowViewModel) and the Artists
// page's album cards (ArtistAlbumSectionViewModel), both drawn by
// AlbumTrackList. Owns the rows, which of them is selected, and what is
// playable and playing.
public abstract class AlbumTracksViewModel : ViewModelBase
{
    // What counts as playable right now, pushed in by AlbumGridView alongside
    // the tracks themselves - an expanded album's rows are built here from raw
    // Tracks (there is no TrackListBuilder pass behind them), so without this
    // they would be the one song list in the app with no way to know the
    // server had gone away. Set before ExpandedTracks by ApplyExpansion, and
    // re-applied to the rows already built when it changes on its own.
    private TrackAvailabilityContext _availability;
    public TrackAvailabilityContext Availability
    {
        get => _availability;
        set
        {
            _availability = value;
            foreach (var row in _trackRows)
            {
                row.IsUnavailable  = !value.IsPlayable(row.Track);
                row.IsDownloadable = value.IsDownloadable(row.Track);
            }
        }
    }

    private IReadOnlyList<Track> _expandedTracks = Array.Empty<Track>();
    public IReadOnlyList<Track> ExpandedTracks
    {
        get => _expandedTracks;
        set
        {
            _expandedTracks = value;
            // The outgoing rows are dropped here, so anything still animating
            // on one of them (a download spinner) has to be unsubscribed from
            // the shared AnimationClock - same reason TrackRowMerge disposes
            // the rows a rebuild discards.
            foreach (var row in _trackRows)
                row.Dispose();
            // Fresh ExpandedTrackRowViewModel instances each time, not just a
            // re-wrap of the same rows - a set here always means the expanded
            // album (or its track list) changed, so any previous selection is
            // stale anyway (see SelectTrack).
            _trackRows = value
                .Select(t => new ExpandedTrackRowViewModel
                {
                    Track = t,
                    IsCurrentlyPlaying = _currentlyPlayingTrackId is { } id && t.Id == id,
                    IsUnavailable = !_availability.IsPlayable(t),
                    IsDownloadable = _availability.IsDownloadable(t),
                })
                .ToList();
            OnPropertyChanged();
            OnPropertyChanged(nameof(TrackRows));
        }
    }

    // Whichever track is currently playing, if any - see
    // AlbumGridView.CurrentlyPlayingTrackId. Pushed in independently of
    // ExpandedTracks above (a track can start/stop playing without this
    // row's own expansion state changing at all), but also applied to any
    // *new* rows ExpandedTracks produces (see its setter above), so
    // whichever one becomes newly expanded still shows the right indicator
    // immediately rather than waiting for the next playback change.
    //
    // The track's Id, not its Path: every undownloaded placeholder has a null
    // Path, so a path comparison lit the indicator on all of them at once
    // whenever nothing was playing - see TrackListBuilder.PlanRows, which had
    // to guard against exactly that.
    private Guid? _currentlyPlayingTrackId;
    public Guid? CurrentlyPlayingTrackId
    {
        get => _currentlyPlayingTrackId;
        set
        {
            _currentlyPlayingTrackId = value;
            foreach (var row in _trackRows)
                row.IsCurrentlyPlaying = value is { } id && row.Track.Id == id;
        }
    }

    // List, not IReadOnlyList, so SelectTrack's range-select below can index
    // into it directly (IReadOnlyList<T> has no IndexOf) - still only ever
    // exposed as IReadOnlyList via TrackRows.
    private List<ExpandedTrackRowViewModel> _trackRows = new();

    public IReadOnlyList<ExpandedTrackRowViewModel> TrackRows => _trackRows;

    // A song was picked in this list, so a page holding several of them (the
    // Artists page's album cards) can clear the others - one selection at a
    // time, as a single list would have.
    public event System.EventHandler? SelectionStarted;

    public void ClearSelection()
    {
        foreach (var row in _trackRows)
            row.IsSelected = false;
        _selectionAnchor = null;
        _currentRow = null;
    }

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
    // base rationale), in album order (_trackRows).
    public void SelectTrack(ExpandedTrackRowViewModel target, bool toggle, bool rangeSelect)
    {
        SelectionStarted?.Invoke(this, System.EventArgs.Empty);

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
    // _currentRow by delta in the same album order SelectTrack's
    // range-select uses. Shift extends the range from the existing anchor
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
