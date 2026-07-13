using System;
using System.Collections.Generic;

using Avalonia;
using Avalonia.Controls;

using Flower.ViewModels.Mobile;

namespace Flower.Controls;

/// <summary>
/// Virtualized panel for the album art grid (Albums/Recently Added) - same
/// active-set/pooling approach as MusicListPanel, extended to two dimensions
/// (columns computed from the current viewport width, rows virtualized by
/// uniform height) rather than that panel's single-column uniform-height math.
/// Purely layout + an active AlbumTileViewModel set - click/multi-select/drag
/// interaction lives one level up (AlbumGridView/MainView.axaml.cs), same
/// split as MusicListPanel/MusicListView.axaml.cs's Panel_PointerPressed.
/// </summary>
public class AlbumGridPanel : Panel
{
    public const double TileWidth  = 180;
    public const double TileHeight = 180;
    public const double LabelHeight = 60; // album name + artist lines below the art
    public const double Spacing = 16;

    private static double CellWidth  => TileWidth + Spacing;
    private static double CellHeight => TileHeight + LabelHeight + Spacing;

    private IReadOnlyList<AlbumTileViewModel> _items = Array.Empty<AlbumTileViewModel>();
    private double _scrollOffset;
    private double _viewportHeight = 600;
    private double _viewportWidth  = 800;
    private int _columns = 1;
    private HashSet<string> _selectedNames = new();

    // Index of each active child's source item; parallel to Children.
    private readonly List<int> _activeIndex = new();

    public void SetItems(IReadOnlyList<AlbumTileViewModel> items)
    {
        _items = items;
        for (int i = 0; i < _activeIndex.Count; i++) _activeIndex[i] = -1;
        RefreshActiveSet();
        InvalidateMeasure();
    }

    // Drives each visible tile's IsSelected - see MainView.axaml.cs's SubList-
    // equivalent pointer handling on the Albums grid (plain click / Ctrl-toggle
    // / Shift-range), which is the only thing that ever calls this.
    public void SetSelectedNames(IReadOnlyCollection<string> names)
    {
        _selectedNames = new HashSet<string>(names);
        foreach (Control child in Children)
        {
            if (child is AlbumTileControl { DataContext: AlbumTileViewModel tile })
                tile.IsSelected = _selectedNames.Contains(tile.Name);
        }
    }

    public void SetViewport(double scrollOffset, double viewportHeight, double viewportWidth)
    {
        bool changed = Math.Abs(_scrollOffset - scrollOffset) > 0.5
                    || Math.Abs(_viewportHeight - viewportHeight) > 0.5
                    || Math.Abs(_viewportWidth - viewportWidth) > 0.5;
        _scrollOffset   = scrollOffset;
        _viewportHeight = viewportHeight;
        _viewportWidth  = viewportWidth;
        if (!changed)
            return;

        _columns = Math.Max(1, (int)(_viewportWidth / CellWidth));
        RefreshActiveSet();
        InvalidateArrange();
        InvalidateMeasure();
    }

    private int RowCount => _items.Count == 0 ? 0 : (_items.Count + _columns - 1) / _columns;

    // Resolves a point in this panel's own local coordinate space (scroll
    // included - same convention as MusicListPanel.HitTestRow) to whichever
    // tile occupies that cell, if any.
    public AlbumTileViewModel? HitTestTile(Point panelPoint)
    {
        if (_columns <= 0 || _items.Count == 0)
            return null;

        int col = (int)(panelPoint.X / CellWidth);
        int row = (int)(panelPoint.Y / CellHeight);
        if (col < 0 || col >= _columns || row < 0)
            return null;

        int index = row * _columns + col;
        return index >= 0 && index < _items.Count ? _items[index] : null;
    }

    // ── Active-set management ─────────────────────────────────────────────────

    private void RefreshActiveSet()
    {
        var indices = ComputeRenderIndices();

        while (Children.Count < indices.Count)
        {
            int slot = Children.Count;
            var ctrl = new AlbumTileControl();
            BindTile(ctrl, indices[slot]);
            _activeIndex.Add(indices[slot]);
            Children.Add(ctrl);
        }

        for (int slot = 0; slot < Children.Count; slot++)
        {
            if (slot < indices.Count)
            {
                int itemIdx = indices[slot];
                if (_activeIndex[slot] != itemIdx)
                {
                    BindTile((AlbumTileControl)Children[slot], itemIdx);
                    _activeIndex[slot] = itemIdx;
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

    private void BindTile(AlbumTileControl ctrl, int itemIndex)
    {
        var tile = _items[itemIndex];
        tile.IsSelected = _selectedNames.Contains(tile.Name);
        ctrl.DataContext = tile;
    }

    private List<int> ComputeRenderIndices()
    {
        if (_items.Count == 0)
            return [];

        int firstRow = Math.Max(0, (int)Math.Floor(_scrollOffset / CellHeight));
        int rowCount = (int)Math.Ceiling(_viewportHeight / CellHeight) + 2;
        int lastRow  = Math.Min(RowCount, firstRow + rowCount);

        var list = new List<int>();
        for (int row = firstRow; row < lastRow; row++)
        {
            int start = row * _columns;
            int end   = Math.Min(_items.Count, start + _columns);
            for (int i = start; i < end; i++)
                list.Add(i);
        }
        return list;
    }

    // ── Layout ────────────────────────────────────────────────────────────────

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (Control child in Children)
        {
            if (child.IsVisible)
                child.Measure(new Size(TileWidth, TileHeight + LabelHeight));
        }

        return new Size(_viewportWidth, RowCount * CellHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (int slot = 0; slot < Children.Count; slot++)
        {
            if (!Children[slot].IsVisible)
                continue;

            int itemIdx = _activeIndex[slot];
            int row = itemIdx / _columns;
            int col = itemIdx % _columns;
            Children[slot].Arrange(new Rect(col * CellWidth, row * CellHeight, TileWidth, TileHeight + LabelHeight));
        }

        return new Size(finalSize.Width, RowCount * CellHeight);
    }
}
