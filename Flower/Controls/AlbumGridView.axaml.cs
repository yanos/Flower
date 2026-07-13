using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

using Flower.Models;
using Flower.ViewModels;
using Flower.ViewModels.Mobile;

namespace Flower.Controls;

// Hosts a row-chunked, virtualized album art grid - see AlbumGridRowControl/
// AlbumGridRowViewModel for the actual row+tile+inline-expansion rendering.
// Click/multi-select/drag interaction is handled externally (MainView.axaml.cs),
// via HitTestTile - same split MusicListView keeps with its own panel.
public partial class AlbumGridView : UserControl
{
    // Matches AlbumTileControl's own hardcoded tile width (180) plus the row
    // template's inter-tile Spacing (16) - used only to estimate how many
    // tiles fit per row; not load-bearing for the tiles' own actual layout.
    private const double CellWidth = 180 + 16;

    private IReadOnlyList<AlbumTileViewModel> _tiles = Array.Empty<AlbumTileViewModel>();
    private readonly List<AlbumGridRowViewModel> _rows = new();
    private int _columns = 1;

    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<AlbumGridView, IEnumerable?>(nameof(ItemsSource));

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    // Album names currently selected (see MainViewModel.SelectedSubItems) -
    // drives which tiles render as selected (drag/multi-select), independent
    // of ExpandedName below.
    public static readonly StyledProperty<IEnumerable?> SelectedNamesProperty =
        AvaloniaProperty.Register<AlbumGridView, IEnumerable?>(nameof(SelectedNames));

    public IEnumerable? SelectedNames
    {
        get => GetValue(SelectedNamesProperty);
        set => SetValue(SelectedNamesProperty, value);
    }

    // The one album (if any) currently expanded inline - see
    // MainViewModel.ExpandedAlbumName.
    public static readonly StyledProperty<string?> ExpandedNameProperty =
        AvaloniaProperty.Register<AlbumGridView, string?>(nameof(ExpandedName));

    public string? ExpandedName
    {
        get => GetValue(ExpandedNameProperty);
        set => SetValue(ExpandedNameProperty, value);
    }

    public static readonly StyledProperty<IEnumerable?> ExpandedTracksProperty =
        AvaloniaProperty.Register<AlbumGridView, IEnumerable?>(nameof(ExpandedTracks));

    public IEnumerable? ExpandedTracks
    {
        get => GetValue(ExpandedTracksProperty);
        set => SetValue(ExpandedTracksProperty, value);
    }

    public AlbumGridView()
    {
        InitializeComponent();
        RowsList.ItemsSource = _rows;
        SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 1)
                RebuildRows();
        };
    }

    private INotifyCollectionChanged? _observedItemsSource;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ItemsSourceProperty)
        {
            if (_observedItemsSource != null)
                _observedItemsSource.CollectionChanged -= OnItemsSourceCollectionChanged;
            _observedItemsSource = ItemsSource as INotifyCollectionChanged;
            if (_observedItemsSource != null)
                _observedItemsSource.CollectionChanged += OnItemsSourceCollectionChanged;

            _tiles = ItemsSource?.Cast<AlbumTileViewModel>().ToList() ?? new List<AlbumTileViewModel>();
            RebuildRows();
        }
        else if (change.Property == SelectedNamesProperty)
        {
            ApplySelection();
        }
        else if (change.Property == ExpandedNameProperty || change.Property == ExpandedTracksProperty)
        {
            ApplyExpansion();
        }
    }

    private void OnItemsSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _tiles = ItemsSource?.Cast<AlbumTileViewModel>().ToList() ?? new List<AlbumTileViewModel>();
        RebuildRows();
    }

    // Recomputes tiles-per-row from the current width and re-chunks _tiles
    // into fresh AlbumGridRowViewModel wrappers - the tiles themselves are
    // the same instances either way (same cache/art), only which row wraps
    // which tiles changes. Selection/expansion are re-applied afterward since
    // a rebuild produces brand new row objects.
    private void RebuildRows()
    {
        var width = Bounds.Width > 0 ? Bounds.Width : 800; // reasonable default before first layout
        _columns = Math.Max(1, (int)(width / CellWidth));

        _rows.Clear();
        for (int i = 0; i < _tiles.Count; i += _columns)
            _rows.Add(new AlbumGridRowViewModel { Tiles = _tiles.Skip(i).Take(_columns).ToList() });

        RowsList.ItemsSource = null;
        RowsList.ItemsSource = _rows;

        ApplySelection();
        ApplyExpansion();
    }

    private void ApplySelection()
    {
        var selected = new HashSet<string>(SelectedNames?.Cast<string>() ?? Enumerable.Empty<string>());
        foreach (var tile in _tiles)
            tile.IsSelected = selected.Contains(tile.Name);
    }

    private void ApplyExpansion()
    {
        var expandedName = ExpandedName;
        var tracks = ExpandedTracks?.Cast<Track>().ToList() ?? new List<Track>();

        foreach (var tile in _tiles)
            tile.IsExpanded = expandedName != null && tile.Name == expandedName;

        foreach (var row in _rows)
        {
            var isMatch = expandedName != null && row.Tiles.Any(t => t.Name == expandedName);
            row.IsExpanded = isMatch;
            row.ExpandedTracks = isMatch ? tracks : Array.Empty<Track>();
        }
    }

    // Resolves whichever AlbumTileControl (if any) a raw pointer event's
    // original source landed on, by walking up the visual tree - used by
    // MainView.axaml.cs's pointer handlers instead of coordinate math, since
    // tiles are now real, individually-templated controls (VirtualizingStackPanel
    // of rows) rather than manually arranged by a custom Panel.
    public AlbumTileViewModel? HitTestTile(object? eventSource) =>
        (eventSource as Visual)?.FindAncestorOfType<AlbumTileControl>(includeSelf: true)?.DataContext as AlbumTileViewModel;
}
