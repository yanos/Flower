using System;
using System.Collections.Generic;
using System.Linq;

using Flower.ViewModels;

using FlowerTrack = Flower.Models.Track;

namespace Flower.Controls;

/// <summary>
/// The track list's multi-selection: plain, Shift-range and Primary-toggle
/// presses, the range anchor, and carrying the selection across a rebuild of
/// the rows. <see cref="MusicListView"/> owns the pointer, the keyboard and the
/// TwoWay <c>SelectedTrack</c> binding; this owns what a press does to the
/// selection, and is testable without a control at all.
/// </summary>
/// <remarks>
/// Keyed by Track.Id rather than row identity: rows are rebuilt from scratch on
/// every <see cref="SetItems"/> (filter/sort/view switch), so IsSelected never
/// survives on its own and the selection has to be re-matched against the new
/// instances.
///
/// Id, not Path, because Path is not an identity: it is null for every
/// not-yet-downloaded placeholder, so all of them hashed to the same key.
/// Selecting one greyed-out track therefore re-selected *every* greyed-out
/// track in the view on the next rebuild - and dragging it to a playlist
/// dropped all of them in. Id is the same identity TrackRowMerge already reuses
/// rows by, and it survives a rescan (Library.CarryForwardMutableState copies it
/// onto the new Track instance).
/// </remarks>
public sealed class TrackRowSelection
{
    private IReadOnlyList<TrackRowViewModel> _items = [];
    private readonly HashSet<Guid> _selectedIds = new();
    private readonly List<TrackRowViewModel> _selectedRows = new();
    private Guid? _anchorId;

    /// <summary>The row the selection is about - the last one picked.</summary>
    public TrackRowViewModel? Current { get; private set; }

    public int Count => _selectedRows.Count;

    public IReadOnlyList<TrackRowViewModel> Rows => _selectedRows.ToList();

    public IReadOnlyList<FlowerTrack> Tracks => _selectedRows.Select(r => r.Track).ToList();

    public IReadOnlyList<TrackRowViewModel> Items => _items;

    /// <summary>
    /// A press on <paramref name="row"/>: Shift extends from the anchor,
    /// Primary toggles, and a plain press collapses onto the row - unless the
    /// row is already part of the selection, which is kept whole so a drag or
    /// the context menu acts on all of it. Returns whether the selection
    /// changed.
    /// </summary>
    public bool Press(TrackRowViewModel row, bool extend, bool toggle)
    {
        if (extend)
            ExtendTo(row);
        else if (toggle)
            Toggle(row);
        else if (!row.IsSelected)
            SelectOnly(row);
        else
            return false;
        return true;
    }

    /// <summary>Collapses the selection to <paramref name="row"/>, or clears it for null.</summary>
    public void SelectOnly(TrackRowViewModel? row)
    {
        Clear();
        Current = row;
        if (row != null)
        {
            row.IsSelected = true;
            _selectedRows.Add(row);
            _selectedIds.Add(row.Track.Id);
            _anchorId = row.Track.Id;
        }
    }

    public void Toggle(TrackRowViewModel row)
    {
        row.IsSelected = !row.IsSelected;
        if (row.IsSelected)
        {
            _selectedIds.Add(row.Track.Id);
            _selectedRows.Add(row);
            Current = row;
            _anchorId = row.Track.Id;
        }
        else
        {
            _selectedIds.Remove(row.Track.Id);
            _selectedRows.Remove(row);
            if (Current == row)
                Current = _selectedRows.Count > 0 ? _selectedRows[^1] : null;
        }
    }

    /// <summary>
    /// Selects the inclusive range from the anchor to <paramref name="row"/>.
    /// The anchor itself is left where it was, so a further Shift+press
    /// re-measures from the same start rather than from this one's row.
    /// </summary>
    public void ExtendTo(TrackRowViewModel row)
    {
        var anchorRow = _anchorId is { } anchorId ? _items.FirstOrDefault(r => r.Track.Id == anchorId) : null;
        int anchorIdx = anchorRow != null ? IndexOf(anchorRow) : IndexOf(row);
        int clickIdx = IndexOf(row);
        if (anchorIdx < 0)
            anchorIdx = clickIdx;
        int lo = Math.Min(anchorIdx, clickIdx);
        int hi = Math.Max(anchorIdx, clickIdx);

        Clear();
        for (int i = lo; i <= hi; i++)
        {
            _items[i].IsSelected = true;
            _selectedIds.Add(_items[i].Track.Id);
            _selectedRows.Add(_items[i]);
        }
        Current = row;
    }

    /// <summary>
    /// Arrow-key movement from <see cref="Current"/>: one row on, collapsing to
    /// it or, with <paramref name="extend"/>, growing the range from the
    /// anchor. Returns the index moved to, or -1 for no move.
    /// </summary>
    public int Move(int delta, bool extend)
    {
        if (_items.Count == 0)
            return -1;
        int current = Current == null ? -1 : IndexOf(Current);
        int next = Math.Clamp(current + delta, 0, _items.Count - 1);
        if (next == current)
            return -1;
        if (extend)
            ExtendTo(_items[next]);
        else
            SelectOnly(_items[next]);
        return next;
    }

    public TrackRowViewModel? RowFor(FlowerTrack? track) =>
        track != null ? _items.FirstOrDefault(r => r.Track.Id == track.Id) : null;

    /// <summary>
    /// Re-applies the selection to a rebuilt set of rows. Anything no longer
    /// present (filtered or sorted out) is dropped for good, so switching back
    /// does not resurrect it.
    /// </summary>
    public void SetItems(IReadOnlyList<TrackRowViewModel> items, FlowerTrack? selectedTrack)
    {
        _items = items;

        _selectedRows.Clear();
        if (_selectedIds.Count > 0)
        {
            var stillPresent = new HashSet<Guid>();
            foreach (var row in items)
            {
                if (_selectedIds.Contains(row.Track.Id))
                {
                    row.IsSelected = true;
                    _selectedRows.Add(row);
                    stillPresent.Add(row.Track.Id);
                }
            }
            _selectedIds.IntersectWith(stillPresent);
        }

        if (RowFor(selectedTrack) is { } match)
            Current = match;
    }

    private void Clear()
    {
        foreach (var row in _selectedRows)
            row.IsSelected = false;
        _selectedRows.Clear();
        _selectedIds.Clear();
    }

    private int IndexOf(TrackRowViewModel item)
    {
        for (int i = 0; i < _items.Count; i++)
            if (_items[i] == item)
                return i;
        return -1;
    }
}
