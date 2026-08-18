using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia;

namespace Flower.Controls;

/// <summary>
/// The shift-range / ctrl-toggle / drag-threshold selection gesture shared by
/// <c>MainView</c>'s SubList (album and artist names, a stock <c>ListBox</c>)
/// and both album tile grids. All three are the same gesture over a list of
/// item *names*, differing only in how a name is hit-tested out of a pointer
/// event and in what order the names are laid out for a Shift+click range -
/// both of which stay in the view, since they're the genuinely control-bound
/// half. Everything else - anchor bookkeeping, the ctrl-toggle algebra, the
/// squared-distance drag threshold and resolving which items a drag actually
/// carries - lives here, once, and is testable without a control at all.
/// </summary>
/// <remarks>
/// The selection itself is not owned here: it lives on the view model
/// (<c>MainViewModel.SelectedSubItems</c>), which all three call sites share,
/// so it is read and written through the delegates passed in. The Shift+click
/// anchor, by contrast, is per-instance - a range only means anything against
/// one particular ordering, so the two grids (alphabetical vs. by-recency)
/// each keep their own.
/// </remarks>
public sealed class NameSelectionDragGesture
{
    /// <summary>Squared-distance threshold, in pixels, before a press becomes a drag.</summary>
    public const double DragThreshold = 4.0;

    private readonly Func<IReadOnlyList<string>> _orderedItems;
    private readonly Func<IReadOnlyCollection<string>> _currentSelection;
    private readonly Action<IReadOnlyList<string>> _setSelection;
    private readonly bool _selectOnPlainPress;

    private string? _anchor;
    private Point _pressPoint;

    /// <param name="orderedItems">
    /// The items in display order - the ordering a Shift+click range is taken over.
    /// </param>
    /// <param name="currentSelection">The live selection, read fresh on every use.</param>
    /// <param name="setSelection">Replaces the selection wholesale.</param>
    /// <param name="selectOnPlainPress">
    /// Whether an unmodified press on a not-yet-selected item collapses the
    /// selection onto it immediately. True for SubList, whose click both selects
    /// and navigates; false for the album grids, where a plain click expands a
    /// tile in place without touching the tile-level selection at all, and
    /// where the drag path falls back to the pressed item on its own anyway.
    /// </param>
    public NameSelectionDragGesture(
        Func<IReadOnlyList<string>> orderedItems,
        Func<IReadOnlyCollection<string>> currentSelection,
        Action<IReadOnlyList<string>> setSelection,
        bool selectOnPlainPress)
    {
        _orderedItems = orderedItems;
        _currentSelection = currentSelection;
        _setSelection = setSelection;
        _selectOnPlainPress = selectOnPlainPress;
    }

    /// <summary>The item pressed on, until the gesture ends. Null when no press is in flight.</summary>
    public string? PressedItem { get; private set; }

    /// <summary>True once the pointer has moved past <see cref="DragThreshold"/> since the press.</summary>
    public bool IsDragging { get; private set; }

    /// <summary>
    /// The items the in-flight drag carries, resolved once at threshold-crossing.
    /// Null until then.
    /// </summary>
    public IReadOnlyList<string>? DragItems { get; private set; }

    /// <summary>
    /// Applies a press on <paramref name="item"/> and arms the drag threshold.
    /// Returns whether the item was already selected *before* this press -
    /// which is what the album grids use to decide a plain click is a
    /// pending expand/collapse rather than a selection change.
    /// </summary>
    public bool Press(string item, Point position, bool shift, bool toggle)
    {
        bool alreadySelected = _currentSelection().Contains(item);

        if (shift)
            SelectRangeTo(item);
        else if (toggle)
            Toggle(item);
        else if (_selectOnPlainPress && !alreadySelected)
        {
            _setSelection(new[] { item });
            _anchor = item;
        }
        // else: already selected with no modifier - preserve the whole
        // selection so it can be dragged or right-clicked as a batch.

        PressedItem = item;
        _pressPoint = position;
        IsDragging = false;
        DragItems = null;
        return alreadySelected;
    }

    /// <summary>
    /// Feeds a pointer move. Returns true once a drag is in flight - i.e. the
    /// caller should now be drawing a ghost and highlighting a drop target -
    /// and false while the press is still within the threshold or absent.
    /// </summary>
    public bool Move(Point position)
    {
        if (PressedItem == null)
            return false;

        if (!IsDragging)
        {
            var dx = position.X - _pressPoint.X;
            var dy = position.Y - _pressPoint.Y;
            if (dx * dx + dy * dy < DragThreshold * DragThreshold)
                return false;

            IsDragging = true;
            // Selection is final by now - Press already resolved it. A drag
            // started from outside the selection carries only the pressed
            // item, without disturbing what is selected.
            DragItems = _currentSelection().Contains(PressedItem)
                ? _currentSelection().ToList()
                : new List<string> { PressedItem };
        }

        return true;
    }

    /// <summary>Clears the press/drag state. Leaves the selection and the anchor alone.</summary>
    public void End()
    {
        PressedItem = null;
        DragItems = null;
        IsDragging = false;
    }

    private void Toggle(string item)
    {
        var current = _currentSelection().ToList();
        if (!current.Remove(item))
            current.Add(item);
        _setSelection(current);
        _anchor = item;
    }

    private void SelectRangeTo(string item)
    {
        var items = _orderedItems();
        int anchorIdx = _anchor != null ? IndexOf(items, _anchor) : -1;
        int clickIdx = IndexOf(items, item);
        if (clickIdx < 0)
            return;
        if (anchorIdx < 0)
            anchorIdx = clickIdx;

        int lo = Math.Min(anchorIdx, clickIdx);
        int hi = Math.Max(anchorIdx, clickIdx);

        var range = new List<string>(hi - lo + 1);
        for (int i = lo; i <= hi; i++)
            range.Add(items[i]);

        // Anchor deliberately left untouched so repeated Shift+clicks keep
        // extending/shrinking the range from the same starting point.
        _setSelection(range);
    }

    private static int IndexOf(IReadOnlyList<string> items, string value)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] == value)
                return i;
        }
        return -1;
    }
}
