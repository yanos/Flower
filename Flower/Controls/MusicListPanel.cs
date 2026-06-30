using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Flower.ViewModels;

namespace Flower.Controls;

/// <summary>
/// Virtualized panel for the track list.  Only creates TrackRowControl instances for the
/// visible viewport plus a small overdraw buffer.  Album-group leaders whose art spans into
/// the visible range are always kept in the rendered set.
/// </summary>
public class MusicListPanel : Panel
{
    private IReadOnlyList<TrackRowViewModel> _items = Array.Empty<TrackRowViewModel>();
    private double _scrollOffset;
    private double _viewportHeight = 600; // reasonable default before first layout

    // Index of each active child's source row; parallel to Children.
    private readonly List<int> _activeIndex = new();

    public void SetItems(IReadOnlyList<TrackRowViewModel> items)
    {
        _items = items;
        // The new list may reuse the same indices as the old one (e.g. switching
        // albums while scrolled near the top), so force every active slot to
        // re-bind its DataContext rather than relying on the index comparison.
        for (int i = 0; i < _activeIndex.Count; i++) _activeIndex[i] = -1;
        RefreshActiveSet();
        InvalidateMeasure();
    }

    public void SetViewport(double scrollOffset, double viewportHeight)
    {
        bool changed = Math.Abs(_scrollOffset - scrollOffset) > 0.5
                    || Math.Abs(_viewportHeight - viewportHeight) > 0.5;
        _scrollOffset  = scrollOffset;
        _viewportHeight = viewportHeight;
        if (changed)
        {
            RefreshActiveSet();
            InvalidateArrange();
        }
    }

    // ── Active-set management ─────────────────────────────────────────────────

    private void RefreshActiveSet()
    {
        var indices = ComputeRenderIndices();

        // Grow the Children pool if needed (never shrink — just hide extras).
        // DataContext must be set BEFORE Children.Add so the compiled binding never sees
        // the inherited MainViewModel DataContext during the brief insertion window.
        while (Children.Count < indices.Count)
        {
            int slot = Children.Count;
            var ctrl = new TrackRowControl();
            ctrl.DataContext = _items[indices[slot]];
            _activeIndex.Add(indices[slot]);
            Children.Add(ctrl);
        }

        for (int slot = 0; slot < Children.Count; slot++)
        {
            if (slot < indices.Count)
            {
                int rowIdx = indices[slot];
                if (_activeIndex[slot] != rowIdx)
                {
                    ((TrackRowControl)Children[slot]).DataContext = _items[rowIdx];
                    _activeIndex[slot] = rowIdx;
                }
                Children[slot].IsVisible = true;
            }
            else
            {
                Children[slot].IsVisible = false;
                _activeIndex[slot] = -1;
            }
        }
    }

    private List<int> ComputeRenderIndices()
    {
        if (_items.Count == 0) return [];

        int first = Math.Max(0, (int)Math.Floor(_scrollOffset / TrackRowViewModel.RowHeight));
        int count = (int)Math.Ceiling(_viewportHeight  / TrackRowViewModel.RowHeight) + 3;
        int last  = Math.Min(_items.Count, first + count);

        var set = new SortedSet<int>();
        for (int i = first; i < last; i++)
        {
            set.Add(i);
            // Ensure the album-group leader is always rendered so its art spans down visually
            if (!_items[i].IsFirstInAlbumGroup)
            {
                int j = i - 1;
                while (j >= 0 && !_items[j].IsFirstInAlbumGroup) j--;
                if (j >= 0) set.Add(j);
            }
        }
        return [.. set];
    }

    // ── Layout ────────────────────────────────────────────────────────────────

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width;

        foreach (Control child in Children)
        {
            if (child.IsVisible)
                child.Measure(new Size(w, TrackRowViewModel.RowHeight));
        }

        return new Size(w, _items.Count * TrackRowViewModel.RowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (int slot = 0; slot < Children.Count; slot++)
        {
            if (!Children[slot].IsVisible) continue;
            double y = _activeIndex[slot] * TrackRowViewModel.RowHeight;
            Children[slot].Arrange(new Rect(0, y, finalSize.Width, TrackRowViewModel.RowHeight));
        }
        return new Size(finalSize.Width, _items.Count * TrackRowViewModel.RowHeight);
    }
}
