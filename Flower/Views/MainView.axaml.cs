using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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

    private ContextMenu _columnMenu = new();
    private ContextMenu _trackMenu  = new();
    private MenuItem    _addToPlaylistItem = new();
    private readonly ContextMenu _playlistMenu = new();
    private SidebarItem? _dropTargetPlaylistItem;

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

            // Push initial rows to MusicListView
            ApplyRows();
        }
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
        }
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

        if (_currentViewKey != null)
            _viewStates[_currentViewKey] = new ViewScrollState(MusicList.GetScrollOffsetY(), MusicList.SelectedRow?.Track.Path);

        MusicList.SetItems(_viewModel.Rows);

        if (_viewStates.TryGetValue(newKey, out var saved))
        {
            MusicList.SelectedRow = _viewModel.Rows.FirstOrDefault(r => r.Track.Path == saved.SelectedTrackPath);
            MusicList.SetScrollOffsetY(saved.ScrollOffsetY);
        }
        else
        {
            MusicList.SelectedRow = null;
            MusicList.SetScrollOffsetY(0);
        }

        _currentViewKey = newKey;
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
        PopulateAddToPlaylistMenu(row.Track);
        _trackMenu.Open(MusicList);
    }

    private void OnHeaderContextMenu(object? sender, EventArgs e)
        => _columnMenu.Open(MusicList);

    private void OnSortRequested(object? sender, string columnId)
        => _viewModel?.SortByColumnCommand?.Execute(columnId);

    // Right-click on a Playlist row in the sidebar. SidebarList is a stock ListBox
    // (unlike MusicList's hand-rolled hit-testing), so the target row is found by
    // walking up from the routed event's Source to its containing ListBoxItem.
    private void SidebarList_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is not Visual visual)
            return;
        if (visual.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is not
            SidebarItem { Kind: SidebarItemKind.Playlist, Playlist: { } playlist } item)
            return;
        if (_viewModel is not MainViewModel vm)
            return;

        _playlistMenu.Items.Clear();

        // Reuses the same IsEditing/RenameBox flow CreatePlaylistWithTrack already
        // drops a freshly-created playlist into - see RenameBox_Loaded/KeyDown/
        // LostFocus and CommitRename below.
        var renameItem = new MenuItem { Header = "Rename Playlist" };
        renameItem.Click += (_, _) => BeginRename(item);
        _playlistMenu.Items.Add(renameItem);

        var deleteItem = new MenuItem { Header = "Delete Playlist" };
        deleteItem.Click += async (_, _) => await vm.DeletePlaylistAsync(playlist);
        _playlistMenu.Items.Add(deleteItem);

        _playlistMenu.Open(SidebarList);
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

    private void OnTrackDragMoved(object? sender, (Track track, Point position) e)
    {
        if (MusicList.TranslatePoint(e.position, ContentGrid) is { } ghostPos)
        {
            DragGhost.Margin = new Thickness(ghostPos.X + 14, ghostPos.Y + 14, 0, 0);
            DragGhost.IsVisible = true;
            DragGhostText.Text = e.track.Title ?? "Untitled";
        }

        SetSidebarDropHighlight(HitTestSidebarDrop(e.position));
    }

    private void OnTrackDragEnded(object? sender, (Track track, Point position) e)
    {
        var target = HitTestSidebarDrop(e.position);
        ResetDragVisuals();

        if (target?.PlaylistItem?.Playlist is { } playlist)
            _ = _viewModel?.AddTrackToPlaylist(e.track, playlist);
        else if (target?.CreateNew == true)
            _ = _viewModel?.CreatePlaylistWithTrack(e.track);
    }

    // musicListPosition is in MusicListView's own local coordinate space (see
    // MusicListView's TrackDragMoved/TrackDragEnded) - translated here into
    // SidebarList's space. A specific playlist row wins if the pointer is
    // directly over one; otherwise falls back to GetPlaylistsDropBand to see if
    // the pointer is still somewhere within the Playlists section.
    private SidebarDropTarget? HitTestSidebarDrop(Point musicListPosition)
    {
        if (MusicList.TranslatePoint(musicListPosition, SidebarList) is not { } sidebarPos)
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

    private void PopulateAddToPlaylistMenu(Track track)
    {
        if (_viewModel is not MainViewModel vm)
            return;

        _addToPlaylistItem.Items.Clear();

        var newPlaylistItem = new MenuItem { Header = "New Playlist" };
        newPlaylistItem.Click += async (_, _) => await vm.CreatePlaylistWithTrack(track);
        _addToPlaylistItem.Items.Add(newPlaylistItem);

        if (vm.Library.Playlists.Count > 0)
        {
            _addToPlaylistItem.Items.Add(new Separator());
            foreach (var playlist in vm.Library.Playlists)
            {
                var target = playlist; // capture
                var item = new MenuItem { Header = target.Name };
                item.Click += async (_, _) => await vm.AddTrackToPlaylist(track, target);
                _addToPlaylistItem.Items.Add(item);
            }
        }
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
        if (MusicList.SelectedTrack is not Track track)
            return;
        if (_viewModel is not MainViewModel vm)
            return;
        var tracks = vm.DisplayedTracks;
        var index  = tracks.ToList().IndexOf(track);
        if (index < 0)
            index = 0;
        var infoWindow = new TrackInfoWindow(tracks, index, vm.Library) { ShowInTaskbar = false };
        infoWindow.TrackNavigated += (_, t) => MusicList.SelectedTrack = t;
        if (TopLevel.GetTopLevel(this) is Window owner)
            infoWindow.Show(owner);
        else
            infoWindow.Show();
    }

    private void LocateFile()
    {
        if (MusicList.SelectedTrack is not Track track)
            return;
        var path = track.Path;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;

        if (OperatingSystem.IsMacOS())
            Process.Start("open", ["-R", path]);
        else if (OperatingSystem.IsWindows())
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        else
            Process.Start("xdg-open", [Path.GetDirectoryName(path)!]);
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
        item.Name = string.IsNullOrEmpty(name) ? "New Playlist" : name;
        item.IsEditing = false;

        if (item.Playlist == null || _viewModel == null)
            return;
        item.Playlist.Name = item.Name;
        await new PlaylistStore().SaveAsync(_viewModel.Library.Playlists);
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
