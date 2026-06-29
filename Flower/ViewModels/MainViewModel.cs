using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

using Avalonia.Threading;

using CommunityToolkit.Mvvm.Input;

using Flower.Models;
using Flower.Persistence;

using Material.Icons;

namespace Flower.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly PlaylistControlViewModel _playlistControlViewModel;
    private readonly ColumnVisibilityStore _columnVisibilityStore;
    private readonly ColumnVisibilitySettings _columnSettings;
    private Importer.Importer? _importer;
    private MainPlaylist? _mainPlaylist;

    public ICommand? OpenDatabaseLocationCommand { get; private set; }
    public ICommand? RebuildDatabaseCommand { get; private set; }

    public Library Library { get; private set; }

    public Track? SelectedTrack
    {
        get => _playlistControlViewModel.SelectedTrack;
        set => _playlistControlViewModel.SelectedTrack = value;
    }

    private ObservableCollection<Track> _tracks = new();
    public ObservableCollection<Track> Tracks
    {
        get => _tracks;
        private set { _tracks = value; OnPropertyChanged(); }
    }

    private List<Track> _allTracks = new();
    private CancellationTokenSource? _filterCts;

    private string? _filterText;
    public string? FilterText
    {
        get => _filterText;
        set
        {
            _filterText = value;
            OnPropertyChanged();
            ScheduleFilter();
        }
    }

    // Sidebar
    private ObservableCollection<SidebarItem> _sidebarItems = new();
    public ObservableCollection<SidebarItem> SidebarItems
    {
        get => _sidebarItems;
        private set { _sidebarItems = value; OnPropertyChanged(); }
    }

    private SidebarItem? _selectedSidebarItem;
    public SidebarItem? SelectedSidebarItem
    {
        get => _selectedSidebarItem;
        set
        {
            if (value != null && !value.IsSelectable) return;
            _selectedSidebarItem = value;
            OnPropertyChanged();
            OnSidebarSelectionChanged();
        }
    }

    public bool IsSubListVisible =>
        _selectedSidebarItem?.Kind == SidebarItemKind.Albums ||
        _selectedSidebarItem?.Kind == SidebarItemKind.Artists;

    private ObservableCollection<string> _subListItems = new();
    public ObservableCollection<string> SubListItems
    {
        get => _subListItems;
        private set { _subListItems = value; OnPropertyChanged(); }
    }

    private string? _selectedSubItem;
    public string? SelectedSubItem
    {
        get => _selectedSubItem;
        set
        {
            _selectedSubItem = value;
            OnPropertyChanged();
            ScheduleFilter();
        }
    }

    // Column visibility
    private bool _isTitleVisible;
    public bool IsTitleVisible
    {
        get => _isTitleVisible;
        set { _isTitleVisible = value; OnPropertyChanged(); SaveColumnSettings(); }
    }

    private bool _isArtistVisible;
    public bool IsArtistVisible
    {
        get => _isArtistVisible;
        set { _isArtistVisible = value; OnPropertyChanged(); SaveColumnSettings(); }
    }

    private bool _isAlbumVisible;
    public bool IsAlbumVisible
    {
        get => _isAlbumVisible;
        set { _isAlbumVisible = value; OnPropertyChanged(); SaveColumnSettings(); }
    }

    private bool _isYearVisible;
    public bool IsYearVisible
    {
        get => _isYearVisible;
        set { _isYearVisible = value; OnPropertyChanged(); SaveColumnSettings(); }
    }

    private bool _isGenreVisible;
    public bool IsGenreVisible
    {
        get => _isGenreVisible;
        set { _isGenreVisible = value; OnPropertyChanged(); SaveColumnSettings(); }
    }

    private bool _isDurationVisible;
    public bool IsDurationVisible
    {
        get => _isDurationVisible;
        set { _isDurationVisible = value; OnPropertyChanged(); SaveColumnSettings(); }
    }

    private void SaveColumnSettings()
    {
        _columnSettings.Title = _isTitleVisible;
        _columnSettings.Artist = _isArtistVisible;
        _columnSettings.Album = _isAlbumVisible;
        _columnSettings.Year = _isYearVisible;
        _columnSettings.Genre = _isGenreVisible;
        _columnSettings.Duration = _isDurationVisible;
        _ = _columnVisibilityStore.SaveAsync(_columnSettings);
    }

    public MainViewModel() { }

    public MainViewModel(
        PlaylistControlViewModel playlistControlViewModel,
        Library library,
        ColumnVisibilityStore columnVisibilityStore,
        Importer.Importer importer,
        MainPlaylist mainPlaylist)
    {
        Library = library;
        _playlistControlViewModel = playlistControlViewModel;
        _columnVisibilityStore = columnVisibilityStore;
        _importer = importer;
        _mainPlaylist = mainPlaylist;

        OpenDatabaseLocationCommand = new RelayCommand(OpenDatabaseLocation);
        RebuildDatabaseCommand = new AsyncRelayCommand(RebuildDatabaseAsync);

        _columnSettings = columnVisibilityStore.Load();
        _isTitleVisible = _columnSettings.Title;
        _isArtistVisible = _columnSettings.Artist;
        _isAlbumVisible = _columnSettings.Album;
        _isYearVisible = _columnSettings.Year;
        _isGenreVisible = _columnSettings.Genre;
        _isDurationVisible = _columnSettings.Duration;

        BuildSidebarItems();
        PopulateTracks();

        library.TracksUpdated += (s, e) =>
            Dispatcher.UIThread.Post(PopulateTracks);

        _playlistControlViewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(_playlistControlViewModel.SelectedTrack))
                OnPropertyChanged(nameof(SelectedTrack));
        };
    }

    private void BuildSidebarItems()
    {
        _sidebarItems.Clear();
        _sidebarItems.Add(new SidebarItem(SidebarItemKind.Header, "Library"));
        _sidebarItems.Add(new SidebarItem(SidebarItemKind.Songs,   "Songs",   MaterialIconKind.MusicNote));
        _sidebarItems.Add(new SidebarItem(SidebarItemKind.Albums,  "Albums",  MaterialIconKind.Album));
        _sidebarItems.Add(new SidebarItem(SidebarItemKind.Artists, "Artists", MaterialIconKind.AccountMusic));

        if (Library.Playlists.Count > 0)
        {
            _sidebarItems.Add(new SidebarItem(SidebarItemKind.Header, "Playlists"));
            foreach (var playlist in Library.Playlists)
                _sidebarItems.Add(new SidebarItem(SidebarItemKind.Playlist, playlist.Name, MaterialIconKind.PlaylistPlay, playlist));
        }

        _selectedSidebarItem = _sidebarItems.FirstOrDefault(i => i.Kind == SidebarItemKind.Songs);
        OnPropertyChanged(nameof(SelectedSidebarItem));
    }

    private void OnSidebarSelectionChanged()
    {
        OnPropertyChanged(nameof(IsSubListVisible));
        _selectedSubItem = null;
        OnPropertyChanged(nameof(SelectedSubItem));
        RebuildSubListItems();
        ScheduleFilter();
    }

    private void RebuildSubListItems()
    {
        if (_selectedSidebarItem?.Kind == SidebarItemKind.Albums)
            SubListItems = new ObservableCollection<string>(
                _allTracks.Select(t => t.Album).Where(a => !string.IsNullOrEmpty(a)).Distinct().OrderBy(a => a));
        else if (_selectedSidebarItem?.Kind == SidebarItemKind.Artists)
            SubListItems = new ObservableCollection<string>(
                _allTracks.Select(t => t.Artists).Where(a => !string.IsNullOrEmpty(a)).Distinct().OrderBy(a => a));
        else
            SubListItems = new ObservableCollection<string>();
    }

    private List<Track> GetBaseTracksForFilter()
    {
        return _selectedSidebarItem?.Kind switch
        {
            SidebarItemKind.Playlist when _selectedSidebarItem.Playlist != null
                => new List<Track>(_selectedSidebarItem.Playlist.Tracks),
            SidebarItemKind.Albums when _selectedSubItem != null
                => _allTracks.Where(t => t.Album == _selectedSubItem).ToList(),
            SidebarItemKind.Albums
                => new List<Track>(),
            SidebarItemKind.Artists when _selectedSubItem != null
                => _allTracks.Where(t => t.Artists == _selectedSubItem).ToList(),
            SidebarItemKind.Artists
                => new List<Track>(),
            _ => _allTracks
        };
    }

    private void OpenDatabaseLocation()
    {
        var dir = Path.GetDirectoryName(LibraryStore.StorePath)!;
        Directory.CreateDirectory(dir);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            Process.Start(new ProcessStartInfo { FileName = "open", ArgumentList = { dir } });
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", ArgumentList = { dir } });
        else
            Process.Start(new ProcessStartInfo { FileName = "xdg-open", ArgumentList = { dir } });
    }

    private async Task RebuildDatabaseAsync()
    {
        if (_importer == null || _mainPlaylist == null) return;
        var freshTracks = await Task.Run(() => _importer.Import());
        _mainPlaylist.ReplaceAll(freshTracks);
        Library.UpdateTracks(freshTracks);
        await new LibraryStore().SaveAsync(freshTracks);
    }

    private void PopulateTracks()
    {
        _allTracks = new List<Track>(Library.Tracks);
        RebuildSubListItems();
        ScheduleFilter();
    }

    private async void ScheduleFilter()
    {
        _filterCts?.Cancel();
        _filterCts = new CancellationTokenSource();
        var token = _filterCts.Token;

        try
        {
            await Task.Delay(250, token);

            var text = _filterText;
            var baseTracks = GetBaseTracksForFilter();
            var results = await Task.Run(() =>
            {
                if (string.IsNullOrWhiteSpace(text))
                    return baseTracks;

                return baseTracks.Where(t =>
                    t.Title?.Contains(text, StringComparison.OrdinalIgnoreCase) == true ||
                    t.Artists?.Contains(text, StringComparison.OrdinalIgnoreCase) == true ||
                    t.Album?.Contains(text, StringComparison.OrdinalIgnoreCase) == true ||
                    t.Genre?.Contains(text, StringComparison.OrdinalIgnoreCase) == true)
                    .ToList();
            }, token);

            if (token.IsCancellationRequested) return;

            Tracks = new ObservableCollection<Track>(results);
        }
        catch (OperationCanceledException) { }
    }
}
