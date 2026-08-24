using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

using Flower.Models;
using Flower.Services;
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
    private List<AlbumGridRowViewModel> _rows = new();
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

    // Whichever track is currently playing, if any - see
    // MainViewModel.CurrentlyPlayingTrack. Drives the little "now playing"
    // arrow on the matching row inside an expanded album's track list (see
    // AlbumGridRowViewModel.CurrentlyPlayingTrackId), the same indicator
    // MusicListView's own rows already have (TrackRowViewModel.
    // IsCurrentlyPlaying) - independent of ExpandedName/SelectedNames above,
    // since a track can be playing whether or not its album happens to be
    // the one currently expanded or selected. Identified by Id rather than by
    // Path, which is null for every placeholder and therefore identifies none
    // of them.
    public static readonly StyledProperty<Guid?> CurrentlyPlayingTrackIdProperty =
        AvaloniaProperty.Register<AlbumGridView, Guid?>(nameof(CurrentlyPlayingTrackId));

    public Guid? CurrentlyPlayingTrackId
    {
        get => GetValue(CurrentlyPlayingTrackIdProperty);
        set => SetValue(CurrentlyPlayingTrackIdProperty, value);
    }

    // What counts as playable right now (see TrackAvailability) - bound from
    // MainViewModel.Availability. Only the expanded album's own track rows
    // need it: the tiles carry their own already-computed IsUnavailable,
    // marked wherever they are built.
    public static readonly StyledProperty<TrackAvailabilityContext> AvailabilityProperty =
        AvaloniaProperty.Register<AlbumGridView, TrackAvailabilityContext>(nameof(Availability));

    public TrackAvailabilityContext Availability
    {
        get => GetValue(AvailabilityProperty);
        set => SetValue(AvailabilityProperty, value);
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
        else if (change.Property == CurrentlyPlayingTrackIdProperty)
        {
            ApplyPlayingIndicator();
        }
        else if (change.Property == AvailabilityProperty)
        {
            ApplyAvailability();
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

        // A brand-new list every time, handed over in a single assignment.
        // This used to keep one List for the control's lifetime, refill it in
        // place and then poke the ItemsControl with `ItemsSource = null;
        // ItemsSource = _rows;` to make it notice - a plain List raises no
        // change notifications of its own. That poke does not reliably work:
        // Avalonia resolves a collection to an ItemsSourceView per instance,
        // so handing back the *same* instance can leave the virtualizing panel
        // believing its source never changed, and it keeps the containers it
        // had already realized. Visible as albums that stay on screen after
        // the library behind them is gone (remove the last library folder -
        // the status bar correctly says 0 songs while the grid still shows
        // every album) until any resize happens to force a fresh layout pass.
        // A different instance leaves nothing to recognise, so the panel
        // rebuilds - which is all the null-then-reassign was ever trying to
        // ask for.
        var rows = new List<AlbumGridRowViewModel>();
        for (int i = 0; i < _tiles.Count; i += _columns)
            rows.Add(new AlbumGridRowViewModel { Tiles = _tiles.Skip(i).Take(_columns).ToList() });

        _rows = rows;
        RowsList.ItemsSource = _rows;

        ApplySelection();
        ApplyExpansion();
        ApplyPlayingIndicator();
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
            // Availability first: ExpandedTracks builds the row objects, and
            // each one reads it as it is constructed rather than waiting for a
            // second pass to grey it in.
            row.Availability = Availability;
            row.ExpandedTracks = isMatch ? tracks : Array.Empty<Track>();
        }
    }

    // Same shape (and same near-no-op cost) as ApplyPlayingIndicator above -
    // only the one expanded row has any track rows to re-mark.
    private void ApplyAvailability()
    {
        foreach (var row in _rows)
            row.Availability = Availability;
    }

    // Cheap even though it touches every row - only the one currently
    // expanded row (if any) actually has a non-empty ExpandedTracks/
    // _trackRows to iterate (see ApplyExpansion above), so this is a no-op
    // pass over every other row.
    private void ApplyPlayingIndicator()
    {
        foreach (var row in _rows)
            row.CurrentlyPlayingTrackId = CurrentlyPlayingTrackId;
    }

    // Resolves whichever AlbumTileControl (if any) a raw pointer event's
    // original source landed on, by walking up the visual tree - used by
    // MainView.axaml.cs's pointer handlers instead of coordinate math, since
    // tiles are now real, individually-templated controls (VirtualizingStackPanel
    // of rows) rather than manually arranged by a custom Panel.
    public AlbumTileViewModel? HitTestTile(object? eventSource) =>
        (eventSource as Visual)?.FindAncestorOfType<AlbumTileControl>(includeSelf: true)?.DataContext as AlbumTileViewModel;

    // The song(s) selected (click/Ctrl/Shift/arrow-keys - see
    // AlbumGridRowViewModel.SelectTrack/MoveSelection) within whichever row
    // is currently expanded, if any - for Cmd/Ctrl+I (MainView.axaml.cs's
    // OpenTrackInfoForSelectedAlbums), which otherwise has no way to know
    // about this song-level selection at all and would fall back to treating
    // the whole expanded album as selected instead of just the one song the
    // user actually clicked. At most one row across either grid instance is
    // ever expanded at a time (MainViewModel.ExpandedAlbumName is shared).
    public IReadOnlyList<Track> GetExpandedRowSelectedTracks() =>
        _rows.FirstOrDefault(r => r.IsExpanded)?.SelectedTracks ?? Array.Empty<Track>();

    // Per-view scroll position, persisted across launches - see
    // MainView.axaml.cs's own GetScrollOffsetYForKey/SetScrollOffsetYForKey
    // and MainViewModel.SaveLastView/ResolveLastSidebarItem. Same shape as
    // MusicListView.GetScrollOffsetY/SetScrollOffsetY, just wrapping this
    // control's own Scroller instead.
    public double GetScrollOffsetY() => Scroller.Offset.Y;

    public void SetScrollOffsetY(double y)
    {
        // Force a layout pass first so the ScrollViewer's extent reflects
        // whatever ItemsSource was just assigned, otherwise the offset gets
        // clamped against the stale (pre-update) extent - same reasoning as
        // MusicListView.SetScrollOffsetY.
        Scroller.UpdateLayout();
        Scroller.Offset = new Vector(0, y);
    }
}
