using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace Flower.Controls;

// A vertical virtualizing panel for a list of a few kinds of row, each kind
// one height - Home's headings, rows of covers and rows of songs - where now
// and then one row is taller than its kind: the row of an album opened in
// place, grown to hold its songs. Where a row goes is worked out exactly, from
// how tall each kind is and how tall each taller row is, never estimated.
//
// Avalonia's VirtualizingStackPanel estimates the rows it has not built from
// the average of those it has, and an open album's row is several times the
// height of the rest: with it on screen every unbuilt row was guessed that
// much taller, so the page grew and shrank by thousands of pixels as the album
// came and went from view, and the scroll jumped about with it.
//
// A kind is the item's type. Its height is the shortest of its rows measured
// at this width; any row of it measured taller is remembered, by the item,
// and kept realized wherever it is scrolled to, so that what it adds is
// always its height now - its album may close while it is out of view. By the
// item rather than its index, and by the item's own Equals, so a list handed a
// new collection holding equal rows - Home's rebuilds, which make a new
// heading every time (HomeShelfHeaderRow.Equals) - still knows how tall each
// one is before it comes back into view.
//
// Rows are kept attached once made and handed the next item to show, as
// UniformRowPanel's are; see there for why.
public sealed class ShelfRowPanel : RetainedRowPanel
{
    private const int BufferRows = 2;

    // Each kind's height at _measuredWidth, and the rows taller than theirs.
    private readonly Dictionary<Type, double> _kindHeights = new();
    private readonly Dictionary<object, double> _tall = new();
    private double _measuredWidth = double.NaN;

    // Where each row starts; _tops[Count] is where the last one ends.
    private double[] _tops = [0];

    private Rect _viewport;
    private bool _hasViewport;
    private int _first;
    private int _last = -1;
    private int _scrollingTo = -1;
    private bool _inLayout;

    public ShelfRowPanel()
    {
        EffectiveViewportChanged += OnEffectiveViewportChanged;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = Items.Count;
        if (ItemContainerGenerator is null || count == 0)
        {
            RecycleAll();
            _tall.Clear();
            _tops = [0];
            return default;
        }

        _inLayout = true;
        try
        {
            var width = availableSize.Width;
            if (width != _measuredWidth)
            {
                _kindHeights.Clear();
                _tall.Clear();
                _measuredWidth = width;
            }

            ForgetDeparted();
            MeasureEveryKind(width, count);

            // Again only if the rows came back a different height from the
            // one the window was worked out with - a taller row coming into
            // view, or growing as its album opens.
            for (var pass = 0; pass < 3; pass++)
            {
                LayOut(count);
                (_first, _last) = WindowFor(count, availableSize.Height);
                RecycleWhere(keep: r => (r.Index >= _first && r.Index <= _last) || _tall.ContainsKey(Items[r.Index]!));
                for (var index = _first; index <= _last; index++)
                {
                    if (RowAt(index) == null)
                        Realize(index);
                }
                foreach (var index in TallIndices(count))
                {
                    if (RowAt(index) == null)
                        Realize(index);
                }
                if (_scrollingTo >= 0 && _scrollingTo < count && RowAt(_scrollingTo) == null)
                    Realize(_scrollingTo);

                foreach (var row in Rows)
                    row.Container.Measure(new Size(width, double.PositiveInfinity));

                if (!NoteHeights())
                    break;
            }

            LayOut(count);
            return new Size(double.IsInfinity(width) ? 0 : width, _tops[count]);
        }
        finally
        {
            _inLayout = false;
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var row in Rows)
        {
            if (row.Index + 1 < _tops.Length)
                row.Container.Arrange(new Rect(0, _tops[row.Index], finalSize.Width, _tops[row.Index + 1] - _tops[row.Index]));
        }
        return finalSize;
    }

    private static Type KindOf(object? item) => item?.GetType() ?? typeof(object);

    private double HeightOf(object? item) =>
        item != null && _tall.TryGetValue(item, out var tall) ? tall
        : _kindHeights.TryGetValue(KindOf(item), out var height) ? height
        : 0;

    private void LayOut(int count)
    {
        if (_tops.Length != count + 1)
            _tops = new double[count + 1];
        var top = 0.0;
        for (var i = 0; i < count; i++)
        {
            _tops[i] = top;
            top += HeightOf(Items[i]);
        }
        _tops[count] = top;
    }

    // A kind not yet measured at this width: its first row is realized and
    // measured, whether or not it is on screen - the window cannot be worked
    // out until every row above it has a height.
    private void MeasureEveryKind(double width, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var kind = KindOf(Items[i]);
            if (_kindHeights.ContainsKey(kind))
                continue;
            var row = RowAt(i) ?? Realize(i);
            row.Container.Measure(new Size(width, double.PositiveInfinity));
            _kindHeights[kind] = Math.Max(1, row.Container.DesiredSize.Height);
        }
    }

    // Each kind's height lowered to its shortest realized row, and every
    // realized row taller than its kind remembered - or forgotten once it no
    // longer is. True if anything moved.
    private bool NoteHeights()
    {
        var changed = false;
        foreach (var row in Rows)
        {
            var kind = KindOf(Items[row.Index]);
            var height = Math.Max(1, row.Container.DesiredSize.Height);
            if (!_kindHeights.TryGetValue(kind, out var kindHeight) || height < kindHeight - 0.01)
            {
                _kindHeights[kind] = height;
                changed = true;
            }
        }

        foreach (var row in Rows)
        {
            var item = Items[row.Index]!;
            var height = Math.Max(1, row.Container.DesiredSize.Height);
            if (height > _kindHeights[KindOf(item)] + 0.5)
            {
                if (!_tall.TryGetValue(item, out var was) || Math.Abs(was - height) >= 0.01)
                {
                    _tall[item] = height;
                    changed = true;
                }
            }
            else if (_tall.Remove(item))
            {
                changed = true;
            }
        }
        return changed;
    }

    private IEnumerable<int> TallIndices(int count)
    {
        if (_tall.Count == 0)
            yield break;
        for (var i = 0; i < count; i++)
        {
            if (_tall.ContainsKey(Items[i]!))
                yield return i;
        }
    }

    // A taller row whose item has left the list adds nothing.
    private void ForgetDeparted()
    {
        if (_tall.Count == 0)
            return;
        var present = new HashSet<object>(Items.Where(i => i != null)!);
        foreach (var item in _tall.Keys.Where(k => !present.Contains(k)).ToList())
            _tall.Remove(item);
    }

    // The rows the window covers, plus the buffer. Before the panel has been
    // told where it is being looked at, the top of it, as far down as it has
    // room for; once it has, nothing at all if it is entirely out of view.
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

        if (bottom <= 0 || top >= _tops[count] || bottom <= top)
            return (0, -1);

        var first = Math.Max(0, IndexAt(top, count) - BufferRows);
        var last = Math.Min(count - 1, IndexAt(Math.Max(top, bottom - 0.01), count) + BufferRows);
        return (first, last);
    }

    // The row `y` falls in.
    private int IndexAt(double y, int count)
    {
        int lo = 0, hi = count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (_tops[mid] <= y)
                lo = mid;
            else
                hi = mid - 1;
        }
        return Math.Max(0, lo);
    }

    private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        _viewport = e.EffectiveViewport;
        _hasViewport = true;
        _scrollingTo = -1;

        // Only a change in which rows are showing needs a measure; the rows
        // themselves do not move inside the panel as it scrolls.
        if (_inLayout || _tops.Length != Items.Count + 1)
            return;
        var (first, last) = WindowFor(Items.Count, Bounds.Height);
        if (first != _first || last != _last)
            InvalidateMeasure();
    }

    // A spare comes back with the size it last measured to, and a change deep
    // inside it as it is handed its new item - an album card opening for an
    // open album's row - leaves only that control needing a measure: its
    // ancestors still hold their old sizes, so measuring the row here would
    // stop at the top and give the size of whatever the spare last showed,
    // until a later layout pass got round to the card. Measured as that, the
    // open album's row was taken for closed, forgotten, and never built again
    // while out of view. So everything between such a control and the row is
    // marked as needing a measure too, and the row's measure reaches it.
    private static void InvalidateDownTo(Control container)
    {
        foreach (var descendant in container.GetVisualDescendants().OfType<Layoutable>())
        {
            if (descendant.IsMeasureValid)
                continue;
            for (var at = descendant.GetVisualParent<Layoutable>(); at != null; at = at.GetVisualParent<Layoutable>())
            {
                at.InvalidateMeasure();
                if (ReferenceEquals(at, container))
                    break;
            }
        }
    }

    protected override void OnContainerReused(Control container) => InvalidateDownTo(container);

    private void RecycleAll() => RecycleWhere(keep: _ => false);

    // Home hands the list a new collection on every rebuild; anything else is
    // treated the same way. Every row goes back to the spares and the next
    // measure lays the list out again, the taller rows still known by item.
    protected override void OnItemsChanged(IReadOnlyList<object?> items, NotifyCollectionChangedEventArgs e)
    {
        base.OnItemsChanged(items, e);
        RecycleAll();
        InvalidateMeasure();
    }

    protected override void OnItemsControlChanged(ItemsControl? oldValue)
    {
        base.OnItemsControlChanged(oldValue);
        _tall.Clear();
        _kindHeights.Clear();
        _measuredWidth = double.NaN;
        _tops = [0];
    }

    protected override Control? ScrollIntoView(int index)
    {
        if (_inLayout || index < 0 || index >= Items.Count)
            return null;
        if (_tops.Length != Items.Count + 1)
            UpdateLayout();
        if (_tops.Length != Items.Count + 1)
            return null;

        _scrollingTo = index;
        InvalidateMeasure();
        this.BringIntoView(new Rect(0, _tops[index], Bounds.Width, _tops[index + 1] - _tops[index]));
        UpdateLayout();
        return ContainerFromIndex(index);
    }

    // Home's rows are read and clicked, not stepped through with the arrows.
    protected override IInputElement? GetControl(NavigationDirection direction, IInputElement? from, bool wrap) => null;
}
