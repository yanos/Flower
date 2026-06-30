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

using Avalonia.Controls;
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
    public ICommand SortCommand { get; private set; } = null!;

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
        private set { _tracks = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusBarText)); }
    }

    public string StatusBarText
    {
        get
        {
            var tracks = _tracks;
            var songCount = tracks.Count;
            var albumCount = tracks.Select(t => t.Album).Where(a => !string.IsNullOrEmpty(a)).Distinct().Count();
            var total = TimeSpan.FromTicks(tracks.Sum(t => t.Duration.Ticks));
            var songStr = songCount == 1 ? "1 song" : $"{songCount:N0} songs";
            var albumStr = albumCount == 1 ? "1 album" : $"{albumCount:N0} albums";
            var durStr = total.TotalHours >= 1
                ? $"{(int)total.TotalHours}:{total.Minutes:D2}:{total.Seconds:D2}"
                : $"{total.Minutes}:{total.Seconds:D2}";
            return $"{songStr}  ·  {albumStr}  ·  {durStr}";
        }
    }

    // Busy state — increment/decrement via BeginBusy(message)
    private int _busyCount;
    private string? _busyMessage;

    public bool IsBusy => _busyCount > 0;
    public string? BusyMessage => _busyMessage;

    private IDisposable BeginBusy(string? message = null)
    {
        _busyMessage = message;
        Interlocked.Increment(ref _busyCount);
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(BusyMessage));
        return new BusyScope(this);
    }

    private sealed class BusyScope : IDisposable
    {
        private readonly MainViewModel _vm;
        internal BusyScope(MainViewModel vm) => _vm = vm;
        public void Dispose()
        {
            if (Interlocked.Decrement(ref _vm._busyCount) == 0)
                Dispatcher.UIThread.Post(() =>
                {
                    _vm._busyMessage = null;
                    _vm.OnPropertyChanged(nameof(IsBusy));
                    _vm.OnPropertyChanged(nameof(BusyMessage));
                });
        }
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

    // Column visibility + GridLength widths for header and item rows
    private bool _isTitleVisible;
    public bool IsTitleVisible
    {
        get => _isTitleVisible;
        set { _isTitleVisible = value; OnPropertyChanged(); OnPropertyChanged(nameof(TitleColWidth)); SaveColumnSettings(); }
    }

    private bool _isArtistVisible;
    public bool IsArtistVisible
    {
        get => _isArtistVisible;
        set { _isArtistVisible = value; OnPropertyChanged(); OnPropertyChanged(nameof(ArtistColWidth)); SaveColumnSettings(); }
    }

    private bool _isAlbumVisible;
    public bool IsAlbumVisible
    {
        get => _isAlbumVisible;
        set { _isAlbumVisible = value; OnPropertyChanged(); OnPropertyChanged(nameof(AlbumColWidth)); SaveColumnSettings(); }
    }

    private bool _isYearVisible;
    public bool IsYearVisible
    {
        get => _isYearVisible;
        set { _isYearVisible = value; OnPropertyChanged(); OnPropertyChanged(nameof(YearColWidth)); SaveColumnSettings(); }
    }

    private bool _isGenreVisible;
    public bool IsGenreVisible
    {
        get => _isGenreVisible;
        set { _isGenreVisible = value; OnPropertyChanged(); OnPropertyChanged(nameof(GenreColWidth)); SaveColumnSettings(); }
    }

    private bool _isDurationVisible;
    public bool IsDurationVisible
    {
        get => _isDurationVisible;
        set { _isDurationVisible = value; OnPropertyChanged(); OnPropertyChanged(nameof(DurationColWidth)); SaveColumnSettings(); }
    }

    // GridLength for each column — consumed by both the header and item DataTemplate
    public GridLength TitleColWidth    => IsTitleVisible    ? new GridLength(2,   GridUnitType.Star) : new GridLength(0);
    public GridLength ArtistColWidth   => IsArtistVisible   ? new GridLength(1.5, GridUnitType.Star) : new GridLength(0);
    public GridLength AlbumColWidth    => IsAlbumVisible    ? new GridLength(1.5, GridUnitType.Star) : new GridLength(0);
    public GridLength YearColWidth     => IsYearVisible     ? new GridLength(60)                     : new GridLength(0);
    public GridLength GenreColWidth    => IsGenreVisible    ? new GridLength(100)                    : new GridLength(0);
    public GridLength DurationColWidth => IsDurationVisible ? new GridLength(80)                     : new GridLength(0);

    // Column header labels with sort indicator
    private string? _sortProperty;
    private bool _sortDescending;

    private string SortArrow(string prop) => _sortProperty == prop ? (_sortDescending ? " ↓" : " ↑") : "";
    public string TitleHeader    => $"Title{SortArrow("Title")}";
    public string ArtistHeader   => $"Artist{SortArrow("Artists")}";
    public string AlbumHeader    => $"Album{SortArrow("Album")}";
    public string YearHeader     => $"Year{SortArrow("Year")}";
    public string GenreHeader    => $"Genre{SortArrow("Genre")}";
    public string DurationHeader => $"Duration{SortArrow("Duration")}";

    private void Sort(string? property)
    {
        if (property == null) return;
        _sortDescending = _sortProperty == property && !_sortDescending;
        _sortProperty = property;
        OnPropertyChanged(nameof(TitleHeader));
        OnPropertyChanged(nameof(ArtistHeader));
        OnPropertyChanged(nameof(AlbumHeader));
        OnPropertyChanged(nameof(YearHeader));
        OnPropertyChanged(nameof(GenreHeader));
        OnPropertyChanged(nameof(DurationHeader));
        ScheduleFilter();
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
        SortCommand = new RelayCommand<string>(Sort);

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
                _allTracks.Select(t => t.Album!).Where(a => !string.IsNullOrEmpty(a)).Distinct().OrderBy(a => a));
        else if (_selectedSidebarItem?.Kind == SidebarItemKind.Artists)
            SubListItems = new ObservableCollection<string>(
                _allTracks.Select(t => t.Artists!).Where(a => !string.IsNullOrEmpty(a)).Distinct().OrderBy(a => a));
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
        using var _ = BeginBusy("Rebuilding library…");
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
            var sortProp = _sortProperty;
            var sortDesc = _sortDescending;
            var results = await Task.Run(() =>
            {
                IEnumerable<Track> filtered = string.IsNullOrWhiteSpace(text)
                    ? baseTracks
                    : baseTracks.Where(t =>
                        t.Title?.Contains(text, StringComparison.OrdinalIgnoreCase) == true ||
                        t.Artists?.Contains(text, StringComparison.OrdinalIgnoreCase) == true ||
                        t.Album?.Contains(text, StringComparison.OrdinalIgnoreCase) == true ||
                        t.Genre?.Contains(text, StringComparison.OrdinalIgnoreCase) == true);

                if (sortProp == null) return filtered.ToList();
                return sortProp switch
                {
                    "Title"    => sortDesc ? filtered.OrderByDescending(t => t.Title).ToList()    : filtered.OrderBy(t => t.Title).ToList(),
                    "Artists"  => sortDesc ? filtered.OrderByDescending(t => t.Artists).ToList()  : filtered.OrderBy(t => t.Artists).ToList(),
                    "Album"    => sortDesc ? filtered.OrderByDescending(t => t.Album).ToList()    : filtered.OrderBy(t => t.Album).ToList(),
                    "Year"     => sortDesc ? filtered.OrderByDescending(t => t.Year).ToList()     : filtered.OrderBy(t => t.Year).ToList(),
                    "Genre"    => sortDesc ? filtered.OrderByDescending(t => t.Genre).ToList()    : filtered.OrderBy(t => t.Genre).ToList(),
                    "Duration" => sortDesc ? filtered.OrderByDescending(t => t.Duration).ToList() : filtered.OrderBy(t => t.Duration).ToList(),
                    _ => filtered.ToList()
                };
            }, token);

            if (token.IsCancellationRequested) return;

            Tracks = new ObservableCollection<Track>(results);
        }
        catch (OperationCanceledException) { }
    }
}
