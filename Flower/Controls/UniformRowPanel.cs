using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace Flower.Controls;

// A vertical virtualizing panel for lists whose rows are all one height - the
// Songs list, a playlist, the queue, the artist picker, the rows of an album
// grid - that keeps every row it has made attached for as long as it lives.
//
// That second half is the point. Avalonia's VirtualizingStackPanel takes a row
// that scrolls out of the window off the visual tree and puts it back for the
// next one coming in, and a control joining a tree is the expensive thing it
// does: a new composition visual for each element of the row, and every
// inherited value - theme, font, brushes - pushed down through all of them
// again. Measured on the Songs list, that was most of 60KB allocated per frame
// of a fling. Here a row that leaves the window is handed the next item to
// show and moved; the only rows ever hidden are the spares a shrinking window
// leaves over, and they are hidden, not removed.
//
// One height is what lets it stay this simple: where row N goes is N times
// that height, so there is nothing to estimate and nothing to anchor. The
// height is the first row's own, measured at the width the panel is given and
// again whenever that width changes.
//
// HasTallRows allows a few rows taller than the rest - the desktop's Albums
// grid, where the row of an album opened in place grows to hold its songs.
// Then the height is the shortest row's, every row measured taller is
// remembered with its height, and where row N goes is N times the height plus
// what the taller rows above it add. A taller row is kept realized wherever it
// is scrolled to, so that what it adds is always its height now rather than
// the height it had when it was last on screen - its album may close while it
// is out of view. A list whose rows are many heights (Home's headings and
// shelves) wants VirtualizingStackPanel.
public sealed class UniformRowPanel : VirtualizingPanel
{
    // Whether any row may be taller than the rest - see above.
    public bool HasTallRows { get; set; }

    // The rows measured taller than the rest, by index, in index order, and
    // each one's height.
    private readonly List<(int Index, double Height)> _tall = new();

    // Rows realized beyond each edge of the window, so the one about to come
    // on screen has already been handed its item a frame or two before.
    private const int BufferRows = 2;

    private sealed class Row(Control container, object? recycleKey)
    {
        public readonly Control Container = container;
        public readonly object? RecycleKey = recycleKey;
        public int Index;
    }

    private readonly List<Row> _rows = new();
    private readonly List<Row> _spare = new();
    private Rect _viewport;
    private bool _hasViewport;
    private double _rowHeight = double.NaN;
    private double _measuredWidth = double.NaN;
    private int _first;
    private int _last = -1;
    private int _scrollingTo = -1;
    private bool _inLayout;

    public UniformRowPanel()
    {
        EffectiveViewportChanged += OnEffectiveViewportChanged;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = Items.Count;
        if (ItemContainerGenerator is null || count == 0)
        {
            RecycleOutside(0, -1);
            _tall.Clear();
            _rowHeight = double.NaN;
            return default;
        }

        _inLayout = true;
        try
        {
            var width = availableSize.Width;
            if (double.IsNaN(_rowHeight) || width != _measuredWidth)
            {
                _tall.Clear();
                MeasureRowHeight(width, count);
            }

            // Again only if the rows came back a different height from the
            // one the window was worked out with - which a taller row coming
            // into view, or growing as its album opens, also is.
            for (var pass = 0; pass < 3; pass++)
            {
                (_first, _last) = WindowFor(count, availableSize.Height);
                RecycleOutside(_first, _last);
                for (var index = _first; index <= _last; index++)
                {
                    if (RowAt(index) == null)
                        Realize(index);
                }
                if (_scrollingTo >= 0 && _scrollingTo < count && RowAt(_scrollingTo) == null)
                    Realize(_scrollingTo);

                Row? top = null;
                var shortest = double.PositiveInfinity;
                foreach (var row in _rows)
                {
                    row.Container.Measure(new Size(width, double.PositiveInfinity));
                    if (top == null || row.Index < top.Index)
                        top = row;
                    shortest = Math.Min(shortest, row.Container.DesiredSize.Height);
                }

                if (top == null)
                    break;
                var height = Math.Max(1, HasTallRows ? Math.Min(_rowHeight, shortest) : top.Container.DesiredSize.Height);
                var changed = Math.Abs(height - _rowHeight) >= 0.01;
                _rowHeight = height;
                if (HasTallRows)
                    changed |= NoteTallRows();
                if (!changed)
                    break;
            }

            var desiredWidth = double.IsInfinity(width) && _rows.Count > 0 ? _rows.Max(r => r.Container.DesiredSize.Width) : width;
            if (double.IsInfinity(desiredWidth))
                desiredWidth = 0;
            return new Size(desiredWidth, RowTop(count));
        }
        finally
        {
            _inLayout = false;
        }
    }

    // Records each realized row's height where it is taller than the rest, and
    // forgets it where it no longer is. True if anything moved.
    private bool NoteTallRows()
    {
        var changed = false;
        foreach (var row in _rows)
        {
            var height = row.Container.DesiredSize.Height;
            var at = TallAt(row.Index);
            if (height > _rowHeight + 0.5)
            {
                if (at < 0)
                {
                    var insert = 0;
                    while (insert < _tall.Count && _tall[insert].Index < row.Index)
                        insert++;
                    _tall.Insert(insert, (row.Index, height));
                    changed = true;
                }
                else if (Math.Abs(_tall[at].Height - height) >= 0.01)
                {
                    _tall[at] = (row.Index, height);
                    changed = true;
                }
            }
            else if (at >= 0)
            {
                _tall.RemoveAt(at);
                changed = true;
            }
        }
        return changed;
    }

    private int TallAt(int index)
    {
        for (var i = 0; i < _tall.Count; i++)
        {
            if (_tall[i].Index == index)
                return i;
        }
        return -1;
    }

    // Where row `index` starts; RowTop(Count) is where the last one ends.
    private double RowTop(int index)
    {
        var top = index * _rowHeight;
        foreach (var (tall, height) in _tall)
        {
            if (tall >= index)
                break;
            top += height - _rowHeight;
        }
        return top;
    }

    private double RowHeightOf(int index)
    {
        var at = TallAt(index);
        return at >= 0 ? _tall[at].Height : _rowHeight;
    }

    // The row `y` falls in, unclamped.
    private int IndexAt(double y)
    {
        var added = 0.0;
        foreach (var (tall, height) in _tall)
        {
            var top = tall * _rowHeight + added;
            if (y < top)
                break;
            if (y < top + height)
                return tall;
            added += height - _rowHeight;
        }
        return (int)Math.Floor((y - added) / _rowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var row in _rows)
            row.Container.Arrange(new Rect(0, RowTop(row.Index), finalSize.Width, RowHeightOf(row.Index)));
        return finalSize;
    }

    // The first time, or at a new width: any row will do, since they are all
    // one height, so a realized one if there is one and the first item if not.
    private void MeasureRowHeight(double width, int count)
    {
        var row = _rows.Count > 0 ? _rows[0] : Realize(Math.Clamp(_first, 0, count - 1));
        row.Container.Measure(new Size(width, double.PositiveInfinity));
        _rowHeight = Math.Max(1, row.Container.DesiredSize.Height);
        _measuredWidth = width;
    }

    // The rows the window covers, plus the buffer. Before the panel has been
    // told where it is being looked at, the top of it, as far down as it has
    // room for; once it has, nothing at all if it is entirely out of view - a
    // screen kept alive behind the one showing has no rows worth handing items.
    private (int First, int Last) WindowFor(int count, double availableHeight)
    {
        double top, bottom;
        if (_hasViewport)
        {
            top = _viewport.Top;
            bottom = _viewport.Bottom;
        }
        else
        {
            top = 0;
            bottom = double.IsInfinity(availableHeight) ? 1000 : availableHeight;
        }

        if (bottom <= 0 || top >= RowTop(count) || bottom <= top)
            return (0, -1);

        var first = Math.Max(0, IndexAt(top) - BufferRows);
        var last = Math.Min(count - 1, IndexAt(Math.Max(top, bottom - 0.01)) + BufferRows);
        return (first, last);
    }

    private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        _viewport = e.EffectiveViewport;
        _hasViewport = true;
        _scrollingTo = -1;

        // Only a change in which rows are showing needs a measure; the rows
        // themselves do not move inside the panel as it scrolls.
        if (_inLayout || double.IsNaN(_rowHeight))
            return;
        var (first, last) = WindowFor(Items.Count, Bounds.Height);
        if (first != _first || last != _last)
            InvalidateMeasure();
    }

    private Row Realize(int index)
    {
        var generator = ItemContainerGenerator!;
        var item = Items[index];
        Row row;

        if (!generator.NeedsContainer(item, index, out var recycleKey))
        {
            // The item is a control already: it is its own row, and only ever
            // shows itself.
            var control = (Control)item!;
            row = new Row(control, null);
            generator.PrepareItemContainer(control, control, index);
            AddInternalChild(control);
            generator.ItemContainerPrepared(control, item, index);
        }
        else if (TakeSpare(recycleKey) is { } spare)
        {
            row = spare;
            generator.PrepareItemContainer(row.Container, item, index);
            row.Container.SetCurrentValue(IsVisibleProperty, true);
            generator.ItemContainerPrepared(row.Container, item, index);
        }
        else
        {
            var container = generator.CreateContainer(item, index, recycleKey);
            row = new Row(container, recycleKey);
            generator.PrepareItemContainer(container, item, index);
            AddInternalChild(container);
            generator.ItemContainerPrepared(container, item, index);
        }

        row.Index = index;
        _rows.Add(row);
        return row;
    }

    private Row? TakeSpare(object? recycleKey)
    {
        for (var i = _spare.Count - 1; i >= 0; i--)
        {
            var row = _spare[i];
            if (Equals(row.RecycleKey, recycleKey))
            {
                _spare.RemoveAt(i);
                return row;
            }
        }
        return null;
    }

    // Every row outside [first, last] goes back to the spares: cleared of its
    // item and hidden, but left where it is in the tree for the next item that
    // needs one.
    private void RecycleOutside(int first, int last)
    {
        for (var i = _rows.Count - 1; i >= 0; i--)
        {
            var row = _rows[i];
            if ((row.Index >= first && row.Index <= last) || (last >= first && TallAt(row.Index) >= 0))
                continue;
            _rows.RemoveAt(i);
            Recycle(row);
        }
    }

    private void Recycle(Row row)
    {
        if (row.RecycleKey == null)
        {
            // Its own container: there is nothing to hand it, and nobody else
            // can use it.
            RemoveInternalChild(row.Container);
            return;
        }

        ItemContainerGenerator?.ClearItemContainer(row.Container);
        row.Container.SetCurrentValue(IsVisibleProperty, false);
        _spare.Add(row);
    }

    protected override void OnItemsChanged(IReadOnlyList<object?> items, NotifyCollectionChangedEventArgs e)
    {
        base.OnItemsChanged(items, e);

        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                Shift(e.NewStartingIndex, e.NewItems!.Count);
                break;
            case NotifyCollectionChangedAction.Remove:
                ForgetTall(e.OldStartingIndex, e.OldItems!.Count);
                RecycleRange(e.OldStartingIndex, e.OldItems.Count);
                Shift(e.OldStartingIndex + e.OldItems.Count, -e.OldItems.Count);
                break;
            case NotifyCollectionChangedAction.Replace:
                ForgetTall(e.OldStartingIndex, e.OldItems!.Count);
                RecycleRange(e.OldStartingIndex, e.OldItems.Count);
                break;
            default:
                _tall.Clear();
                RecycleOutside(0, -1);
                break;
        }

        InvalidateMeasure();
    }

    private void RecycleRange(int start, int count)
    {
        for (var i = _rows.Count - 1; i >= 0; i--)
        {
            var row = _rows[i];
            if (row.Index < start || row.Index >= start + count)
                continue;
            _rows.RemoveAt(i);
            Recycle(row);
        }
    }

    // A row that is no longer there adds nothing; one that is will say what
    // it adds when it is measured.
    private void ForgetTall(int start, int count)
    {
        for (var i = _tall.Count - 1; i >= 0; i--)
        {
            if (_tall[i].Index >= start && _tall[i].Index < start + count)
                _tall.RemoveAt(i);
        }
    }

    // Rows at or past `from` now show an item `by` places further along.
    private void Shift(int from, int by)
    {
        for (var i = 0; i < _tall.Count; i++)
        {
            if (_tall[i].Index >= from)
                _tall[i] = (_tall[i].Index + by, _tall[i].Height);
        }

        foreach (var row in _rows)
        {
            if (row.Index < from)
                continue;
            var old = row.Index;
            row.Index += by;
            ItemContainerGenerator?.ItemContainerIndexChanged(row.Container, old, row.Index);
        }
    }

    protected override void OnItemsControlChanged(ItemsControl? oldValue)
    {
        base.OnItemsControlChanged(oldValue);
        _rows.Clear();
        _spare.Clear();
        _tall.Clear();
        Children.Clear();
        _rowHeight = double.NaN;
    }

    private Row? RowAt(int index)
    {
        foreach (var row in _rows)
        {
            if (row.Index == index)
                return row;
        }
        return null;
    }

    protected override Control? ContainerFromIndex(int index) => RowAt(index)?.Container;

    protected override int IndexFromContainer(Control container)
    {
        foreach (var row in _rows)
        {
            if (ReferenceEquals(row.Container, container))
                return row.Index;
        }
        return -1;
    }

    protected override IEnumerable<Control>? GetRealizedContainers() =>
        _rows.OrderBy(r => r.Index).Select(r => r.Container);

    // Where row N goes is known before it exists, so this is: realize it,
    // ask the scroller for its slot, and let the layout that follows settle
    // the window round it.
    protected override Control? ScrollIntoView(int index)
    {
        if (_inLayout || index < 0 || index >= Items.Count)
            return null;

        if (double.IsNaN(_rowHeight))
            UpdateLayout();
        if (double.IsNaN(_rowHeight))
            return null;

        // Realized even if the scroller has not said where it moved to by the
        // end of the layout below; the next viewport it reports lets it go.
        _scrollingTo = index;
        InvalidateMeasure();
        this.BringIntoView(new Rect(0, RowTop(index), Bounds.Width, RowHeightOf(index)));
        UpdateLayout();
        return ContainerFromIndex(index);
    }

    protected override IInputElement? GetControl(NavigationDirection direction, IInputElement? from, bool wrap)
    {
        var count = Items.Count;
        var fromIndex = from is Control control ? IndexFromContainer(control) : -1;
        if (count == 0 || (fromIndex < 0 && direction is not (NavigationDirection.First or NavigationDirection.Last)))
            return null;

        var toIndex = direction switch
        {
            NavigationDirection.First => 0,
            NavigationDirection.Last => count - 1,
            NavigationDirection.Next or NavigationDirection.Down => fromIndex + 1,
            NavigationDirection.Previous or NavigationDirection.Up => fromIndex - 1,
            _ => fromIndex,
        };

        if (wrap)
            toIndex = (toIndex + count) % count;
        if (toIndex == fromIndex || toIndex < 0 || toIndex >= count)
            return null;
        return ScrollIntoView(toIndex);
    }
}
