using System;
using System.ComponentModel;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

using CommunityToolkit.Mvvm.DependencyInjection;

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
        // Tunnel intercepts Enter/Space before DataGrid's own keyboard handler runs
        TrackList.AddHandler(InputElement.KeyDownEvent, TrackList_KeyDown, RoutingStrategies.Tunnel);
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
            BuildMenus();
            ApplyColumnVisibility();
            if (_viewModel.IsBusy) StartSpinner();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsBusy):
                if (_viewModel!.IsBusy) StartSpinner(); else StopSpinner();
                break;
            case nameof(MainViewModel.IsTitleVisible):
            case nameof(MainViewModel.IsArtistVisible):
            case nameof(MainViewModel.IsAlbumVisible):
            case nameof(MainViewModel.IsYearVisible):
            case nameof(MainViewModel.IsGenreVisible):
            case nameof(MainViewModel.IsDurationVisible):
                ApplyColumnVisibility();
                break;
        }
    }

    private void ApplyColumnVisibility()
    {
        if (_viewModel == null) return;
        TrackList.Columns[0].IsVisible = _viewModel.IsTitleVisible;
        TrackList.Columns[1].IsVisible = _viewModel.IsArtistVisible;
        TrackList.Columns[2].IsVisible = _viewModel.IsAlbumVisible;
        TrackList.Columns[3].IsVisible = _viewModel.IsYearVisible;
        TrackList.Columns[4].IsVisible = _viewModel.IsGenreVisible;
        TrackList.Columns[5].IsVisible = _viewModel.IsDurationVisible;
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

    // ── Context menus ─────────────────────────────────────────────────────────

    private void BuildMenus()
    {
        if (_viewModel == null) return;
        var vm = _viewModel;

        _columnMenu = new ContextMenu();
        _columnMenu.Items.Add(MakeToggleItem("Title",    () => vm.IsTitleVisible,    v => vm.IsTitleVisible = v));
        _columnMenu.Items.Add(MakeToggleItem("Artist",   () => vm.IsArtistVisible,   v => vm.IsArtistVisible = v));
        _columnMenu.Items.Add(MakeToggleItem("Album",    () => vm.IsAlbumVisible,    v => vm.IsAlbumVisible = v));
        _columnMenu.Items.Add(MakeToggleItem("Year",     () => vm.IsYearVisible,     v => vm.IsYearVisible = v));
        _columnMenu.Items.Add(MakeToggleItem("Genre",    () => vm.IsGenreVisible,    v => vm.IsGenreVisible = v));
        _columnMenu.Items.Add(MakeToggleItem("Duration", () => vm.IsDurationVisible, v => vm.IsDurationVisible = v));

        var getInfoItem = new MenuItem
        {
            Header = "Get Info",
            InputGesture = new KeyGesture(Key.I, KeyModifiers.Meta)
        };
        getInfoItem.Click += (_, _) => OpenTrackInfo();

        _trackMenu = new ContextMenu();
        _trackMenu.Items.Add(getInfoItem);
    }

    private void TrackList_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (IsInColumnHeader(e.Source as Visual))
        {
            _columnMenu.Open(TrackList);
            e.Handled = true;
        }
        else if (TrackList.SelectedItem is Track)
        {
            _trackMenu.Open(TrackList);
            e.Handled = true;
        }
    }

    private static bool IsInColumnHeader(Visual? v)
    {
        while (v != null)
        {
            if (v is DataGridColumnHeader) return true;
            v = v.GetVisualParent();
        }
        return false;
    }

    private static MenuItem MakeToggleItem(string header, Func<bool> getter, Action<bool> setter)
    {
        var item = new MenuItem { Header = header };
        SetCheckIcon(item, getter());
        item.Click += (_, _) =>
        {
            var newValue = !getter();
            setter(newValue);
            SetCheckIcon(item, newValue);
        };
        return item;
    }

    private static void SetCheckIcon(MenuItem item, bool visible)
    {
        item.Icon = visible
            ? new MaterialIcon { Kind = MaterialIconKind.Check, Width = 14, Height = 14 }
            : null;
    }

    // ── Keyboard ──────────────────────────────────────────────────────────────

    private void TrackList_KeyDown(object? sender, KeyEventArgs e)
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
        else if (e.Key == Key.Return)
        {
            if (TrackList.SelectedItem is Track track)
                _playlistControlViewModel.Play(track);
            e.Handled = true;
        }
    }

    // ── Track actions ─────────────────────────────────────────────────────────

    private void TrackList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (TrackList.SelectedItem is Track track)
        {
            _playlistControlViewModel.Play(track);
            e.Handled = true;
        }
    }

    private void OpenTrackInfo()
    {
        if (TrackList.SelectedItem is not Track track) return;
        var infoWindow = new TrackInfoWindow(track) { ShowInTaskbar = false };
        if (TopLevel.GetTopLevel(this) is Window owner)
            infoWindow.Show(owner);
        else
            infoWindow.Show();
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
