using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

using CommunityToolkit.Mvvm.DependencyInjection;

using Flower.Models;
using Flower.Services;
using Flower.ViewModels;
using Flower.Views;

namespace Flower.Controls;

// One row of tiles (+ an optional expanded track list below them) in
// AlbumGridView's grid - see AlbumGridRowViewModel. Instances are recycled by
// VirtualizingStackPanel as the user scrolls, so DataContext changes
// repeatedly on the same control - the PropertyChanged subscription below is
// swapped, not just added, each time to avoid leaking one per recycle.
public partial class AlbumGridRowControl : UserControl
{
    // Rough per-track-row height - each row is the Panel's Margin (3 top + 3
    // bottom) around the text Grid's own Margin (6 + 6) around FontSize="12"
    // text (~16), i.e. ~34 - see this control's own XAML. Doesn't need to be
    // pixel-perfect: ExpansionBorder's ClipToBounds hides any minor overshoot/
    // undershoot, but it drives the animated target Height so should be close.
    // Slightly overestimates (the last row has no trailing gap to actually
    // account for), which just leaves a little harmless empty space at the
    // bottom rather than clipping anything.
    private const double TrackRowHeight = 34;

    // ExpansionCard's own Padding="12,10" (top+bottom = 20) - added on top of
    // the track rows themselves since it sits *inside* the animated
    // ExpansionBorder, not outside it (see AlbumGridRowControl.axaml).
    private const double ExpansionCardVerticalPadding = 20;

    private AlbumGridRowViewModel? _row;
    private DispatcherTimer? _scrollIntoViewTimer;

    public AlbumGridRowControl()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        // Tunnel, matching MusicListView's own OnKeyDown registration -
        // there's no deeper focusable descendant here to tunnel past (each
        // row Border isn't focusable - see TrackRow_PointerPressed), but
        // matching the established pattern rather than picking Bubble
        // arbitrarily keeps the two implementations easy to compare.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_row != null)
            _row.PropertyChanged -= Row_PropertyChanged;

        _row = DataContext as AlbumGridRowViewModel;

        if (_row != null)
            _row.PropertyChanged += Row_PropertyChanged;

        UpdateExpansionHeight();
    }

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AlbumGridRowViewModel.IsExpanded) or nameof(AlbumGridRowViewModel.ExpandedTracks))
            UpdateExpansionHeight();
    }

    // ExpansionBorder's Height is set here rather than bound in XAML - it
    // needs a concrete pixel target (not "Auto") for the DoubleTransition on
    // Height to actually animate smoothly between two real values, which is
    // the whole point of precomputing it from the (small, bounded) track
    // count instead of just letting the two-column content measure itself.
    private void UpdateExpansionHeight()
    {
        if (_row is not { IsExpanded: true } row)
        {
            ExpansionBorder.Height = 0;
            _scrollIntoViewTimer?.Stop();
            return;
        }

        var rowCount = Math.Max(row.Column1Tracks.Count, row.Column2Tracks.Count);
        ExpansionBorder.Height = Math.Max(rowCount, 1) * TrackRowHeight + ExpansionCardVerticalPadding;

        ScheduleScrollIntoView();
    }

    // Waits out ExpansionBorder's own 220ms Height transition (see this
    // control's own XAML) before scrolling - calling BringIntoView
    // immediately would measure against the pre-animation (near-zero)
    // height, under-scrolling and leaving the newly revealed tracks cut off
    // at the bottom of the viewport instead of fully visible once expanded.
    private void ScheduleScrollIntoView()
    {
        _scrollIntoViewTimer?.Stop();
        _scrollIntoViewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
        _scrollIntoViewTimer.Tick += ScrollIntoViewTimer_Tick;
        _scrollIntoViewTimer.Start();
    }

    private void ScrollIntoViewTimer_Tick(object? sender, EventArgs e)
    {
        _scrollIntoViewTimer?.Stop();
        _scrollIntoViewTimer = null;
        ExpansionBorder.BringIntoView();
    }

    // See ExpandedTrackRowViewModel.IsHovered's own comment for why hover is
    // driven from here rather than a XAML :pointerover Style selector.
    private void TrackRow_PointerEntered(object? sender, PointerEventArgs e)
    {
        if ((sender as Border)?.DataContext is ExpandedTrackRowViewModel row)
            row.IsHovered = true;
    }

    private void TrackRow_PointerExited(object? sender, PointerEventArgs e)
    {
        if ((sender as Border)?.DataContext is ExpandedTrackRowViewModel row)
            row.IsHovered = false;
    }

    // Plain click collapses to just this row; Ctrl/Cmd toggles it within the
    // existing selection; Shift extends a range - see AlbumGridRowViewModel.
    // SelectTrack.
    private void TrackRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        if (sender is not Border border || border.DataContext is not ExpandedTrackRowViewModel row)
            return;

        var toggle = e.KeyModifiers.HasFlag(PlatformShortcuts.Primary);
        var rangeSelect = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        _row?.SelectTrack(row, toggle, rangeSelect);
        // This control itself takes focus, not the clicked row - mirrors
        // MusicListView, which is Focusable at the control level with one
        // central OnKeyDown, not per-row (see this control's own OnKeyDown
        // below). A per-row Border isn't focusable by default the way a
        // ListBoxItem is, so without *some* explicit Focus() call here, focus
        // - and with it, arrow keys - just stayed on whatever had it before a
        // click here, most often the sidebar, silently switching the
        // sidebar's own selected view instead of moving selection.
        Focus();
        // Otherwise bubbles up to AlbumGrid_PointerPressed (MainView.axaml.cs)
        // - harmless today (HitTestTile finds no AlbumTileControl ancestor
        // here and no-ops) but not this row's event to keep propagating.
        e.Handled = true;
    }

    // Mirrors MusicListView.OnKeyDown/MoveSelection - Down/Up step the
    // "current" row by one across the flat top-to-bottom-then-next-column
    // order (see AlbumGridRowViewModel.MoveSelection), Shift extends the
    // range instead of collapsing to one row, and Enter plays the active
    // end of the selection (AlbumGridRowViewModel.CurrentTrack).
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_row is not { } row)
            return;

        switch (e.Key)
        {
            case Key.Down:
                row.MoveSelection(1, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                e.Handled = true;
                break;
            case Key.Up:
                row.MoveSelection(-1, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                e.Handled = true;
                break;
            case Key.Enter:
                if (row.CurrentTrack is { } track)
                    Ioc.Default.GetService<MainViewModel>()?.PlayTrack(track);
                e.Handled = true;
                break;
        }
    }

    private void TrackRow_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Border)?.DataContext is ExpandedTrackRowViewModel row)
            Ioc.Default.GetService<MainViewModel>()?.PlayTrack(row.Track);
    }

    // Get Info / Add To Playlist / Locate File - the same three actions
    // MusicListView's own row context menu offers (see MainView.axaml.cs's
    // _trackMenu), built fresh per-request here instead of sharing that one:
    // it's tightly coupled to MusicList's own SelectedTracks/SelectedTrack,
    // and this row has neither - it's a plain Track from ExpandedTracks.
    // Mirrors MusicList's own Panel_ContextRequested: only collapses to just
    // this row if it wasn't already part of the current selection, so a
    // right-click on an existing multi-selection acts on the whole thing.
    private void TrackRow_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Border border || border.DataContext is not ExpandedTrackRowViewModel row)
            return;
        if (_row is not { } gridRow || Ioc.Default.GetService<MainViewModel>() is not { } vm)
            return;

        if (!row.IsSelected)
            gridRow.SelectTrack(row, toggle: false, rangeSelect: false);

        BuildTrackContextMenu(row.Track, gridRow.SelectedTracks, vm).Open(border);
        e.Handled = true;
    }

    private ContextMenu BuildTrackContextMenu(Track clickedTrack, IReadOnlyList<Track> selectedTracks, MainViewModel vm)
    {
        var getInfoItem = new MenuItem { Header = "Get Info" };
        getInfoItem.Click += (_, _) => OpenTrackInfo(selectedTracks, vm);

        var addToPlaylistItem = new MenuItem { Header = "Add To Playlist" };
        PopulateAddToPlaylistMenu(addToPlaylistItem, selectedTracks, vm);

        // Revealing more than one file at once isn't a coherent single
        // action, so this always targets the row that was actually
        // right-clicked, not the whole multi-selection - same as Get Info/
        // Add To Playlist would with only one track selected.
        var locateFileItem = new MenuItem { Header = "Locate File" };
        locateFileItem.Click += (_, _) => FileLocator.Reveal(clickedTrack.Path);

        var menu = new ContextMenu();
        menu.Items.Add(getInfoItem);
        menu.Items.Add(addToPlaylistItem);
        menu.Items.Add(locateFileItem);
        return menu;
    }

    private static void PopulateAddToPlaylistMenu(MenuItem addToPlaylistItem, IReadOnlyList<Track> tracks, MainViewModel vm)
    {
        var newPlaylistItem = new MenuItem { Header = "New Playlist" };
        newPlaylistItem.Click += async (_, _) => await vm.CreatePlaylistWithTracks(tracks);
        addToPlaylistItem.Items.Add(newPlaylistItem);

        if (vm.Library.Playlists.Count > 0)
        {
            addToPlaylistItem.Items.Add(new Separator());
            foreach (var playlist in vm.Library.Playlists)
            {
                var target = playlist; // capture
                var item = new MenuItem { Header = target.Name };
                item.Click += async (_, _) => await vm.AddTracksToPlaylist(tracks, target);
                addToPlaylistItem.Items.Add(item);
            }
        }
    }

    // Batch mode (no Prev/Next) for a multi-selection, same as MainView.
    // axaml.cs's own OpenTrackInfo; single-track mode otherwise, with Prev/
    // Next navigating through this album's own track list (the row's
    // ExpandedTracks, in disc/track order) - a coherent context to browse,
    // unlike MainView.axaml.cs's OpenTrackInfoForSelectedAlbums (Cmd/Ctrl+I),
    // which has no one specific track to anchor a navigable list on.
    private void OpenTrackInfo(IReadOnlyList<Track> selectedTracks, MainViewModel vm)
    {
        TrackInfoWindow infoWindow;
        if (selectedTracks.Count > 1)
        {
            infoWindow = new TrackInfoWindow(selectedTracks, vm.Library) { ShowInTaskbar = false };
        }
        else
        {
            if (selectedTracks.Count == 0)
                return;
            var track = selectedTracks[0];
            var tracks = _row?.ExpandedTracks.ToList() ?? new List<Track>();
            var index = tracks.IndexOf(track);
            if (index < 0)
                index = 0;
            infoWindow = new TrackInfoWindow(tracks, index, vm.Library) { ShowInTaskbar = false };
        }

        if (TopLevel.GetTopLevel(this) is Window owner)
            infoWindow.Show(owner);
        else
            infoWindow.Show();
    }
}
