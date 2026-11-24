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
/// Keyed by the row's <see cref="TrackRowViewModel.Key"/> rather than row
/// identity: rows are rebuilt on every <see cref="SetItems"/> (filter/sort/view
/// switch), so IsSelected never survives on its own and the selection has to
/// be re-matched against the new instances.
///
/// The key is the song's Id, not its Path, because Path is not an identity:
/// it is null for every not-yet-downloaded placeholder, so all of them hashed
/// to the same key. Selecting one greyed-out track therefore re-selected
/// *every* greyed-out track in the view on the next rebuild - and dragging it
/// to a playlist dropped all of them in. Id survives a rescan
/// (Library.CarryForwardMutableState copies it onto the new Track instance).
///
/// In a playlist or the queue it is the entry instead, which one song there
/// twice has two of: picking one copy must not select both after the next
/// rebuild, or Remove from Playlist takes both out. An entry keeps its id
/// through a move, so a row dragged past another copy of its song is still
/// the one selected.
/// </remarks>
public sealed class TrackRowSelection
{
    private IReadOnlyList<TrackRowViewModel> _items = [];

    private readonly HashSet<Guid> _selectedKeys = new();
    private readonly List<TrackRowViewModel> _selectedRows = new();
    private TrackRowViewModel? _anchor;
    private Guid? _anchorKey;
    private Guid? _currentKey;

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
            _anchor = row;
        }
        Remember();
    }

    public void Toggle(TrackRowViewModel row)
    {
        row.IsSelected = !row.IsSelected;
        if (row.IsSelected)
        {
            _selectedRows.Add(row);
            Current = row;
            _anchor = row;
        }
        else
        {
            _selectedRows.Remove(row);
            if (Current == row)
                Current = _selectedRows.Count > 0 ? _selectedRows[^1] : null;
        }
        Remember();
    }

    /// <summary>
    /// Selects the inclusive range from the anchor to <paramref name="row"/>.
    /// The anchor itself is left where it was, so a further Shift+press
    /// re-measures from the same start rather than from this one's row.
    /// </summary>
    public void ExtendTo(TrackRowViewModel row)
    {
        int anchorIdx = _anchor != null ? IndexOf(_anchor) : IndexOf(row);
        int clickIdx = IndexOf(row);
        if (anchorIdx < 0)
            anchorIdx = clickIdx;
        int lo = Math.Min(anchorIdx, clickIdx);
        int hi = Math.Max(anchorIdx, clickIdx);

        Clear();
        for (int i = lo; i <= hi; i++)
        {
            _items[i].IsSelected = true;
            _selectedRows.Add(_items[i]);
        }
        Current = row;
        Remember();
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
        TrackRowViewModel? anchor = null;
        TrackRowViewModel? current = null;
        var stillPresent = new HashSet<Guid>();
        foreach (var row in items)
        {
            var key = row.Key;
            // Set either way: a reused row may carry a mark from before.
            row.IsSelected = _selectedKeys.Contains(key);
            if (row.IsSelected)
            {
                _selectedRows.Add(row);
                stillPresent.Add(key);
            }
            if (key == _anchorKey)
                anchor = row;
            if (key == _currentKey)
                current = row;
        }
        _selectedKeys.IntersectWith(stillPresent);
        _anchor = anchor;

        if (current != null && (selectedTrack == null || current.Track.Id == selectedTrack.Id))
        {
            Current = current;
        }
        else if (RowFor(selectedTrack) is { } match)
        {
            Current = match;
            _currentKey = null;
        }
    }

    private void Remember()
    {
        _selectedKeys.Clear();
        foreach (var row in _selectedRows)
            _selectedKeys.Add(row.Key);
        _anchorKey = _anchor?.Key;
        _currentKey = Current?.Key;
    }

    private void Clear()
    {
        foreach (var row in _selectedRows)
            row.IsSelected = false;
        _selectedRows.Clear();
        _selectedKeys.Clear();
    }

    private int IndexOf(TrackRowViewModel item)
    {
        for (int i = 0; i < _items.Count; i++)
            if (_items[i] == item)
                return i;
        return -1;
    }
}
