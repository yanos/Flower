using System;
using System.ComponentModel;

using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;

using CommunityToolkit.Mvvm.DependencyInjection;

using Flower.Converters;
using Flower.Models;
using Flower.ViewModels;

using Material.Icons;
using Material.Icons.Avalonia;

namespace Flower.Views;

public partial class MainView : UserControl
{
    private readonly PlaylistControlViewModel _playlistControlViewModel;
    private MainViewModel? _viewModel;

    private readonly DataGridTextColumn _titleColumn;
    private readonly DataGridTextColumn _artistColumn;
    private readonly DataGridTextColumn _albumColumn;
    private readonly DataGridTextColumn _yearColumn;
    private readonly DataGridTextColumn _genreColumn;
    private readonly DataGridTextColumn _durationColumn;

    public MainView()
    {
        InitializeComponent();

        _playlistControlViewModel = Ioc.Default.GetService<PlaylistControlViewModel>()!;

        var durationConverter = new DurationConverter();

        _titleColumn    = new DataGridTextColumn { Header = "Title",    Binding = new Binding(nameof(Track.Title)),    Width = new DataGridLength(2, DataGridLengthUnitType.Star) };
        _artistColumn   = new DataGridTextColumn { Header = "Artist",   Binding = new Binding(nameof(Track.Artists)),  Width = new DataGridLength(1.5, DataGridLengthUnitType.Star) };
        _albumColumn    = new DataGridTextColumn { Header = "Album",    Binding = new Binding(nameof(Track.Album)),    Width = new DataGridLength(1.5, DataGridLengthUnitType.Star) };
        _yearColumn     = new DataGridTextColumn { Header = "Year",     Binding = new Binding(nameof(Track.Year)),     Width = new DataGridLength(60) };
        _genreColumn    = new DataGridTextColumn { Header = "Genre",    Binding = new Binding(nameof(Track.Genre)),    Width = new DataGridLength(100) };
        _durationColumn = new DataGridTextColumn { Header = "Duration", Binding = new Binding("_DebugDurationTicks"), Width = new DataGridLength(120) };

        TrackGrid.Columns.Add(_titleColumn);
        TrackGrid.Columns.Add(_artistColumn);
        TrackGrid.Columns.Add(_albumColumn);
        TrackGrid.Columns.Add(_yearColumn);
        TrackGrid.Columns.Add(_genreColumn);
        TrackGrid.Columns.Add(_durationColumn);

        TrackGrid.KeyDown += TrackGrid_KeyDown;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel != null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _viewModel = DataContext as MainViewModel;

        if (_viewModel != null)
        {
            ApplyColumnVisibility();
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            BuildContextMenu();
        }
    }

    private void ApplyColumnVisibility()
    {
        if (_viewModel == null) return;
        _titleColumn.IsVisible    = _viewModel.IsTitleVisible;
        _artistColumn.IsVisible   = _viewModel.IsArtistVisible;
        _albumColumn.IsVisible    = _viewModel.IsAlbumVisible;
        _yearColumn.IsVisible     = _viewModel.IsYearVisible;
        _genreColumn.IsVisible    = _viewModel.IsGenreVisible;
        _durationColumn.IsVisible = _viewModel.IsDurationVisible;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsTitleVisible):    _titleColumn.IsVisible    = _viewModel!.IsTitleVisible;    break;
            case nameof(MainViewModel.IsArtistVisible):   _artistColumn.IsVisible   = _viewModel!.IsArtistVisible;   break;
            case nameof(MainViewModel.IsAlbumVisible):    _albumColumn.IsVisible    = _viewModel!.IsAlbumVisible;    break;
            case nameof(MainViewModel.IsYearVisible):     _yearColumn.IsVisible     = _viewModel!.IsYearVisible;     break;
            case nameof(MainViewModel.IsGenreVisible):    _genreColumn.IsVisible    = _viewModel!.IsGenreVisible;    break;
            case nameof(MainViewModel.IsDurationVisible): _durationColumn.IsVisible = _viewModel!.IsDurationVisible; break;
        }
    }

    private void BuildContextMenu()
    {
        if (_viewModel == null) return;
        var vm = _viewModel;

        var menu = new ContextMenu();
        menu.Items.Add(MakeToggleItem("Title",    () => vm.IsTitleVisible,    v => vm.IsTitleVisible = v));
        menu.Items.Add(MakeToggleItem("Artist",   () => vm.IsArtistVisible,   v => vm.IsArtistVisible = v));
        menu.Items.Add(MakeToggleItem("Album",    () => vm.IsAlbumVisible,    v => vm.IsAlbumVisible = v));
        menu.Items.Add(MakeToggleItem("Year",     () => vm.IsYearVisible,     v => vm.IsYearVisible = v));
        menu.Items.Add(MakeToggleItem("Genre",    () => vm.IsGenreVisible,    v => vm.IsGenreVisible = v));
        menu.Items.Add(MakeToggleItem("Duration", () => vm.IsDurationVisible, v => vm.IsDurationVisible = v));

        TrackGrid.ContextMenu = menu;
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

    private void TrackGrid_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.I && e.KeyModifiers == KeyModifiers.Meta &&
            TrackGrid.SelectedItem is Track track)
        {
            var infoWindow = new TrackInfoWindow(track) { ShowInTaskbar = false };
            if (TopLevel.GetTopLevel(this) is Window owner)
                infoWindow.Show(owner);
            else
                infoWindow.Show();
            e.Handled = true;
        }
    }

    private void DataGrid_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (sender is DataGrid dataGrid && dataGrid.SelectedItem is Track selectedTrack)
        {
            _playlistControlViewModel.Play(selectedTrack);
            e.Handled = true;
        }
    }

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
