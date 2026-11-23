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
///
/// With which copy of it, because a playlist or the queue can hold one song
/// twice and only the first copy's row survives a rebuild (TrackRowMerge).
/// Keyed by the song alone, picking one copy selected both after the next
/// rebuild, and Remove from Playlist then took both out. A filter keeps or
/// drops every copy together and the sort is stable, so the nth row of a song
/// is still its nth row afterwards.
/// </remarks>
public sealed class TrackRowSelection
{
    private IReadOnlyList<TrackRowViewModel> _items = [];
    private readonly record struct RowKey(Guid Track, int Occurrence);

    private readonly HashSet<RowKey> _selectedKeys = new();
    private readonly List<TrackRowViewModel> _selectedRows = new();
    private TrackRowViewModel? _anchor;
    private RowKey? _anchorKey;
    private RowKey? _currentKey;

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

    /// <summary>
    /// <paramref name="row"/> is being dragged to just before
    /// <paramref name="insertBefore"/>, or to the end for null. Dragged past
    /// another copy of its song it becomes a different occurrence, and the
    /// selection has to stay on the one that moved through the rebuild to come.
    /// </summary>
    public void NoteMoved(TrackRowViewModel row, TrackRowViewModel? insertBefore)
    {
        var order = _items.Where(r => !ReferenceEquals(r, row)).ToList();
        var at = insertBefore == null ? order.Count : order.IndexOf(insertBefore);
        if (at < 0)
            return;

        order.Insert(at, row);
        Remember(order);
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
        var songs = _selectedKeys.Select(k => k.Track).ToHashSet();
        if (_anchorKey is { } anchorKey)
            songs.Add(anchorKey.Track);
        if (_currentKey is { } currentKey)
            songs.Add(currentKey.Track);

        if (songs.Count > 0)
        {
            var stillPresent = new HashSet<RowKey>();
            var seen = new Dictionary<Guid, int>();
            foreach (var row in items)
            {
                if (!songs.Contains(row.Track.Id))
                    continue;

                var occurrence = seen.GetValueOrDefault(row.Track.Id);
                seen[row.Track.Id] = occurrence + 1;
                var key = new RowKey(row.Track.Id, occurrence);

                // Set either way: the row reused for a song's first copy may
                // be carrying the mark of a copy that has moved.
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
        }
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

    // Which occurrence of its song each row the selection cares about is, in
    // `order` (the items, unless they are about to change).
    private void Remember(IReadOnlyList<TrackRowViewModel>? order = null)
    {
        _selectedKeys.Clear();
        _anchorKey = null;
        _currentKey = null;

        var unplaced = new HashSet<TrackRowViewModel>(_selectedRows, ReferenceEqualityComparer.Instance);
        var songs = _selectedRows.Select(r => r.Track.Id).ToHashSet();
        if (_anchor != null)
            songs.Add(_anchor.Track.Id);
        if (Current != null)
            songs.Add(Current.Track.Id);
        if (songs.Count == 0)
            return;

        var seen = new Dictionary<Guid, int>();
        foreach (var row in order ?? _items)
        {
            if (!songs.Contains(row.Track.Id))
                continue;

            var occurrence = seen.GetValueOrDefault(row.Track.Id);
            seen[row.Track.Id] = occurrence + 1;
            var key = new RowKey(row.Track.Id, occurrence);
            if (unplaced.Remove(row))
                _selectedKeys.Add(key);
            if (ReferenceEquals(row, _anchor))
                _anchorKey = key;
            if (ReferenceEquals(row, Current))
                _currentKey = key;
        }

        // A row that is not one of the items: its song is all there is to go on.
        foreach (var row in unplaced)
            _selectedKeys.Add(new RowKey(row.Track.Id, 0));
        if (_anchor != null)
            _anchorKey ??= new RowKey(_anchor.Track.Id, 0);
        if (Current != null)
            _currentKey ??= new RowKey(Current.Track.Id, 0);
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
