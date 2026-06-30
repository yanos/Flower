using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

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

    private DispatcherTimer? _spinTimer;
    private RotateTransform? _spinTransform;

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

        // Forward Space / Cmd+I from inside the list
        MusicList.AddHandler(KeyDownEvent, MusicList_KeyDown, RoutingStrategies.Tunnel);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            StopSpinner();
        }

        _viewModel = DataContext as MainViewModel;

        if (_viewModel != null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            BuildColumnMenu();
            if (_viewModel.IsBusy) StartSpinner();

            // Push initial rows to MusicListView
            MusicList.SetItems(_viewModel.Rows);
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
                MusicList.SetItems(_viewModel!.Rows);
                break;
            case nameof(MainViewModel.SortColumn):
            case nameof(MainViewModel.SortAscending):
                MusicList.UpdateSortIndicators(_viewModel!.SortColumn, _viewModel.SortAscending);
                break;
        }
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
        => _trackMenu.Open(MusicList);

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

        _trackMenu = new ContextMenu();
        _trackMenu.Items.Add(getInfoItem);
        _trackMenu.Items.Add(locateFileItem);
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

    // ── Track actions ─────────────────────────────────────────────────────────

    private void OpenTrackInfo()
    {
        if (MusicList.SelectedTrack is not Track track) return;
        var infoWindow = new TrackInfoWindow(track) { ShowInTaskbar = false };
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
}
