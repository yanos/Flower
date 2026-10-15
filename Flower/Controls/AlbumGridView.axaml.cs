using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
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
    // The narrowest a tile gets, its 8px margins either side included: the
    // grid fits as many of these across as the width allows, and the tiles
    // then share the width evenly (AlbumGridRowControl's TileRowPanel), so the
    // columns fill the page the way the phone's do rather than leaving what
    // is over at the right.
    private const double CellWidth = 180 + 16;

    // Room left between an opened album's songs and the foot of the page -
    // the player bar, when the playback controls are at the bottom - once the
    // grid has scrolled them into view.
    private const double RevealGap = 24;

    // The reveal's scroll runs as long as the expansion's own Height
    // transition (AlbumGridRowControl.axaml), with the same easing, so a row
    // closing above the one opening shrinks exactly as fast as the scroll
    // makes up for it.
    private static readonly TimeSpan RevealDuration = TimeSpan.FromMilliseconds(220);

    // How much longer the reveal keeps after its target once its own time is
    // up: the expansion may finish a frame after it, and until it does the
    // scroller cannot reach an offset past the end of what it holds.
    private static readonly TimeSpan RevealSettle = TimeSpan.FromMilliseconds(150);

    private IDisposable? _reveal;

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
    // of ExpandedKey below.
    public static readonly StyledProperty<IEnumerable?> SelectedNamesProperty =
        AvaloniaProperty.Register<AlbumGridView, IEnumerable?>(nameof(SelectedNames));

    public IEnumerable? SelectedNames
    {
        get => GetValue(SelectedNamesProperty);
        set => SetValue(SelectedNamesProperty, value);
    }

    // The one tile (if any) currently expanded inline - see
    // MainViewModel.ExpandedAlbumKey.
    //
    // A tile, not an album name - see AlbumTileKey.
    public static readonly StyledProperty<AlbumTileKey?> ExpandedKeyProperty =
        AvaloniaProperty.Register<AlbumGridView, AlbumTileKey?>(nameof(ExpandedKey));

    public AlbumTileKey? ExpandedKey
    {
        get => GetValue(ExpandedKeyProperty);
        set => SetValue(ExpandedKeyProperty, value);
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
    // IsCurrentlyPlaying) - independent of ExpandedKey/SelectedNames above,
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
        // Scrolling by hand mid-reveal takes over from it rather than being
        // pulled back to where the reveal was going.
        Scroller.AddHandler(PointerWheelChangedEvent, (_, _) => StopReveal(), RoutingStrategies.Tunnel);
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
        else if (change.Property == ExpandedKeyProperty)
        {
            // The row closing, if another was open - found before ApplyExpansion
            // marks it closed: the reveal has to allow for it if it sits above
            // the one opening.
            var closing = _rows.FindIndex(r => r.IsExpanded);

            ApplyExpansion();

            // Only on an album being opened - never on a rebuild, which
            // re-applies the same expansion to new rows and must leave the
            // scroll where the user put it. Posted, because the expanded
            // album's songs (ExpandedTracks) arrive just after its key.
            if (ExpandedKey != null)
                Dispatcher.UIThread.Post(() => RevealExpanded(closing));
            else
                StopReveal();
        }
        else if (change.Property == ExpandedTracksProperty)
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
        var columns = Math.Max(1, (int)(width / CellWidth));

        // The same tiles at the same width make the same rows, so the ones
        // there stay. A rebuild of the page hands the grid a new collection
        // for every change that reaches it - selecting a tile, which its
        // "..." does, is one - and AlbumTileMerge keeps the instances, so
        // most of those are this. New rows would cost more than the work:
        // the panel forgets how tall an open album's row is when its rows are
        // replaced, so with that row above the window everything below it
        // jumped up by the album's height until it was scrolled back to.
        if (columns == _columns && _rows.Count > 0 && RowsHold(_tiles))
            return;
        _columns = columns;

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
        // the page header correctly says 0 songs while the grid still shows
        // every album) until any resize happens to force a fresh layout pass.
        // A different instance leaves nothing to recognise, so the panel
        // rebuilds - which is all the null-then-reassign was ever trying to
        // ask for.
        var rows = new List<AlbumGridRowViewModel>();
        for (int i = 0; i < _tiles.Count; i += _columns)
            rows.Add(new AlbumGridRowViewModel { Tiles = _tiles.Skip(i).Take(_columns).ToList(), Columns = _columns });

        _rows = rows;
        RowsList.ItemsSource = _rows;

        ApplySelection();
        ApplyExpansion();
        ApplyPlayingIndicator();
    }

    private bool RowsHold(IReadOnlyList<AlbumTileViewModel> tiles)
    {
        var at = 0;
        foreach (var row in _rows)
        {
            foreach (var tile in row.Tiles)
            {
                if (at >= tiles.Count || !ReferenceEquals(tiles[at], tile))
                    return false;
                at++;
            }
        }
        return at == tiles.Count;
    }

    private void ApplySelection()
    {
        var selected = new HashSet<string>(SelectedNames?.Cast<string>() ?? Enumerable.Empty<string>());
        foreach (var tile in _tiles)
            tile.IsSelected = selected.Contains(tile.Name);
    }

    private void ApplyExpansion()
    {
        var expandedKey = ExpandedKey;
        var tracks = ExpandedTracks?.Cast<Track>().ToList() ?? new List<Track>();

        foreach (var tile in _tiles)
            tile.IsExpanded = expandedKey != null && tile.Key == expandedKey;

        foreach (var row in _rows)
        {
            var isMatch = expandedKey != null && row.Tiles.Any(t => t.Key == expandedKey);
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
    // tiles are now real, individually-templated controls (a virtualizing
    // list of rows) rather than manually arranged by a custom Panel.
    public AlbumTileViewModel? HitTestTile(object? eventSource) =>
        (eventSource as Visual)?.FindAncestorOfType<AlbumTileControl>(includeSelf: true)?.DataContext as AlbumTileViewModel;

    // The song(s) selected (click/Ctrl/Shift/arrow-keys - see
    // AlbumGridRowViewModel.SelectTrack/MoveSelection) within whichever row
    // is currently expanded, if any - for Cmd/Ctrl+I (MainView.axaml.cs's
    // OpenTrackInfoForSelectedAlbums), which otherwise has no way to know
    // about this song-level selection at all and would fall back to treating
    // the whole expanded album as selected instead of just the one song the
    // user actually clicked. At most one row across either grid instance is
    // ever expanded at a time (MainViewModel.ExpandedAlbumKey is shared).
    public IReadOnlyList<Track> GetExpandedRowSelectedTracks() =>
        _rows.FirstOrDefault(r => r.IsExpanded)?.SelectedTracks ?? Array.Empty<Track>();

    // Per-view scroll position, persisted across launches - see
    // MainView.axaml.cs's own GetScrollOffsetYForKey/SetScrollOffsetYForKey
    // and MainViewModel.SaveLastView/ResolveLastSidebarItem. Same shape as
    // MusicListView.GetScrollOffsetY/SetScrollOffsetY, just wrapping this
    // control's own Scroller instead.
    public double GetScrollOffsetY() => Scroller.Offset.Y;

    // Brings the row holding this tile into view - an album opened from Home
    // (see MainViewModel.ShowAlbumAsync).
    public void ScrollToTile(AlbumTileKey key)
    {
        var index = _rows.FindIndex(r => r.Tiles.Any(t => t.Key == key));
        if (index < 0)
            return;
        RowsList.ScrollIntoView(index);
        RevealExpanded(-1);
    }

    // Scrolls an album just opened so its songs show whole, with RevealGap to
    // spare under them - but never so far that its cover leaves the top of
    // the page: an album with more songs than the page holds stops with its
    // cover at the top and the rest a scroll away. Nothing moves when the
    // songs already fit where they are, and a cover partly above the top is
    // brought down into view.
    //
    // Worked out from where the songs will end rather than waiting for them
    // to finish opening (AlbumGridRowControl.ExpandedBottom), so the scroll
    // runs alongside the expansion. A row closing above the one opening
    // takes what it still holds away from everything under it as it goes,
    // and the scroll gives that back over the same time with the same easing
    // - the tile clicked holds still instead of sliding up under the pointer.
    // What it still holds is read here, not when it was told to close: its
    // transition may already have run a frame, and the row opening has been
    // laid out against that frame's height.
    private void RevealExpanded(int closing)
    {
        StopReveal();

        var index = _rows.FindIndex(r => r.IsExpanded);
        if (RowControlAt(index) is not { } row || row.TranslatePoint(default, Scroller) is not { } at)
            return;

        // Where the row's top will be in the page once the row closing above
        // it, if any, has closed; where the page would have to sit for the
        // tile to stay just where it was clicked; and from there, the least
        // scroll that shows the songs whole, but not past the cover.
        var from = Scroller.Offset;
        var shift = closing >= 0 && closing < index ? RowControlAt(closing)?.CurrentExpansionHeight ?? 0 : 0;
        var top = from.Y + at.Y - shift;
        var bottom = top + row.ExpandedBottom + RevealGap;
        var held = top - at.Y;
        var target = Math.Max(0, Math.Min(Math.Max(held, bottom - Scroller.Viewport.Height), top));
        var by = target - from.Y;
        if (Math.Abs(by) < 0.5)
            return;

        var to = target;
        IDisposable? reveal = null;
        reveal = AnimationClock.Current.Subscribe(elapsed =>
        {
            var t = Math.Min(1.0, elapsed / RevealDuration);
            Scroller.Offset = new Vector(from.X, from.Y + by * EaseOut(t));
            if (t < 1.0 || (Math.Abs(Scroller.Offset.Y - to) >= 0.5 && elapsed < RevealDuration + RevealSettle))
                return;
            reveal!.Dispose();
            if (ReferenceEquals(_reveal, reveal))
                _reveal = null;
        });
        _reveal = reveal;
    }

    private void StopReveal()
    {
        _reveal?.Dispose();
        _reveal = null;
    }

    // The same curve as the expansion's CubicEaseOut.
    private static double EaseOut(double t) => 1 - Math.Pow(1 - t, 3);

    private AlbumGridRowControl? RowControlAt(int index) =>
        index < 0 ? null : RowsList.ContainerFromIndex(index)?.FindDescendantOfType<AlbumGridRowControl>(includeSelf: true);

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
