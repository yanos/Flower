using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

using CommunityToolkit.Mvvm.DependencyInjection;

using Flower.Controls;
using Flower.Models;
using Flower.Persistence;
using Flower.Services;
using Flower.ViewModels;

using Material.Icons;
using Material.Icons.Avalonia;

namespace Flower.Views;

public partial class MainView : UserControl
{
    private readonly PlaylistControlViewModel _playlistControlViewModel;
    private MainViewModel? _viewModel;
    private readonly PlaylistStore _playlistStore = Ioc.Default.GetService<PlaylistStore>()!;
    private readonly DeviceNicknameStore _deviceNicknameStore = Ioc.Default.GetService<DeviceNicknameStore>()!;

    private ContextMenu _columnMenu = new();
    private ContextMenu _trackMenu  = new();
    private MenuItem    _addToPlaylistItem = new();
    private readonly ContextMenu _sidebarItemMenu = new();
    private SidebarItem? _dropTargetPlaylistItem;

    // Drag album/artist names from SubList onto a sidebar playlist - mirrors
    // MusicListView's own cross-drag fields/gesture (see OnTrackDragMoved/
    // OnTrackDragEnded) but lives here since SubList is a stock ListBox with no
    // hand-rolled panel to raise that event itself.
    private const double SubListDragThreshold = 4.0;
    private string? _subListDragHitItem;               // item under the pointer at press-time
    private IReadOnlyList<string>? _subListDragItems;   // final drag set, resolved at threshold-crossing
    private Point   _subListDragStartPoint;
    private bool    _isSubListDragging;
    private bool    _syncingSubListSelection; // guards against feedback loops between SubList and the VM

    private DispatcherTimer? _spinTimer;
    private RotateTransform? _spinTransform;

    // Per-view (Songs / album / artist / playlist) scroll position + selection memory
    private readonly Dictionary<string, ViewScrollState> _viewStates = new();
    private string? _currentViewKey;

    private readonly record struct ViewScrollState(double ScrollOffsetY, string? SelectedTrackPath);

    public MainView()
    {
        InitializeComponent();
        _playlistControlViewModel = Ioc.Default.GetService<PlaylistControlViewModel>()!;
        DataContextChanged += OnDataContextChanged;

        // Wire MusicListView events
        MusicList.RowActivated    += OnRowActivated;
        MusicList.RowContextMenu  += OnRowContextMenu;
        MusicList.HeaderContextMenu += OnHeaderContextMenu;
        MusicList.SortRequested   += OnSortRequested;
        MusicList.RowReordered    += OnRowReordered;
        MusicList.TrackDragMoved  += OnTrackDragMoved;
        MusicList.TrackDragEnded  += OnTrackDragEnded;

        // Forward Space / Cmd+I (Ctrl+I on Windows/Linux) from inside the list
        MusicList.AddHandler(KeyDownEvent, MusicList_KeyDown, RoutingStrategies.Tunnel);

        SidebarList.ContextRequested += SidebarList_ContextRequested;

        // Drag album/artist names from SubList onto a sidebar playlist. Also
        // fully owns SubList's click-to-select behavior (see SubList_PointerPressed)
        // rather than relying on ListBoxItem's native SelectionMode="Multiple"
        // handling, which doesn't give the plain-click-collapses-the-rest /
        // Ctrl-toggle / Shift-range semantics this needs. Registered on the
        // Tunnel phase - not Bubble - so this runs BEFORE ListBoxItem's own
        // PointerPressed handling and can mark the event Handled to suppress it
        // entirely; a Bubble handler (even with handledEventsToo) would only ever
        // see the press after native selection processing already ran.
        SubList.AddHandler(PointerPressedEvent, SubList_PointerPressed, RoutingStrategies.Tunnel);
        SubList.PointerMoved += SubList_PointerMoved;
        SubList.PointerReleased += SubList_PointerReleased;
        SubList.PointerCaptureLost += SubList_PointerCaptureLost;

        // Same gesture as SubList above (click/Ctrl-toggle/Shift-range select,
        // drag onto a sidebar playlist), retargeted at the tile grids - see
        // AlbumGrid_PointerPressed. Both AlbumGrid and RecentlyAddedGrid share
        // this exact handler set (they're the same gesture over two different
        // tile orderings, not two different features - see that method's own
        // doc comment); neither is a stock control with native selection to
        // suppress, so plain Bubble wiring is enough, no Tunnel trick needed.
        foreach (var grid in new[] { AlbumGrid, RecentlyAddedGrid })
        {
            grid.PointerPressed += AlbumGrid_PointerPressed;
            grid.PointerMoved += AlbumGrid_PointerMoved;
            grid.PointerReleased += AlbumGrid_PointerReleased;
            grid.PointerCaptureLost += AlbumGrid_PointerCaptureLost;
            // AlbumTileControl itself has no pointer handling of its own (see its
            // own doc comment) - right-click bubbles up from whichever tile the
            // pointer landed on the same way the four handlers above do, resolved
            // the same way via HitTestTile.
            grid.ContextRequested += AlbumGrid_ContextRequested;
        }

        // Cmd/Ctrl+, (Settings) must work regardless of which control currently
        // has focus, so it's handled at the MainView root rather than scoped to MusicList.
        AddHandler(KeyDownEvent, MainView_PreviewKeyDown, RoutingStrategies.Tunnel);

        // See MainView_PreviewPointerPressed for why this can't just rely on LostFocus.
        AddHandler(PointerPressedEvent, MainView_PreviewPointerPressed, RoutingStrategies.Tunnel);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.SettingsRequested -= OnSettingsRequested;
            _viewModel.ColumnSelectorRequested -= OnColumnSelectorRequested;
            _viewModel.NavigateToTrackRequested -= OnNavigateToTrackRequested;
            _viewModel.PlaylistConflictRequested -= OnPlaylistConflictRequested;
            _viewModel.PeerApprovalRequested -= OnPeerApprovalRequested;
            _viewModel.RenamePlaylistRequested -= OnRenamePlaylistRequested;
            _viewModel.DeletePlaylistConfirmationRequested -= OnDeletePlaylistConfirmationRequested;
            StopSpinner();
        }

        _viewModel = DataContext as MainViewModel;

        if (_viewModel != null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.SettingsRequested += OnSettingsRequested;
            _viewModel.ColumnSelectorRequested += OnColumnSelectorRequested;
            _viewModel.NavigateToTrackRequested += OnNavigateToTrackRequested;
            _viewModel.PlaylistConflictRequested += OnPlaylistConflictRequested;
            _viewModel.PeerApprovalRequested += OnPeerApprovalRequested;
            _viewModel.RenamePlaylistRequested += OnRenamePlaylistRequested;
            _viewModel.DeletePlaylistConfirmationRequested += OnDeletePlaylistConfirmationRequested;
            BuildColumnMenu();
            if (_viewModel.IsBusy)
                StartSpinner();

            // Reflect the ViewModel's (possibly persisted) sort state immediately -
            // MusicList's own SortColumn/SortAscending fields default to
            // "TrackNumber"/ascending and only update via the PropertyChanged handler
            // below, which won't fire for a value the ViewModel already had before
            // this handler was attached.
            MusicList.UpdateSortIndicators(_viewModel.SortColumn, _viewModel.SortAscending);

            // Seeds _viewStates with whatever scroll position was persisted
            // last session (see MainViewModel.SaveLastView) so the *first*
            // real view-switch detection in ApplyRows below - which, at
            // startup, is the background rescan's own Rows population, not
            // this call (Rows is always still empty this early) - finds it
            // and restores scroll instead of defaulting to 0.
            SeedRestoredViewState();

            // Push initial rows to MusicListView - a no-op in the normal
            // startup case (Rows is empty here; the real initial push happens
            // below, once Rows' own PropertyChanged fires after the
            // background rescan actually populates it), but still needed for
            // the rarer case for this DataContext already having Rows (e.g.
            // MainView re-attaching to an already-initialized ViewModel).
            if (_viewModel.Rows.Count > 0)
                ApplyRows();
        }
    }

    // See OnDataContextChanged's own comment for why this only matters at
    // startup, not on an in-session view switch (ApplyRows' _viewStates
    // save/restore already covers that).
    private void SeedRestoredViewState()
    {
        if (_viewModel is not { WasLastViewRestored: true } vm)
            return;
        _viewStates[vm.CurrentViewKey] = new ViewScrollState(vm.LastScrollOffsetY, null);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsBusy):
                if (_viewModel!.IsBusy)
                    StartSpinner();
                else
                    StopSpinner();
                break;
            case nameof(MainViewModel.Rows):
                ApplyRows();
                break;
            case nameof(MainViewModel.SortColumn):
            case nameof(MainViewModel.SortAscending):
                MusicList.UpdateSortIndicators(_viewModel!.SortColumn, _viewModel.SortAscending);
                break;
            case nameof(MainViewModel.SelectedSubItems):
                SyncSubListSelectionFromViewModel();
                break;
        }
    }

    // Pushes the ViewModel's SelectedSubItems set into SubList's own selection.
    // Guarded so the resulting SelectionChanged (fired by SubList.SelectedItems
    // mutation below) doesn't loop back into SetSelectedSubItems.
    private void SyncSubListSelectionFromViewModel()
    {
        if (_viewModel == null)
            return;
        _syncingSubListSelection = true;
        try
        {
            SubList.SelectedItems!.Clear();
            foreach (var item in _viewModel.SelectedSubItems)
                SubList.SelectedItems!.Add(item);
        }
        finally
        {
            _syncingSubListSelection = false;
        }
    }

    private void SubList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingSubListSelection || _viewModel == null)
            return;
        _viewModel.SetSelectedSubItems(SubList.SelectedItems!.Cast<string>().ToList());
    }

    // ── Album grid: select / multi-select / drag-to-playlist ────────────────────
    // Same gesture and reasoning as SubList's own handlers above - see
    // SubList_PointerPressed's doc comment - just hit-testing AlbumGrid.Panel's
    // tiles (HitTestTile) instead of a ListBoxItem, and resolving the final
    // drag payload through the same MainViewModel.GetTracksForSubListItems
    // both share.

    // Shared by both AlbumGrid and RecentlyAddedGrid - they're the same
    // gesture (see SubList_PointerPressed's doc comment for the base
    // rationale) over two different tile orderings, not two different
    // features. Both instances are wired to this same handler set (see the
    // constructor), and `sender`/pointer capture tell them apart - a plain
    // click on either activates via the same MainViewModel.ToggleAlbumExpandedCommand,
    // which is shared by both grids too (only one album can be expanded at a
    // time, regardless of which grid it was clicked in).

    private string? _albumGridAnchor; // Shift+click range-select anchor
    private string? _albumGridDragHitItem;
    private IReadOnlyList<string>? _albumGridDragItems;
    private Point _albumGridDragStartPoint;
    private bool _isAlbumGridDragging;
    // A plain (unmodified) press on a not-yet-selected tile might still turn
    // into a drag - see AlbumGrid_PointerMoved/Released. Toggling the
    // expansion immediately on press (what this used to do, back when a
    // plain click drilled into a whole separate track-list view rather than
    // expanding in place) hid the grid the instant you pressed down, before
    // a drag gesture ever had a chance to start. Deferred to release, and
    // only actually toggles if no drag occurred.
    private bool _albumGridPendingActivate;

    // The grid ordering to range-select against depends on which of the two
    // instances is mid-gesture - Albums is alphabetical, Recently Added is
    // by-recency (see MainViewModel.AlbumGridTiles/RecentlyAddedGridTiles).
    private List<string> TileNamesFor(AlbumGridView grid) =>
        _viewModel == null
            ? new List<string>()
            : (ReferenceEquals(grid, AlbumGrid) ? _viewModel.AlbumGridTiles : _viewModel.RecentlyAddedGridTiles)
                .Select(t => t.Name).ToList();

    private void AlbumGrid_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not AlbumGridView grid || _viewModel is not { } vm)
            return;
        if (!e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed)
            return;
        var tile = grid.HitTestTile(e.Source);
        if (tile == null)
            return;

        bool shift  = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool toggle = e.KeyModifiers.HasFlag(PlatformShortcuts.Primary);

        // Double-click plays the album and (unlike a plain click's toggle)
        // always leaves it expanded, rather than the two plain clicks the OS
        // reports this as each toggling ToggleAlbumExpandedCommand in turn -
        // which would otherwise expand then immediately re-collapse it.
        if (e.ClickCount >= 2 && !shift && !toggle)
        {
            vm.PlayAlbum(tile.Name);
            e.Handled = true;
            e.Pointer.Capture(null);
            EndAlbumGridDrag();
            return;
        }

        bool alreadySelected = vm.SelectedSubItems.Contains(tile.Name);

        if (shift)
            SelectAlbumGridRange(grid, tile.Name);
        else if (toggle)
            ToggleAlbumGridItem(tile.Name);
        // else: leave selection/view alone for now - AlbumGrid_PointerMoved's
        // drag-threshold check already falls back to just this one tile if
        // it turns out not to be part of the current selection, so nothing
        // here needs to pre-select it for that to work correctly.

        e.Handled = true;

        _albumGridPendingActivate = !shift && !toggle && !alreadySelected;
        _albumGridDragHitItem = tile.Name;
        _albumGridDragStartPoint = e.GetPosition(grid);
        e.Pointer.Capture(grid);
    }

    private void ToggleAlbumGridItem(string name)
    {
        if (_viewModel is not { } vm)
            return;
        var current = vm.SelectedSubItems.ToList();
        if (!current.Remove(name))
            current.Add(name);
        vm.SetSelectedSubItems(current);
        _albumGridAnchor = name;
    }

    private void SelectAlbumGridRange(AlbumGridView grid, string name)
    {
        if (_viewModel is not { } vm)
            return;
        var items = TileNamesFor(grid);
        int anchorIdx = _albumGridAnchor != null ? items.IndexOf(_albumGridAnchor) : -1;
        int clickIdx  = items.IndexOf(name);
        if (anchorIdx < 0)
            anchorIdx = clickIdx;
        int lo = Math.Min(anchorIdx, clickIdx);
        int hi = Math.Max(anchorIdx, clickIdx);

        var range = new List<string>();
        for (int i = lo; i <= hi; i++)
            range.Add(items[i]);
        // Anchor deliberately left untouched so repeated Shift+clicks keep
        // extending/shrinking the range from the same starting point.
        vm.SetSelectedSubItems(range);
    }

    private void AlbumGrid_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not AlbumGridView grid || _albumGridDragHitItem == null)
            return;

        var pt = e.GetPosition(grid);
        if (!_isAlbumGridDragging)
        {
            var dx = pt.X - _albumGridDragStartPoint.X;
            var dy = pt.Y - _albumGridDragStartPoint.Y;
            if (dx * dx + dy * dy < SubListDragThreshold * SubListDragThreshold)
                return;
            _isAlbumGridDragging = true;
            // Selection is final by now (AlbumGrid_PointerPressed above already
            // resolved it for this press).
            _albumGridDragItems = _viewModel?.SelectedSubItems.Contains(_albumGridDragHitItem) == true
                ? _viewModel.SelectedSubItems.ToList()
                : new List<string> { _albumGridDragHitItem };
        }

        ShowDragGhost(grid, pt, DragGhostLabel(_albumGridDragItems!));
        SetSidebarDropHighlight(HitTestSidebarDrop(grid, pt));
    }

    private void AlbumGrid_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not AlbumGridView grid)
            return;

        if (_isAlbumGridDragging && _albumGridDragItems is { } items)
        {
            var target = HitTestSidebarDrop(grid, e.GetPosition(grid));
            var tracks = _viewModel?.GetTracksForSubListItems(items) ?? Enumerable.Empty<Track>();

            if (target?.PlaylistItem?.Playlist is { } playlist)
                _ = _viewModel?.AddTracksToPlaylist(tracks, playlist);
            else if (target?.CreateNew == true)
                _ = _viewModel?.CreatePlaylistWithTracks(tracks);
        }
        else if (_albumGridPendingActivate && _albumGridDragHitItem is { } name)
        {
            // No drag happened - a genuine plain click, now safe to
            // expand/collapse (see _albumGridPendingActivate's doc comment).
            _viewModel?.ToggleAlbumExpandedCommand?.Execute(name);
        }

        e.Pointer.Capture(null);
        EndAlbumGridDrag();
    }

    private void AlbumGrid_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        => EndAlbumGridDrag();

    private void EndAlbumGridDrag()
    {
        _albumGridDragHitItem = null;
        _albumGridDragItems = null;
        _isAlbumGridDragging = false;
        _albumGridPendingActivate = false;
        ResetDragVisuals();
    }

    // Right-click on an album tile's art/name/artist (anywhere in
    // AlbumTileControl, which has no pointer handling of its own - see its own
    // doc comment) - resolved via the same HitTestTile AlbumGrid_PointerPressed
    // uses. Preserves the existing multi-selection if the right-clicked tile is
    // already part of it, otherwise collapses to just this tile - same rule
    // Panel_ContextRequested/TrackRow_ContextRequested already use for the
    // track list, so a right-click can act on a whole multi-selection of albums.
    private void AlbumGrid_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not AlbumGridView grid || _viewModel is not { } vm)
            return;
        var tile = grid.HitTestTile(e.Source);
        if (tile == null)
            return;

        if (!vm.SelectedSubItems.Contains(tile.Name))
            vm.SetSelectedSubItems(new List<string> { tile.Name });

        var tracks = vm.GetTracksForSubListItems(vm.SelectedSubItems).ToList();
        BuildAlbumContextMenu(tracks, tile.Name).Open(grid);
        e.Handled = true;
    }

    // ── Per-view scroll position + selection ────────────────────────────────────

    private void ApplyRows()
    {
        if (_viewModel == null)
            return;

        MusicList.AllowReorder = _viewModel.SelectedSidebarItem?.Kind == SidebarItemKind.Playlist;

        var newKey = _viewModel.CurrentViewKey;

        // Only save/restore on an actual view switch — leave filtering/sorting
        // within the same view untouched.
        if (newKey == _currentViewKey)
        {
            MusicList.SetItems(_viewModel.Rows);
            return;
        }

        // Keyed off the *old* key's own string shape (not the ViewModel's
        // current IsShowingAlbumGrid/IsShowingRecentlyAddedGrid, which by
        // this point already reflect the *new* selection) so the outgoing
        // view's scroll is read from whichever control actually owned it.
        if (_currentViewKey != null)
            _viewStates[_currentViewKey] = new ViewScrollState(GetScrollOffsetYForKey(_currentViewKey), MusicList.SelectedRow?.Track.Path);

        MusicList.SetItems(_viewModel.Rows);

        if (_viewStates.TryGetValue(newKey, out var saved))
        {
            MusicList.SelectedRow = _viewModel.Rows.FirstOrDefault(r => r.Track.Path == saved.SelectedTrackPath);
            RestoreScrollOffsetForKey(newKey, saved.ScrollOffsetY);
        }
        else
        {
            MusicList.SelectedRow = null;
            RestoreScrollOffsetForKey(newKey, 0);
        }

        _currentViewKey = newKey;
    }

    // CurrentViewKey's own prefix (see MainViewModel.CurrentViewKey) already
    // unambiguously says which control owns a given view's scroll - "album:"
    // for AlbumGrid, "recently-added" for RecentlyAddedGrid, anything else
    // (Songs/Artists/a Playlist) for MusicList - so that string, not the
    // ViewModel's current IsShowingAlbumGrid/IsShowingRecentlyAddedGrid
    // (which can already reflect a *different*, newer selection by the time
    // this runs - see ApplyRows above), is what these two switch on.
    private double GetScrollOffsetYForKey(string key) =>
        key.StartsWith("album:", StringComparison.Ordinal) ? AlbumGrid.GetScrollOffsetY()
        : key == "recently-added" ? RecentlyAddedGrid.GetScrollOffsetY()
        : MusicList.GetScrollOffsetY();

    // Deferred a frame for the two grids, not called inline like MusicList's
    // own SetScrollOffsetY - AlbumGridTiles/RecentlyAddedGridTiles (the
    // grids' own ItemsSource) are reassigned a few lines *after* Rows in
    // MainViewModel.RebuildRowsAsync, so at the exact moment this runs
    // (triggered by Rows' own PropertyChanged) the grid's tiles are still the
    // *previous* view's - setting scroll now would just be clobbered once
    // the real ItemsSource change arrives and the grid rebuilds its rows.
    // Posting past that (both are plain, synchronous property assignments
    // within the same RebuildRowsAsync continuation) lets it land after.
    private void RestoreScrollOffsetForKey(string key, double offsetY)
    {
        if (key.StartsWith("album:", StringComparison.Ordinal))
            Dispatcher.UIThread.Post(() => AlbumGrid.SetScrollOffsetY(offsetY));
        else if (key == "recently-added")
            Dispatcher.UIThread.Post(() => RecentlyAddedGrid.SetScrollOffsetY(offsetY));
        else
            MusicList.SetScrollOffsetY(offsetY);
    }

    // MainWindow.Closing calls this alongside SaveWindowGeometry, capturing
    // whichever view is showing *right now* (not the last-switched-away-from
    // one _viewStates otherwise tracks) - the user may never have switched
    // views at all this session, in which case _currentViewKey already holds
    // the only view there ever was.
    public void SaveCurrentViewState()
    {
        if (_viewModel == null || _currentViewKey == null)
            return;
        _viewModel.SaveLastView(GetScrollOffsetYForKey(_currentViewKey));
    }

    // ── Spinner ───────────────────────────────────────────────────────────────

    private void StartSpinner()
    {
        if (_spinTimer != null)
            return;
        
        _spinTransform = new RotateTransform();
        SpinnerIcon.RenderTransformOrigin = RelativePoint.Center;
        SpinnerIcon.RenderTransform = _spinTransform;
        _spinTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _spinTimer.Tick += (_, _) => _spinTransform.Angle = (_spinTransform.Angle + 6) % 360;
        _spinTimer.Start();
    }

    private void StopSpinner()
    {
        _spinTimer?.Stop();
        _spinTimer = null;
        _spinTransform = null;
        
        if (SpinnerIcon != null)
            SpinnerIcon.RenderTransform = null;
    }

    // ── MusicListView event handlers ──────────────────────────────────────────

    private void OnRowActivated(object? sender, TrackRowViewModel row)
        => _viewModel?.PlayTrack(row.Track);

    private void OnRowContextMenu(object? sender, TrackRowViewModel row)
    {
        // MusicList's Panel_ContextRequested already guarantees the selection is
        // either preserved (row was already selected) or collapsed to just this
        // row (it wasn't) before this fires, so SelectedTracks is always the
        // right set to act on here.
        PopulateAddToPlaylistMenu(MusicList.SelectedTracks);
        _trackMenu.Open(MusicList);
    }

    private void OnHeaderContextMenu(object? sender, EventArgs e)
        => _columnMenu.Open(MusicList);

    private void OnSortRequested(object? sender, string columnId)
        => _viewModel?.SortByColumnCommand?.Execute(columnId);

    // Right-click on a Playlist or Device row in the sidebar. SidebarList is a
    // stock ListBox (unlike MusicList's hand-rolled hit-testing), so the target
    // row is found by walking up from the routed event's Source to its
    // containing ListBoxItem.
    private void SidebarList_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is not Visual visual)
            return;
        if (visual.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is not SidebarItem item)
            return;
        if (_viewModel is not MainViewModel vm)
            return;

        _sidebarItemMenu.Items.Clear();

        if (item is { Kind: SidebarItemKind.Playlist, Playlist: { } playlist })
        {
            // Reuses the same IsEditing/RenameBox flow CreatePlaylistWithTrack
            // already drops a freshly-created playlist into - see
            // RenameBox_Loaded/KeyDown/LostFocus and CommitRename below.
            var renameItem = new MenuItem { Header = "Rename Playlist" };
            renameItem.Click += (_, _) => BeginRename(item);
            _sidebarItemMenu.Items.Add(renameItem);

            var deleteItem = new MenuItem { Header = "Delete Playlist" };
            deleteItem.Click += async (_, _) => await vm.DeletePlaylistAsync(playlist);
            _sidebarItemMenu.Items.Add(deleteItem);
        }
        // A rename can only persist against a resolved fingerprint (see
        // DeviceNicknameStore, keyed by fingerprint rather than the mDNS
        // instance name) - not yet available in the brief window before a
        // freshly-discovered device's /info handshake resolves it.
        else if (item is { Kind: SidebarItemKind.Device, Device.Fingerprint.Length: > 0 })
        {
            var renameItem = new MenuItem { Header = "Rename Device" };
            renameItem.Click += (_, _) => BeginRename(item);
            _sidebarItemMenu.Items.Add(renameItem);
        }
        else
        {
            return;
        }

        _sidebarItemMenu.Open(SidebarList);
        e.Handled = true;
    }

    // ── Drag a track onto a sidebar playlist ────────────────────────────────────
    // See MusicListView's TrackDragMoved/TrackDragEnded for the source side and why
    // this is built on plain pointer capture rather than Avalonia's native
    // DragDrop (a real OS-level drag on macOS, which leaked a missed drop into
    // Music.app instead of harmlessly doing nothing). Only active outside a
    // playlist's own view (AllowReorder owns the drag gesture there instead, for
    // reordering within it).

    // A hit on a specific playlist row (PlaylistItem set) adds to that playlist;
    // a hit anywhere else in the Playlists section (CreateNew) - including its
    // empty tail, or the whole section when there are no playlists yet -
    // creates a new one instead. See HitTestSidebarDrop.
    private readonly record struct SidebarDropTarget(SidebarItem? PlaylistItem, bool CreateNew);

    private void OnTrackDragMoved(object? sender, (IReadOnlyList<Track> tracks, Point position) e)
    {
        ShowDragGhost(MusicList, e.position, DragGhostLabel(e.tracks));
        SetSidebarDropHighlight(HitTestSidebarDrop(MusicList, e.position));
    }

    private void OnTrackDragEnded(object? sender, (IReadOnlyList<Track> tracks, Point position) e)
    {
        var target = HitTestSidebarDrop(MusicList, e.position);
        ResetDragVisuals();

        if (target?.PlaylistItem?.Playlist is { } playlist)
            _ = _viewModel?.AddTracksToPlaylist(e.tracks, playlist);
        else if (target?.CreateNew == true)
            _ = _viewModel?.CreatePlaylistWithTracks(e.tracks);
    }

    private static string DragGhostLabel(IReadOnlyList<Track> tracks) =>
        tracks.Count == 1 ? (tracks[0].Title ?? "Untitled") : $"{tracks.Count} tracks";

    // ── Drag an album/artist name (SubList) onto a sidebar playlist ─────────────
    // Same gesture as OnTrackDragMoved/OnTrackDragEnded above, but SubList is a
    // stock ListBox rather than MusicListView's hand-rolled panel, so there's no
    // control-owned event to subscribe to - the pointer sequence is handled
    // directly here instead.

    private string? _subListAnchor; // Shift+click range-select anchor, analogous to MusicListView's _anchorPath

    private void SubList_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(SubList).Properties.IsLeftButtonPressed)
            return;
        var container = (SubList.InputHitTest(e.GetPosition(SubList)) as Visual)
            ?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        if (container?.DataContext is not string item)
            return;
        if (_viewModel is not { } vm)
            return;

        // SubList's selection is fully owned here rather than left to
        // ListBoxItem's native SelectionMode="Multiple" click handling, which
        // doesn't give the plain-click-collapses-everything-else / Ctrl-toggle /
        // Shift-range semantics this needs - mirrors MusicListView's own
        // hand-rolled Panel_PointerPressed selection logic.
        bool alreadySelected = vm.SelectedSubItems.Contains(item);
        bool shift  = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool toggle = e.KeyModifiers.HasFlag(PlatformShortcuts.Primary);

        if (shift)
            SelectSubListRange(item);
        else if (toggle)
            ToggleSubListItem(item);
        else if (!alreadySelected)
        {
            vm.SetSelectedSubItems(new[] { item });
            _subListAnchor = item;
        }
        // else: item already selected, no modifier - preserve the whole
        // selection so it can be dragged or right-clicked as a batch.

        container.Focus();
        e.Handled = true;

        _subListDragHitItem = item;
        _subListDragStartPoint = e.GetPosition(SubList);
        e.Pointer.Capture(SubList);
    }

    private void ToggleSubListItem(string item)
    {
        if (_viewModel is not { } vm)
            return;
        var current = vm.SelectedSubItems.ToList();
        if (!current.Remove(item))
            current.Add(item);
        vm.SetSelectedSubItems(current);
        _subListAnchor = item;
    }

    private void SelectSubListRange(string item)
    {
        if (_viewModel is not { } vm)
            return;
        var items = vm.SubListItems;
        int anchorIdx = _subListAnchor != null ? items.IndexOf(_subListAnchor) : -1;
        int clickIdx  = items.IndexOf(item);
        if (anchorIdx < 0)
            anchorIdx = clickIdx;
        int lo = Math.Min(anchorIdx, clickIdx);
        int hi = Math.Max(anchorIdx, clickIdx);

        var range = new List<string>();
        for (int i = lo; i <= hi; i++)
            range.Add(items[i]);
        // Anchor deliberately left untouched so repeated Shift+clicks keep
        // extending/shrinking the range from the same starting point.
        vm.SetSelectedSubItems(range);
    }

    private void SubList_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_subListDragHitItem == null)
            return;

        var pt = e.GetPosition(SubList);
        if (!_isSubListDragging)
        {
            var dx = pt.X - _subListDragStartPoint.X;
            var dy = pt.Y - _subListDragStartPoint.Y;
            if (dx * dx + dy * dy < SubListDragThreshold * SubListDragThreshold)
                return;
            _isSubListDragging = true;
            // Selection is final by now (SubList_PointerPressed above already
            // resolved it for this press).
            _subListDragItems = _viewModel?.SelectedSubItems.Contains(_subListDragHitItem) == true
                ? _viewModel.SelectedSubItems.ToList()
                : new List<string> { _subListDragHitItem };
        }

        ShowDragGhost(SubList, pt, DragGhostLabel(_subListDragItems!));
        SetSidebarDropHighlight(HitTestSidebarDrop(SubList, pt));
    }

    private void SubList_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isSubListDragging && _subListDragItems is { } items)
        {
            var target = HitTestSidebarDrop(SubList, e.GetPosition(SubList));
            var tracks = _viewModel?.GetTracksForSubListItems(items) ?? Enumerable.Empty<Track>();

            if (target?.PlaylistItem?.Playlist is { } playlist)
                _ = _viewModel?.AddTracksToPlaylist(tracks, playlist);
            else if (target?.CreateNew == true)
                _ = _viewModel?.CreatePlaylistWithTracks(tracks);
        }

        e.Pointer.Capture(null);
        EndSubListDrag();
    }

    private void SubList_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        => EndSubListDrag();

    private static string DragGhostLabel(IReadOnlyList<string> items) =>
        items.Count == 1 ? items[0] : $"{items.Count} items";

    private void EndSubListDrag()
    {
        _subListDragHitItem = null;
        _subListDragItems = null;
        _isSubListDragging = false;
        ResetDragVisuals();
    }

    private void ShowDragGhost(Visual source, Point sourcePosition, string text)
    {
        if (source.TranslatePoint(sourcePosition, ContentGrid) is { } ghostPos)
        {
            DragGhost.Margin = new Thickness(ghostPos.X + 14, ghostPos.Y + 14, 0, 0);
            DragGhost.IsVisible = true;
            DragGhostText.Text = text;
        }
    }

    // sourcePosition is in source's own local coordinate space (e.g.
    // MusicListView's TrackDragMoved/TrackDragEnded, or SubList's pointer
    // events) - translated here into SidebarList's space. A specific playlist
    // row wins if the pointer is directly over one; otherwise falls back to
    // GetPlaylistsDropBand to see if the pointer is still somewhere within the
    // Playlists section.
    private SidebarDropTarget? HitTestSidebarDrop(Visual source, Point sourcePosition)
    {
        if (source.TranslatePoint(sourcePosition, SidebarList) is not { } sidebarPos)
            return null;
        if (!new Rect(SidebarList.Bounds.Size).Contains(sidebarPos))
            return null;

        if ((SidebarList.InputHitTest(sidebarPos) as Visual)
            ?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is
            SidebarItem { Kind: SidebarItemKind.Playlist } item)
            return new SidebarDropTarget(item, false);

        if (GetPlaylistsDropBand() is { } band && sidebarPos.Y >= band.Top && sidebarPos.Y < band.Bottom)
            return new SidebarDropTarget(null, true);

        return null;
    }

    // The "drop here to create a new playlist" zone spans everything below the
    // Library section (Songs/Albums/Artists) and above Devices (if any) - so a
    // drop that misses a specific playlist row still creates a playlist rather
    // than silently doing nothing, and this works even with zero playlists (no
    // "Playlists" header exists yet in that case). Computed from realized
    // container bounds rather than a real stretchy layout element, since
    // SidebarList's items are sized to content and don't grow to fill its
    // leftover height.
    private Rect? GetPlaylistsDropBand()
    {
        if (_viewModel is not { } vm)
            return null;

        var items = vm.SidebarItems;
        var libraryEndIndex = -1;
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Kind is SidebarItemKind.Songs or SidebarItemKind.Albums or SidebarItemKind.Artists)
                libraryEndIndex = i;
        }
        if (libraryEndIndex < 0)
            return null;

        var devicesHeaderIndex = -1;
        for (var i = libraryEndIndex + 1; i < items.Count; i++)
        {
            if (items[i] is { Kind: SidebarItemKind.Header, Name: "Devices" })
            {
                devicesHeaderIndex = i;
                break;
            }
        }

        var top = SidebarList.ContainerFromIndex(libraryEndIndex) is Control lastLibraryRow
            ? lastLibraryRow.Bounds.Bottom
            : 0;
        var bottom = devicesHeaderIndex >= 0 && SidebarList.ContainerFromIndex(devicesHeaderIndex) is Control devicesHeader
            ? devicesHeader.Bounds.Top
            : SidebarList.Bounds.Height;

        return bottom > top ? new Rect(0, top, SidebarList.Bounds.Width, bottom - top) : null;
    }

    private void SetDropTargetHighlight(SidebarItem? item)
    {
        if (_dropTargetPlaylistItem == item)
            return;
        if (_dropTargetPlaylistItem != null)
            _dropTargetPlaylistItem.IsDropTarget = false;
        _dropTargetPlaylistItem = item;
        if (_dropTargetPlaylistItem != null)
            _dropTargetPlaylistItem.IsDropTarget = true;
    }

    private void SetSidebarDropHighlight(SidebarDropTarget? target)
    {
        SetDropTargetHighlight(target?.PlaylistItem);

        if (target?.CreateNew == true && GetPlaylistsDropBand() is { } band)
        {
            NewPlaylistDropZone.Margin = new Thickness(4, band.Top + 1, 4, 0);
            NewPlaylistDropZone.Height = Math.Max(0, band.Height - 2);
            NewPlaylistDropZone.IsVisible = true;
        }
        else
        {
            NewPlaylistDropZone.IsVisible = false;
        }
    }

    private void ResetDragVisuals()
    {
        DragGhost.IsVisible = false;
        NewPlaylistDropZone.IsVisible = false;
        SetDropTargetHighlight(null);
    }

    // ── Context menus ─────────────────────────────────────────────────────────

    private void BuildColumnMenu()
    {
        if (_viewModel == null)
            return;
        var columnManager = Ioc.Default.GetService<ColumnManager>()!;

        _columnMenu = new ContextMenu();
        foreach (var col in columnManager.Columns)
        {
            var col1 = col; // capture
            var item = new MenuItem { Header = col.Header };
            SetCheckIcon(item, col.IsVisible);
            item.Click += (_, _) =>
            {
                col1.IsVisible = !col1.IsVisible;
                SetCheckIcon(item, col1.IsVisible);
            };
            _columnMenu.Items.Add(item);
        }

        var getInfoItem = new MenuItem
        {
            Header       = "Get Info",
            InputGesture = new KeyGesture(Key.I, PlatformShortcuts.Primary),
        };
        getInfoItem.Click += (_, _) => OpenTrackInfo();

        var locateFileItem = new MenuItem { Header = "Locate File" };
        locateFileItem.Click += (_, _) => LocateFile();

        _addToPlaylistItem = new MenuItem { Header = "Add To Playlist" };

        _trackMenu = new ContextMenu();
        _trackMenu.Items.Add(getInfoItem);
        _trackMenu.Items.Add(_addToPlaylistItem);
        _trackMenu.Items.Add(locateFileItem);
    }

    private void PopulateAddToPlaylistMenu(IReadOnlyList<Track> tracks)
        => PopulateAddToPlaylistMenu(_addToPlaylistItem, tracks);

    // Shared by the track list's own _trackMenu (via the overload above) and
    // BuildAlbumContextMenu below - same New Playlist / existing-playlists
    // submenu either way, just acting on a different set of tracks.
    private void PopulateAddToPlaylistMenu(MenuItem addToPlaylistItem, IReadOnlyList<Track> tracks)
    {
        if (_viewModel is not MainViewModel vm)
            return;

        addToPlaylistItem.Items.Clear();

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

    // Play Album / Get Info / Add To Playlist for a right-clicked album tile -
    // see AlbumGrid_ContextRequested. Built fresh per-request rather than a
    // shared field, same as AlbumGridRowControl.BuildTrackContextMenu (its
    // song-level equivalent) does for its own right-click menu. Play always
    // targets just the tile actually clicked, same "not a coherent multi-target
    // action" reasoning as that method's own Locate File; Get Info/Add To
    // Playlist act on the full current album selection via tracks.
    private ContextMenu BuildAlbumContextMenu(IReadOnlyList<Track> tracks, string clickedAlbumName)
    {
        var playItem = new MenuItem { Header = "Play Album" };
        playItem.Click += (_, _) => _viewModel?.PlayAlbum(clickedAlbumName);

        var getInfoItem = new MenuItem { Header = "Get Info" };
        getInfoItem.Click += (_, _) => OpenTrackInfoForSelectedAlbums();

        var addToPlaylistItem = new MenuItem { Header = "Add To Playlist" };
        PopulateAddToPlaylistMenu(addToPlaylistItem, tracks);

        var menu = new ContextMenu();
        menu.Items.Add(playItem);
        menu.Items.Add(getInfoItem);
        menu.Items.Add(addToPlaylistItem);
        return menu;
    }

    private static void SetCheckIcon(MenuItem item, bool visible)
    {
        item.Icon = visible
            ? new MaterialIcon { Kind = MaterialIconKind.Check, Width = 14, Height = 14 }
            : null;
    }

    // ── Keyboard (tunnel to catch keys before MusicListView) ─────────────────

    private void MusicList_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.I && e.KeyModifiers == PlatformShortcuts.Primary)
        {
            OpenTrackInfo();
            e.Handled = true;
        }
        else if (e.Key == Key.Space)
        {
            _viewModel?.PlayOrPauseFromCurrentView();
            e.Handled = true;
        }
        // Enter is handled inside MusicListView (fires RowActivated)
    }

    private void MainView_PreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // Still needed here for Windows/Linux, which have no native app menu
        // at all (App.axaml's NativeMenu.Menu is macOS-only - see its own
        // comment). On macOS specifically this case is unreachable dead code,
        // not incorrect: the app menu's own Settings… item (Gesture="Cmd+OemComma")
        // is resolved by the OS before a Cmd+, key event ever reaches
        // Avalonia's input pipeline at all - that resolution happens whether
        // or not this case exists, which is exactly why leaving it in without
        // also giving the menu item its own Gesture silently broke the
        // shortcut on macOS rather than merely duplicating it.
        if (e.Key == Key.OemComma && e.KeyModifiers == PlatformShortcuts.Primary)
        {
            _viewModel?.OpenSettingsCommand?.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.L && e.KeyModifiers == PlatformShortcuts.Primary)
        {
            _ = _viewModel?.GoToCurrentlyPlayingTrackAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.J && e.KeyModifiers == PlatformShortcuts.Primary)
        {
            _viewModel?.OpenColumnSelectorCommand?.Execute(null);
            e.Handled = true;
        }
        // Cmd/Ctrl+I on Albums/Recently Added - MusicList's own tunnel handler
        // (MusicList_KeyDown) can't fire here since MusicList is hidden and
        // never has focus while a grid is showing, so it's only reachable at
        // this root level. Deliberately scoped to just the grid views - the
        // track-list case is left alone so MusicList_KeyDown still owns it,
        // unchanged, exactly as before this handler existed.
        else if (e.Key == Key.I && e.KeyModifiers == PlatformShortcuts.Primary &&
                 _viewModel is { IsShowingAlbumGrid: true } or { IsShowingRecentlyAddedGrid: true })
        {
            OpenTrackInfoForSelectedAlbums();
            e.Handled = true;
        }
    }

    private void OnNavigateToTrackRequested(object? sender, Track track) => MusicList.ScrollToTrack(track);

    private void OnSettingsRequested(object? sender, EventArgs e) => OpenSettingsWindow();

    private void OnColumnSelectorRequested(object? sender, EventArgs e) => OpenColumnSelectorWindow();

    // Raised by MainViewModel (forwarding PlaylistSyncService.ConflictDetected)
    // when the same playlist changed on both this device and a peer since they
    // last agreed - see SYNC-PLAN.md Phase 2. The dialog's result is fed back into
    // e.Resolution, which unblocks that one playlist's merge on the sync-session
    // background task; nothing else about the sync waits on the dialog closing.
    private async void OnPlaylistConflictRequested(object? sender, PlaylistConflictEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            e.Resolution.TrySetResult(PlaylistConflictChoice.KeepLocal);
            return;
        }

        var choice = await new PlaylistConflictWindow(e).ShowDialog<PlaylistConflictChoice>(owner);
        e.Resolution.TrySetResult(choice);
    }

    // Raised by MainViewModel (forwarding SyncHttpServer.PeerApprovalRequested)
    // the first time an unrecognized peer fingerprint calls a gated sync endpoint
    // - see SYNC-PLAN.md Phase 3's trust gate. Reuses the generic confirm dialog
    // rather than a bespoke window - Cancel is that dialog's default/Escape
    // action, which conveniently doubles as "deny" here too.
    private async void OnPeerApprovalRequested(object? sender, PeerApprovalRequestedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            e.Resolution.TrySetResult(false);
            return;
        }

        var allowed = await ConfirmDialogWindow.ShowAsync(
            owner,
            "Allow This Device to Sync?",
            $"\"{e.Alias}\" wants to sync playlists and library data with this device. Only allow devices you recognize - it will not be asked again.",
            "Allow");
        e.Resolution.TrySetResult(allowed);
    }

    private async void OnDeletePlaylistConfirmationRequested(object? sender, DeletePlaylistConfirmationEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            e.Confirmed.TrySetResult(true);
            return;
        }

        var confirmed = await ConfirmDialogWindow.ShowAsync(
            owner,
            "Delete Playlist?",
            $"\"{e.Playlist.Name}\" will be permanently deleted. This cannot be undone.",
            "Delete");
        e.Confirmed.TrySetResult(confirmed);
    }

    private void OpenSettingsWindow()
    {
        if (_viewModel == null)
            return;
        var settingsWindow = new SettingsWindow(_viewModel);
        if (TopLevel.GetTopLevel(this) is Window owner)
            settingsWindow.ShowDialog(owner);
        else
            settingsWindow.Show();
    }

    private void OpenColumnSelectorWindow()
    {
        if (_viewModel == null)
            return;
        var columnManager = Ioc.Default.GetService<ColumnManager>()!;
        var columnSelectorWindow = new ColumnSelectorWindow(columnManager, _viewModel);
        if (TopLevel.GetTopLevel(this) is Window owner)
            columnSelectorWindow.ShowDialog(owner);
        else
            columnSelectorWindow.Show();
    }

    // ── Track actions ─────────────────────────────────────────────────────────

    private void OpenTrackInfo()
    {
        if (_viewModel is not MainViewModel vm)
            return;

        TrackInfoWindow infoWindow;
        var selected = MusicList.SelectedTracks;
        if (selected.Count > 1)
        {
            // Batch mode: edit the whole multi-selection together, no Prev/Next.
            infoWindow = new TrackInfoWindow(selected, vm.Library) { ShowInTaskbar = false };
        }
        else
        {
            if (MusicList.SelectedTrack is not Track track)
                return;
            var tracks = vm.DisplayedTracks;
            var index  = tracks.ToList().IndexOf(track);
            if (index < 0)
                index = 0;
            infoWindow = new TrackInfoWindow(tracks, index, vm.Library) { ShowInTaskbar = false };
            infoWindow.TrackNavigated += (_, t) => MusicList.SelectedTrack = t;
        }

        if (TopLevel.GetTopLevel(this) is Window owner)
            infoWindow.Show(owner);
        else
            infoWindow.Show();
    }

    // Cmd/Ctrl+I on Albums/Recently Added (see MainView_PreviewKeyDown) - acts
    // on the multi-selected album(s) (Ctrl/Shift-click, see AlbumGrid_
    // PointerPressed) if there is one, otherwise falls back to whichever
    // single album is currently expanded (the common case: a plain click
    // expands without touching SelectedSubItems at all - see ToggleAlbumGrid
    // Item's own doc comment). Always batch mode, even for one album's worth
    // of tracks - there's no meaningful single-track Prev/Next context here
    // the way there is for a MusicListView row.
    private void OpenTrackInfoForSelectedAlbums()
    {
        if (_viewModel is not MainViewModel vm)
            return;

        // A specific song (or songs) selected within the currently-expanded
        // album's own track list (click/Ctrl/Shift/arrow-keys - see
        // AlbumGridRowControl) takes priority over album-tile-level
        // selection below - otherwise this always fell back to "the whole
        // expanded album," even with just one particular song selected.
        var songSelection = AlbumGrid.GetExpandedRowSelectedTracks();
        if (songSelection.Count == 0)
            songSelection = RecentlyAddedGrid.GetExpandedRowSelectedTracks();

        TrackInfoWindow infoWindow;
        if (songSelection.Count == 1)
        {
            // Single-track mode, with Prev/Next through the expanded album's
            // own track list - same as AlbumGridRowControl's own row context
            // menu's "Get Info" gives a single selected track, for the same
            // reason: there's a specific, coherent list to browse here that
            // the "multiple albums selected" case below doesn't have.
            var track = songSelection[0];
            var albumTracks = vm.ExpandedAlbumTracks.ToList();
            var index = albumTracks.IndexOf(track);
            if (index < 0)
                index = 0;
            infoWindow = new TrackInfoWindow(albumTracks, index, vm.Library) { ShowInTaskbar = false };
        }
        else
        {
            var tracks = songSelection.Count > 0 ? songSelection : ResolveSelectedAlbumTracks(vm);
            if (tracks.Count == 0)
                return;
            infoWindow = new TrackInfoWindow(tracks, vm.Library) { ShowInTaskbar = false };
        }

        if (TopLevel.GetTopLevel(this) is Window owner)
            infoWindow.Show(owner);
        else
            infoWindow.Show();
    }

    // Fallback for OpenTrackInfoForSelectedAlbums above when no specific song
    // is selected within the expanded album's track list - the multi-selected
    // album tile(s) (Ctrl/Shift-click, see AlbumGrid_PointerPressed) if there
    // are any, otherwise whichever single album is currently expanded (the
    // common case: a plain click expands without touching SelectedSubItems
    // at all - see ToggleAlbumGridItem's own doc comment).
    private static IReadOnlyList<Track> ResolveSelectedAlbumTracks(MainViewModel vm)
    {
        var albumNames = vm.SelectedSubItems.Count > 0
            ? vm.SelectedSubItems
            : vm.ExpandedAlbumName is { } expanded ? new[] { expanded } : Array.Empty<string>();
        return albumNames.Count == 0 ? Array.Empty<Track>() : vm.GetTracksForSubListItems(albumNames).ToList();
    }

    private void LocateFile()
    {
        if (MusicList.SelectedTrack is not Track track)
            return;
        FileLocator.Reveal(track.Path);
    }

    // ── Sidebar ───────────────────────────────────────────────────────────────

    private SidebarItem? _lastSelectableSidebarItem;

    private void SidebarList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox list)
            return;
        if (list.SelectedItem is SidebarItem { IsHeader: true })
            list.SelectedItem = _lastSelectableSidebarItem;
        else if (list.SelectedItem is SidebarItem item)
            _lastSelectableSidebarItem = item;
    }

    // ── Sidebar rename (new playlist) ────────────────────────────────────────

    private void RenameBox_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb)
            return;
        tb.Focus();
        tb.SelectAll();
    }

    // Entry point for renaming an *existing* playlist (the sidebar's own
    // context-menu, and the "Playlist > Rename Playlist" main-menu command below).
    // CreatePlaylistWithTrack's brand-new SidebarItem gets a freshly realized
    // container, so its TextBox's Loaded event fires and RenameBox_Loaded's
    // Focus() above just works. An existing item's container was realized long
    // ago, though - flipping IsEditing only flips that already-realized
    // TextBox's IsVisible, which does not raise Loaded again, so nothing ever
    // focused it (every downstream symptom - arrow keys reaching SidebarList,
    // Enter not confirming - traced back to this). Posted a frame so IsVisible
    // has actually applied before Focus() is attempted.
    private void BeginRename(SidebarItem item)
    {
        item.IsEditing = true;

        var index = _viewModel?.SidebarItems.IndexOf(item) ?? -1;
        if (index < 0)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (SidebarList.ContainerFromIndex(index) is Control container &&
                container.FindDescendantOfType<TextBox>() is { } tb)
            {
                tb.Focus();
                tb.SelectAll();
            }
        });
    }

    // "Playlist > Rename Playlist" main-menu command (see MainWindow.axaml and
    // MainViewModel.RenamePlaylistRequested) - operates on whichever playlist is
    // currently selected, since unlike the sidebar's context menu there is no
    // specific right-clicked row to go on.
    private void OnRenamePlaylistRequested(object? sender, EventArgs e)
    {
        if (_viewModel?.SelectedSidebarItem is { Kind: SidebarItemKind.Playlist } item)
            BeginRename(item);
    }

    private void RenameBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: SidebarItem item })
            return;

        switch (e.Key)
        {
            case Key.Enter:
            case Key.Escape:
                // Handled must be set before the (async) commit, not after -
                // otherwise, since this is fire-and-forget, the KeyDown event has
                // already finished bubbling with Handled still false by the time
                // CommitRename's awaited save completes, letting SidebarList's own
                // KeyDown handling see an apparently-unhandled Enter/Escape too.
                e.Handled = true;
                _ = CommitRename(item);
                break;
            case Key.Up:
            case Key.Down:
            case Key.Left:
            case Key.Right:
                // Keep arrow keys scoped to the rename box - otherwise SidebarList's
                // own arrow-key handling moves SelectedItem (and, since it drops
                // focus off this TextBox, ends the rename) while this one is still
                // mid-rename. Left/Right normally never reach here at all - the
                // TextBox's own caret movement already marks them handled - except
                // right at the start/end of the text, where there is no caret move
                // left to make and the event falls through unhandled.
                e.Handled = true;
                break;
        }
    }

    private async void RenameBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: SidebarItem item })
            await CommitRename(item);
    }

    // LostFocus alone isn't enough to end a rename: not every control a click can
    // land on actually takes keyboard focus (MusicListView's hand-rolled row panel
    // only calls Focus() when the click hits an actual row - see its
    // Panel_PointerPressed), so a click elsewhere can leave the textbox focused
    // and the item stuck in edit mode. Tunnelled so it commits before the click's
    // own target handles it.
    private void MainView_PreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel?.SidebarItems.FirstOrDefault(i => i.IsEditing) is not { } editing)
            return;
        if (e.Source is Visual visual && visual.FindAncestorOfType<TextBox>(includeSelf: true)?.DataContext == editing)
            return;

        _ = CommitRename(editing);
    }

    private async Task CommitRename(SidebarItem item)
    {
        if (!item.IsEditing)
            return;

        var name = item.Name?.Trim();

        if (item.Device is { Fingerprint.Length: > 0 } device)
        {
            item.IsEditing = false;
            await _deviceNicknameStore.SetAsync(device.Fingerprint, name ?? "");

            // Re-derives item.Name (and every other Device row's) from
            // MainViewModel.ResolveDeviceDisplayName - the one place that
            // decides what a device is called - rather than duplicating its
            // empty-falls-back-to-device.Alias logic here too.
            _viewModel?.RefreshDeviceDisplayNames();
            return;
        }

        item.Name = string.IsNullOrEmpty(name) ? "New Playlist" : name;
        item.IsEditing = false;

        if (item.Playlist == null || _viewModel == null)
            return;
        item.Playlist.Name = item.Name;
        await _playlistStore.SaveAsync(_viewModel.Library.Playlists);
        _viewModel.ScheduleContentSync();
    }

    // ── Drag-to-reorder (playlist view only) ────────────────────────────────────

    private async void OnRowReordered(object? sender, (Track dragged, Track? insertBefore) e)
    {
        if (_viewModel is not MainViewModel vm)
            return;
        if (vm.SelectedSidebarItem?.Playlist is not Playlist playlist)
            return;

        await vm.ReorderPlaylistTrack(playlist, e.dragged, e.insertBefore);
    }
}
