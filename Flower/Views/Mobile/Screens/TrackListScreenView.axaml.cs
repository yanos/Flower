using System;
using System.Collections.Generic;
using System.ComponentModel;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

using Flower.ViewModels;
using Flower.ViewModels.Mobile;

namespace Flower.Views.Mobile.Screens;

public partial class TrackListScreenView : UserControl, ITrackRowHost
{
    // Touch drag-to-reorder for the playlist track list: the desktop equivalent
    // (MusicListView) starts dragging immediately anywhere on the row with a small
    // 4px threshold, which fights normal touch scrolling. Here dragging only starts
    // from a dedicated handle icon, with a larger threshold before it visually kicks in.
    private const double DragThreshold = 10.0;
    private TrackRowViewModel? _draggedRow;
    private double _dragStartY;
    private bool _isDragging;

    // The rows/header this instance actually renders - deliberately NOT a
    // direct binding to Main.Rows/AlbumDetailRows/CurrentAlbumHeader on the
    // shared MobileMainViewModel (see the XAML's own comment). While this is
    // the CURRENT screen (ObserveLive), these track the live VM exactly as
    // the old direct bindings did. Once demoted to "one back" by
    // ScreenStackPanel (Freeze), they stop tracking anything and hold
    // whatever MobileNavigationFrame.FrozenRows/FrozenHeader captured at the
    // moment this screen was left - the mechanism that lets a kept-alive
    // instance sit there unchanged, ready to be revealed by a swipe, rather
    // than following whatever the live VM now shows (which, since Main.Rows
    // is a single shared collection, could by then be a completely different
    // album/playlist's tracks - see MobileNavigationFrame's own doc comment).
    public static readonly StyledProperty<IReadOnlyList<TrackRowViewModel>> DisplayRowsProperty =
        AvaloniaProperty.Register<TrackListScreenView, IReadOnlyList<TrackRowViewModel>>(
            nameof(DisplayRows), defaultValue: Array.Empty<TrackRowViewModel>());

    public static readonly StyledProperty<AlbumTileViewModel?> DisplayHeaderProperty =
        AvaloniaProperty.Register<TrackListScreenView, AlbumTileViewModel?>(nameof(DisplayHeader));

    public static readonly StyledProperty<bool> IsAlbumModeProperty =
        AvaloniaProperty.Register<TrackListScreenView, bool>(nameof(IsAlbumMode));

    // Same reasoning as IsAlbumMode/DisplayRows above, for TrackRowTemplate's
    // own drag-handle visibility (see that file) - it used to read
    // IsShowingPlaylistTracks straight off the shared DataContext, which is
    // exactly as wrong for a kept-alive one-back/one-forward instance as
    // reading IsShowingAlbumTrackList straight off it was for the row art:
    // briefly showing/hiding based on whatever screen is CURRENTLY live,
    // until the promotion/freeze catches up and it flips back - confirmed on
    // a real device as album art flashing in on every row of a revealed
    // album screen mid swipe-forward, then disappearing once the transition
    // committed and ObserveLive resynced it against the (by-then-correct)
    // live VM.
    public static readonly StyledProperty<bool> IsPlaylistModeProperty =
        AvaloniaProperty.Register<TrackListScreenView, bool>(nameof(IsPlaylistMode));

    // Wide enough to put an album's art beside its track list instead of
    // stacked above it - a phone in landscape, or a tablet either way up. 600
    // clears the widest phone in portrait (~430) with room to spare and sits
    // under the narrowest phone in landscape (~667), so the split is exactly
    // "turned sideways or bigger". Driven off this control's own measured
    // width below rather than a screen/orientation API: the layout only cares
    // how much room it actually got, which is also what makes it testable and
    // what makes a resized desktop window behave sensibly for free.
    private const double WideAlbumLayoutMinWidth = 600;

    public static readonly StyledProperty<bool> IsWideAlbumLayoutProperty =
        AvaloniaProperty.Register<TrackListScreenView, bool>(nameof(IsWideAlbumLayout));

    public bool IsWideAlbumLayout
    {
        get => GetValue(IsWideAlbumLayoutProperty);
        private set => SetValue(IsWideAlbumLayoutProperty, value);
    }

    // Must match the Margin on the pinned art in the XAML, doubled - the art
    // is square and sized off the height left over once its own margin is
    // taken out, so the two numbers have to agree or it overflows the screen
    // by exactly the difference.
    private const double PinnedArtInset = 32;

    // ...and how much of the width the art is allowed to take. A landscape
    // phone is height-bound, so the cap never binds there and the art simply
    // fills the screen top to bottom, which is the point. It exists for the
    // shape where height is the *larger* dimension - a tablet held upright,
    // or a tall desktop window - where filling the height would leave the
    // songs a column an inch wide.
    private const double PinnedArtMaxWidthFraction = 0.4;

    public static readonly StyledProperty<double> PinnedArtSizeProperty =
        AvaloniaProperty.Register<TrackListScreenView, double>(nameof(PinnedArtSize));

    /// <summary>
    /// The side of the album art pinned beside the track list in the wide
    /// layout. Computed here rather than left to the layout system because
    /// nothing in a Grid will size a square off the height it was given: the
    /// art sits in an Auto column, which measures with unconstrained width.
    /// </summary>
    public double PinnedArtSize
    {
        get => GetValue(PinnedArtSizeProperty);
        private set => SetValue(PinnedArtSizeProperty, value);
    }

    public IReadOnlyList<TrackRowViewModel> DisplayRows
    {
        get => GetValue(DisplayRowsProperty);
        private set => SetValue(DisplayRowsProperty, value);
    }

    public AlbumTileViewModel? DisplayHeader
    {
        get => GetValue(DisplayHeaderProperty);
        private set => SetValue(DisplayHeaderProperty, value);
    }

    public bool IsAlbumMode
    {
        get => GetValue(IsAlbumModeProperty);
        private set => SetValue(IsAlbumModeProperty, value);
    }

    public bool IsPlaylistMode
    {
        get => GetValue(IsPlaylistModeProperty);
        private set => SetValue(IsPlaylistModeProperty, value);
    }

    public static readonly StyledProperty<bool> ShowsRowArtistProperty =
        AvaloniaProperty.Register<TrackListScreenView, bool>(nameof(ShowsRowArtist), defaultValue: true);

    /// <summary>
    /// Whether a track row should print its artist under the title - false
    /// when the whole list shares one artist (see ITrackRowHost). Derived
    /// from DisplayRows below rather than from the live ViewModel, so a
    /// frozen one-back screen answers for the rows it is actually showing.
    /// </summary>
    public bool ShowsRowArtist
    {
        get => GetValue(ShowsRowArtistProperty);
        private set => SetValue(ShowsRowArtistProperty, value);
    }

    // One row is not a repetition, so there is nothing to strip: a
    // single-track playlist would otherwise lose its only mention of the
    // artist. Two identical lines is where it starts reading as noise.
    private static bool RowArtistIsWorthShowing(IReadOnlyList<TrackRowViewModel> rows)
    {
        if (rows.Count < 2)
            return true;

        var first = rows[0].Track.Artists;
        for (int i = 1; i < rows.Count; i++)
        {
            if (!string.Equals(rows[i].Track.Artists, first, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == BoundsProperty)
        {
            IsWideAlbumLayout = Bounds.Width >= WideAlbumLayoutMinWidth;
            PinnedArtSize = Math.Max(0, Math.Min(
                Bounds.Height - PinnedArtInset,
                Bounds.Width * PinnedArtMaxWidthFraction));
        }
        else if (change.Property == DisplayRowsProperty)
        {
            ShowsRowArtist = RowArtistIsWorthShowing(DisplayRows);
        }
    }

    private MobileMainViewModel? _observedVm;

    public TrackListScreenView()
    {
        InitializeComponent();

        // TrackRowTemplate's drag handle can't wire these via XAML event
        // attributes (it's a class-less ResourceDictionary - see the
        // template's own comment), so they're attached here instead, tunnel
        // routed off the ListBox itself and keyed off e.Source - the same
        // technique MobileMainView.axaml.cs already uses for its swipe gesture.
        TrackListBox.AddHandler(PointerPressedEvent, DragHandle_PointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        TrackListBox.AddHandler(PointerMovedEvent, DragHandle_PointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        TrackListBox.AddHandler(PointerReleasedEvent, DragHandle_PointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        TrackListBox.AddHandler(PointerCaptureLostEvent, DragHandle_PointerCaptureLost, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    // Called by ScreenStackPanel whenever this instance is the CURRENT
    // screen - subscribes to the shared VM so DisplayRows/DisplayHeader/
    // IsAlbumMode keep following it live, exactly like the old direct
    // bindings did (a rescan/download completing while sitting on this
    // screen still needs to show up immediately, not just at the next
    // navigation change).
    public void ObserveLive(MobileMainViewModel vm)
    {
        if (ReferenceEquals(_observedVm, vm))
        {
            RefreshFromLive();
            return;
        }

        Detach();
        _observedVm = vm;
        vm.PropertyChanged += OnObservedVmChanged;
        vm.Main.PropertyChanged += OnObservedVmChanged;
        RefreshFromLive();
    }

    // Called by ScreenStackPanel when this instance is demoted to "one
    // back" - stops following the live VM and freezes at exactly what the
    // frame captured on the way out. See this class's own DisplayRows doc
    // comment for why.
    public void Freeze(MobileNavigationFrame frame)
    {
        Detach();
        DisplayRows = frame.FrozenRows ?? Array.Empty<TrackRowViewModel>();
        DisplayHeader = frame.FrozenHeader;
        IsAlbumMode = frame.IsAlbumTrackList;
        IsPlaylistMode = frame.IsPlaylistTrackList;
    }

    // ScreenControlFactory calls this when evicting a cached instance from
    // its LRU, so a control nobody references anymore doesn't leak a live
    // subscription to the VM.
    public void Detach()
    {
        if (_observedVm == null)
            return;
        _observedVm.PropertyChanged -= OnObservedVmChanged;
        _observedVm.Main.PropertyChanged -= OnObservedVmChanged;
        _observedVm = null;
    }

    private void OnObservedVmChanged(object? sender, PropertyChangedEventArgs e) => RefreshFromLive();

    private void RefreshFromLive()
    {
        if (_observedVm == null)
            return;
        IsAlbumMode = _observedVm.IsShowingAlbumTrackList;
        IsPlaylistMode = _observedVm.IsShowingPlaylistTracks;
        DisplayRows = IsAlbumMode ? _observedVm.AlbumDetailRows : _observedVm.Main.Rows;
        DisplayHeader = _observedVm.CurrentAlbumHeader;
    }

    private void DragHandle_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Border { Classes: { } classes } handle || !classes.Contains("dragHandle"))
            return;
        if (handle.DataContext is not TrackRowViewModel row)
            return;
        if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed)
            return;

        _draggedRow = row;
        _dragStartY = e.GetPosition(TrackListBox).Y;
        _isDragging = false;
        e.Pointer.Capture(handle);
        e.Handled = true;
    }

    private void DragHandle_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_draggedRow == null)
            return;
        var y = e.GetPosition(TrackListBox).Y;

        if (!_isDragging)
        {
            if (Math.Abs(y - _dragStartY) < DragThreshold)
                return;
            _isDragging = true;
            DropIndicator.IsVisible = true;
        }

        int index = InsertionIndexAt(y);
        DropIndicator.Margin = new Thickness(0, IndicatorOffsetFor(index), 0, 0);
    }

    private void DragHandle_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isDragging && _draggedRow != null && DataContext is MobileMainViewModel vm)
        {
            int index = InsertionIndexAt(e.GetPosition(TrackListBox).Y);
            var insertBefore = TrackListBox.ContainerFromIndex(index)?.DataContext as TrackRowViewModel;
            if (insertBefore != _draggedRow)
                vm.ReorderCurrentPlaylistTrack(_draggedRow.Track, insertBefore?.Track);
        }
        e.Pointer.Capture(null);
        EndDrag();
    }

    private void DragHandle_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndDrag();

    private void EndDrag()
    {
        _draggedRow = null;
        _isDragging = false;
        DropIndicator.IsVisible = false;
    }

    // Hit-tests realized row containers directly rather than assuming a fixed row
    // height, since mobile rows (unlike desktop's uniform MusicListView) size to content.
    private int InsertionIndexAt(double listY)
    {
        int count = TrackListBox.ItemCount;
        for (int i = 0; i < count; i++)
        {
            if (TrackListBox.ContainerFromIndex(i) is not Control container)
                continue;
            var top = container.TranslatePoint(new Point(0, 0), TrackListBox)?.Y ?? 0;
            if (listY < top + container.Bounds.Height / 2)
                return i;
        }
        return count;
    }

    private double IndicatorOffsetFor(int index)
    {
        var container = TrackListBox.ContainerFromIndex(index)
            ?? (index > 0 ? TrackListBox.ContainerFromIndex(index - 1) : null);
        if (container == null)
            return 0;

        var topLeft = container.TranslatePoint(new Point(0, 0), this) ?? default;
        return index >= TrackListBox.ItemCount ? topLeft.Y + container.Bounds.Height : topLeft.Y;
    }
}
