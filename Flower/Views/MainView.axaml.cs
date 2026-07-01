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

        // Forward Space / Cmd+I from inside the list
        MusicList.AddHandler(KeyDownEvent, MusicList_KeyDown, RoutingStrategies.Tunnel);

        // Cmd+, (Settings) must work regardless of which control currently has focus,
        // so it's handled at the MainView root rather than scoped to MusicList.
        AddHandler(KeyDownEvent, MainView_PreviewKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.SettingsRequested -= OnSettingsRequested;
            _viewModel.NavigateToTrackRequested -= OnNavigateToTrackRequested;
            StopSpinner();
        }

        _viewModel = DataContext as MainViewModel;

        if (_viewModel != null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.SettingsRequested += OnSettingsRequested;
            _viewModel.NavigateToTrackRequested += OnNavigateToTrackRequested;
            BuildColumnMenu();
            if (_viewModel.IsBusy) StartSpinner();

            // Push initial rows to MusicListView
            ApplyRows();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsBusy):
                if (_viewModel!.IsBusy) StartSpinner(); else StopSpinner();
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
        if (_viewModel == null) return;

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
        if (_spinTimer != null) return;
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
        if (SpinnerIcon != null) SpinnerIcon.RenderTransform = null;
    }

    // ── MusicListView event handlers ──────────────────────────────────────────

    private void OnRowActivated(object? sender, TrackRowViewModel row)
        => _playlistControlViewModel.Play(row.Track);

    private void OnRowContextMenu(object? sender, TrackRowViewModel row)
    {
        PopulateAddToPlaylistMenu(row.Track);
        _trackMenu.Open(MusicList);
    }

    private void OnHeaderContextMenu(object? sender, EventArgs e)
        => _columnMenu.Open(MusicList);

    private void OnSortRequested(object? sender, string columnId)
        => _viewModel?.SortByColumnCommand?.Execute(columnId);

    // ── Context menus ─────────────────────────────────────────────────────────

    private void BuildColumnMenu()
    {
        if (_viewModel == null) return;
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
            InputGesture = new KeyGesture(Key.I, KeyModifiers.Meta),
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
        if (_viewModel is not MainViewModel vm) return;

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
        if (e.Key == Key.I && e.KeyModifiers == KeyModifiers.Meta)
        {
            OpenTrackInfo();
            e.Handled = true;
        }
        else if (e.Key == Key.Space)
        {
            _playlistControlViewModel.PlayOrPause();
            e.Handled = true;
        }
        // Enter is handled inside MusicListView (fires RowActivated)
    }

    private void MainView_PreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.OemComma && e.KeyModifiers == KeyModifiers.Meta)
        {
            _viewModel?.OpenSettingsCommand?.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.L && e.KeyModifiers == KeyModifiers.Meta)
        {
            _ = _viewModel?.GoToCurrentlyPlayingTrackAsync();
            e.Handled = true;
        }
    }

    private void OnNavigateToTrackRequested(object? sender, Track track) => MusicList.ScrollToTrack(track);

    private void OnSettingsRequested(object? sender, EventArgs e) => OpenSettingsWindow();

    private void OpenSettingsWindow()
    {
        if (_viewModel == null) return;
        var settingsWindow = new SettingsWindow(_viewModel);
        if (TopLevel.GetTopLevel(this) is Window owner)
            settingsWindow.ShowDialog(owner);
        else
            settingsWindow.Show();
    }

    // ── Track actions ─────────────────────────────────────────────────────────

    private void OpenTrackInfo()
    {
        if (MusicList.SelectedTrack is not Track track) return;
        if (_viewModel is not MainViewModel vm) return;
        var tracks = vm.DisplayedTracks;
        var index  = tracks.ToList().IndexOf(track);
        if (index < 0) index = 0;
        var infoWindow = new TrackInfoWindow(tracks, index, vm.Library) { ShowInTaskbar = false };
        infoWindow.TrackNavigated += (_, t) => MusicList.SelectedTrack = t;
        if (TopLevel.GetTopLevel(this) is Window owner)
            infoWindow.Show(owner);
        else
            infoWindow.Show();
    }

    private void LocateFile()
    {
        if (MusicList.SelectedTrack is not Track track) return;
        var path = track.Path;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

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
        if (sender is not ListBox list) return;
        if (list.SelectedItem is SidebarItem { IsHeader: true })
            list.SelectedItem = _lastSelectableSidebarItem;
        else if (list.SelectedItem is SidebarItem item)
            _lastSelectableSidebarItem = item;
    }

    // ── Sidebar rename (new playlist) ────────────────────────────────────────

    private void RenameBox_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        tb.Focus();
        tb.SelectAll();
    }

    private async void RenameBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb) return;
        if (e.Key == Key.Enter || e.Key == Key.Escape)
        {
            await CommitRename(tb);
            e.Handled = true;
        }
    }

    private async void RenameBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) await CommitRename(tb);
    }

    private async Task CommitRename(TextBox tb)
    {
        if (tb.DataContext is not SidebarItem item || !item.IsEditing) return;

        var name = tb.Text?.Trim();
        item.Name = string.IsNullOrEmpty(name) ? "New Playlist" : name;
        item.IsEditing = false;

        if (item.Playlist == null || _viewModel == null) return;
        item.Playlist.Name = item.Name;
        await new PlaylistStore().SaveAsync(_viewModel.Library.Playlists);
    }

    // ── Drag-to-reorder (playlist view only) ────────────────────────────────────

    private async void OnRowReordered(object? sender, (Track dragged, Track? insertBefore) e)
    {
        if (_viewModel is not MainViewModel vm) return;
        if (vm.SelectedSidebarItem?.Playlist is not Playlist playlist) return;

        await vm.ReorderPlaylistTrack(playlist, e.dragged, e.insertBefore);
    }
}
