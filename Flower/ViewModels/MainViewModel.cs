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

using Flower.Controls;
using Flower.Importer;
using Flower.Models;
using Flower.Persistence;
using Flower.Services;

using Material.Icons;

namespace Flower.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly PlaylistControlViewModel _playlistControlViewModel;
    private readonly ColumnVisibilityStore    _columnVisibilityStore;
    private AppSettings? _appSettings;
    private IMusicImporter? _importer;
    private MainPlaylist?      _mainPlaylist;

    public ICommand? OpenDatabaseLocationCommand { get; private set; }
    public ICommand? RebuildDatabaseCommand      { get; private set; }
    public ICommand? SortByColumnCommand         { get; private set; }
    public ICommand? OpenSettingsCommand         { get; private set; }

    public event EventHandler? SettingsRequested;
    public event EventHandler<Track>? NavigateToTrackRequested;

    public Library Library { get; private set; }

    public IReadOnlyList<string> LibraryPaths => _appSettings?.LibraryPaths ?? [];

    // ── Selection ─────────────────────────────────────────────────────────────

    public Track? SelectedTrack
    {
        get => _playlistControlViewModel.SelectedTrack;
        set => _playlistControlViewModel.SelectedTrack = value;
    }

    public Track? CurrentlyPlayingTrack => _playlistControlViewModel.CurrentlyPlayingTrack;

    // ── Rows (flat list for MusicListView) ────────────────────────────────────

    private ObservableCollection<TrackRowViewModel> _rows = new();
    public ObservableCollection<TrackRowViewModel> Rows
    {
        get => _rows;
        private set { _rows = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusBarText)); }
    }

    // ── Status bar ────────────────────────────────────────────────────────────

    private List<Track> _currentFilteredTracks = new();

    public IReadOnlyList<Track> DisplayedTracks => _currentFilteredTracks;

    public string StatusBarText
    {
        get
        {
            var tracks    = _currentFilteredTracks;
            var songCount = tracks.Count;
            var albumCount = tracks.Select(t => t.Album).Where(a => !string.IsNullOrEmpty(a)).Distinct().Count();
            var total     = TimeSpan.FromTicks(tracks.Sum(t => t.Duration.Ticks));
            var songStr   = songCount == 1 ? "1 song"   : $"{songCount:N0} songs";
            var albumStr  = albumCount == 1 ? "1 album" : $"{albumCount:N0} albums";
            var durStr    = total.TotalHours >= 1
                ? $"{(int)total.TotalHours}:{total.Minutes:D2}:{total.Seconds:D2}"
                : $"{total.Minutes}:{total.Seconds:D2}";
            return $"{songStr}  ·  {albumStr}  ·  {durStr}";
        }
    }

    // ── Busy state ────────────────────────────────────────────────────────────

    private int     _busyCount;
    private string? _busyMessage;

    public bool    IsBusy      => _busyCount > 0;
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

    // ── Sort state ────────────────────────────────────────────────────────────

    private string _sortColumn    = "TrackNumber";
    private bool   _sortAscending = true;

    public string SortColumn
    {
        get => _sortColumn;
        private set { _sortColumn = value; OnPropertyChanged(); }
    }

    public bool SortAscending
    {
        get => _sortAscending;
        private set { _sortAscending = value; OnPropertyChanged(); }
    }

    // ── Filter ────────────────────────────────────────────────────────────────

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

    // ── Sidebar ───────────────────────────────────────────────────────────────

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
            if (value != null && !value.IsSelectable)
                return;
            _selectedSidebarItem = value;
            OnPropertyChanged();
            OnSidebarSelectionChanged();
        }
    }

    public bool IsSubListVisible =>
        _selectedSidebarItem?.Kind == SidebarItemKind.Albums ||
        _selectedSidebarItem?.Kind == SidebarItemKind.Artists;

    public bool IsShowingDeviceDetail => _selectedSidebarItem?.Kind == SidebarItemKind.Device;
    public DiscoveredDevice? SelectedDevice => _selectedSidebarItem?.Device;

    // Identifies the currently displayed track list (Songs / a given album / artist / playlist)
    // so the view can remember a separate scroll position and selection for each one.
    public string CurrentViewKey => _selectedSidebarItem?.Kind switch
    {
        SidebarItemKind.Albums   => $"album:{_selectedSubItem}",
        SidebarItemKind.Artists  => $"artist:{_selectedSubItem}",
        SidebarItemKind.Playlist => $"playlist:{_selectedSidebarItem.Playlist?.Name}",
        _                        => "songs"
    };

    private ObservableCollection<string> _subListItems = new();
    public ObservableCollection<string> SubListItems
    {
        get => _subListItems;
        private set { _subListItems = value; OnPropertyChanged(); }
    }

    private string? _selectedSubItem;
    private string? _lastSelectedAlbum;
    private string? _lastSelectedArtist;

    public string? SelectedSubItem
    {
        get => _selectedSubItem;
        set
        {
            _selectedSubItem = value;
            RememberSubItemSelection(value);
            OnPropertyChanged();
            ScheduleFilter();
        }
    }

    private void RememberSubItemSelection(string? value)
    {
        if (value == null)
            return;
        switch (_selectedSidebarItem?.Kind)
        {
            case SidebarItemKind.Albums:  _lastSelectedAlbum  = value; break;
            case SidebarItemKind.Artists: _lastSelectedArtist = value; break;
        }
    }

    // ── Constructors ──────────────────────────────────────────────────────────

    public MainViewModel() { }

    public MainViewModel(
        PlaylistControlViewModel playlistControlViewModel,
        Library library,
        ColumnVisibilityStore columnVisibilityStore,
        AppSettings appSettings,
        IMusicImporter importer,
        MainPlaylist mainPlaylist,
        NetworkDiscoveryService networkDiscovery)
    {
        Library                = library;
        _playlistControlViewModel = playlistControlViewModel;
        _columnVisibilityStore = columnVisibilityStore;
        _appSettings           = appSettings;
        _importer              = importer;
        _mainPlaylist          = mainPlaylist;

        OpenDatabaseLocationCommand = new RelayCommand(OpenDatabaseLocation);
        RebuildDatabaseCommand      = new AsyncRelayCommand(RebuildDatabaseAsync);
        SortByColumnCommand         = new RelayCommand<string>(SortByColumn);
        OpenSettingsCommand         = new RelayCommand(() => SettingsRequested?.Invoke(this, EventArgs.Empty));

        BuildSidebarItems();
        PopulateTracks();

        library.TracksUpdated += (_, _) =>
            Dispatcher.UIThread.Post(PopulateTracks);

        networkDiscovery.DeviceDiscovered += (_, device) =>
            Dispatcher.UIThread.Post(() => AddOrUpdateDeviceSidebarItem(device));
        networkDiscovery.DeviceLost += (_, instanceName) =>
            Dispatcher.UIThread.Post(() => RemoveDeviceSidebarItem(instanceName));

        _playlistControlViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaylistControlViewModel.SelectedTrack))
                OnPropertyChanged(nameof(SelectedTrack));
            if (e.PropertyName == nameof(PlaylistControlViewModel.CurrentlyPlayingTrack))
            {
                OnPropertyChanged(nameof(CurrentlyPlayingTrack));
                UpdatePlayingIndicators();
            }
        };
    }

    // ── Sort ──────────────────────────────────────────────────────────────────

    private void SortByColumn(string? columnId)
    {
        if (columnId == null)
            return;
        if (SortColumn == columnId)
            SortAscending = !SortAscending;
        else
        {
            SortColumn    = columnId;
            SortAscending = true;
        }
        ScheduleFilter();
    }

    // ── Playing indicators ────────────────────────────────────────────────────

    private void UpdatePlayingIndicators()
    {
        var playing = CurrentlyPlayingTrack;
        foreach (var row in _rows)
            row.IsCurrentlyPlaying = row.Track.Path == playing?.Path;
    }

    // ── Sidebar ───────────────────────────────────────────────────────────────

    private void BuildSidebarItems()
    {
        _sidebarItems.Clear();
        _sidebarItems.Add(new SidebarItem(SidebarItemKind.Header,  "Library"));
        _sidebarItems.Add(new SidebarItem(SidebarItemKind.Songs,   "Songs",   MaterialIconKind.MusicNote));
        _sidebarItems.Add(new SidebarItem(SidebarItemKind.Albums,  "Albums",  MaterialIconKind.Album));
        _sidebarItems.Add(new SidebarItem(SidebarItemKind.Artists, "Artists", MaterialIconKind.AccountMusic));

        if (Library.Playlists.Count > 0)
        {
            _sidebarItems.Add(new SidebarItem(SidebarItemKind.Header, "Playlists"));
            foreach (var pl in Library.Playlists)
                _sidebarItems.Add(new SidebarItem(SidebarItemKind.Playlist, pl.Name, MaterialIconKind.PlaylistPlay, pl));
        }

        _selectedSidebarItem = _sidebarItems.FirstOrDefault(i => i.Kind == SidebarItemKind.Songs);
        OnPropertyChanged(nameof(SelectedSidebarItem));
    }

    // Mirrors CreatePlaylistWithTrack's incremental _sidebarItems.Add(...) pattern:
    // devices arrive one at a time from NetworkDiscoveryService, so the "Devices"
    // section is built up live rather than as part of BuildSidebarItems().
    private void AddOrUpdateDeviceSidebarItem(DiscoveredDevice device)
    {
        var existing = _sidebarItems.FirstOrDefault(i =>
            i.Kind == SidebarItemKind.Device && i.Device?.InstanceName == device.InstanceName);
        if (existing != null)
        {
            existing.Name = device.Alias;
            return;
        }

        if (_sidebarItems.All(i => i.Kind != SidebarItemKind.Device))
            _sidebarItems.Add(new SidebarItem(SidebarItemKind.Header, "Devices"));

        _sidebarItems.Add(new SidebarItem(SidebarItemKind.Device, device.Alias, MaterialIconKind.Laptop, device: device));
    }

    private void RemoveDeviceSidebarItem(string instanceName)
    {
        var item = _sidebarItems.FirstOrDefault(i =>
            i.Kind == SidebarItemKind.Device && i.Device?.InstanceName == instanceName);
        if (item == null)
            return;

        if (SelectedSidebarItem == item)
            SelectedSidebarItem = _sidebarItems.FirstOrDefault(i => i.Kind == SidebarItemKind.Songs);

        _sidebarItems.Remove(item);

        if (_sidebarItems.All(i => i.Kind != SidebarItemKind.Device))
        {
            var header = _sidebarItems.FirstOrDefault(i => i.Kind == SidebarItemKind.Header && i.Name == "Devices");
            if (header != null)
                _sidebarItems.Remove(header);
        }
    }

    public async Task CreatePlaylistWithTrack(Track? track)
    {
        var tracks   = track != null ? new List<Track> { track } : new List<Track>();
        var playlist = new Playlist("New Playlist", tracks);
        Library.AddPlaylist(playlist);

        if (_sidebarItems.All(i => i.Kind != SidebarItemKind.Playlist))
            _sidebarItems.Add(new SidebarItem(SidebarItemKind.Header, "Playlists"));

        var sidebarItem = new SidebarItem(SidebarItemKind.Playlist, playlist.Name, MaterialIconKind.PlaylistPlay, playlist)
        {
            IsEditing = true
        };
        _sidebarItems.Add(sidebarItem);

        SelectedSidebarItem = sidebarItem;

        await new PlaylistStore().SaveAsync(Library.Playlists);
    }

    public async Task AddTrackToPlaylist(Track track, Playlist playlist)
    {
        playlist.AppendTrack(track);
        if (_selectedSidebarItem?.Playlist == playlist)
            ScheduleFilter();

        await new PlaylistStore().SaveAsync(Library.Playlists);
    }

    public async Task ReorderPlaylistTrack(Playlist playlist, Track dragged, Track? insertBefore)
    {
        if (!playlist.Tracks.Remove(dragged))
            return;

        var index = insertBefore != null ? playlist.Tracks.IndexOf(insertBefore) : -1;
        playlist.Tracks.Insert(index < 0 ? playlist.Tracks.Count : index, dragged);

        if (_selectedSidebarItem?.Playlist == playlist)
            ScheduleFilter();

        await new PlaylistStore().SaveAsync(Library.Playlists);
    }

    private void OnSidebarSelectionChanged()
    {
        OnPropertyChanged(nameof(IsSubListVisible));
        OnPropertyChanged(nameof(IsShowingDeviceDetail));
        OnPropertyChanged(nameof(SelectedDevice));
        RebuildSubListItems();

        var lastSelected = _selectedSidebarItem?.Kind switch
        {
            SidebarItemKind.Albums  => _lastSelectedAlbum,
            SidebarItemKind.Artists => _lastSelectedArtist,
            _ => null
        };
        _selectedSubItem = lastSelected != null && _subListItems.Contains(lastSelected)
            ? lastSelected
            : _subListItems.FirstOrDefault();
        RememberSubItemSelection(_selectedSubItem);
        OnPropertyChanged(nameof(SelectedSubItem));

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
            SidebarItemKind.Device
                => new List<Track>(),
            _ => _allTracks
        };
    }

    // ── Database ops ──────────────────────────────────────────────────────────

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
        if (_importer == null || _mainPlaylist == null)
            return;
        using var _ = BeginBusy("Rebuilding library…");
        var libraryPaths = _appSettings?.LibraryPaths;
        var freshTracks = await _importer.ImportAsync(libraryPaths);
        _mainPlaylist.ReplaceAll(freshTracks);
        Library.UpdateTracks(freshTracks);
        await new LibraryStore().SaveAsync(freshTracks);
    }

    public async Task UpdateLibraryPathsAsync(List<string> paths)
    {
        _appSettings ??= new AppSettings();
        _appSettings.LibraryPaths = paths;
        await new AppSettingsStore().SaveAsync(_appSettings);
        await RebuildDatabaseAsync();
    }

    // Mobile has no library-paths UI to rescan as a side effect of (desktop's
    // SettingsWindow "Save & Rescan" button) — it needs to trigger a rescan directly,
    // e.g. after granting a previously-denied Android media permission.
    public Task RescanLibraryAsync() => RebuildDatabaseAsync();

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
            await RebuildRowsAsync(token);
        }
        catch (OperationCanceledException) { }
    }

    private async Task RebuildRowsAsync(CancellationToken token)
    {
        var text       = _filterText;
        // Playlists have a user-defined (drag-reorderable) track order rather
        // than a sortable one, so ignore the column sort while viewing one.
        var sortCol    = _selectedSidebarItem?.Kind == SidebarItemKind.Playlist ? "PlaylistOrder" : _sortColumn;
        var sortAsc    = _sortAscending;
        var playing    = CurrentlyPlayingTrack;
        var baseTracks = GetBaseTracksForFilter();

        var rows = await Task.Run(() =>
            TrackListBuilder.Build(baseTracks, text, sortCol, sortAsc, playing), token);

        if (token.IsCancellationRequested)
            return;

        _currentFilteredTracks = rows.Select(r => r.Track).ToList();
        Rows = new ObservableCollection<TrackRowViewModel>(rows);
        OnPropertyChanged(nameof(StatusBarText));
    }

    // ── Go to currently playing track (Cmd/Ctrl+L) ───────────────────────────

    public async Task GoToCurrentlyPlayingTrackAsync()
    {
        var track = CurrentlyPlayingTrack;
        if (track == null)
            return;

        if (_currentFilteredTracks.Any(t => t.Path == track.Path))
        {
            NavigateToTrackRequested?.Invoke(this, track);
            return;
        }

        // Hidden by an active search and/or being scoped to the wrong
        // playlist/album/artist — fix whichever applies, then rebuild
        // immediately (bypassing the normal debounce) so the jump feels instant.
        if (!string.IsNullOrEmpty(FilterText))
            FilterText = null;

        switch (_selectedSidebarItem?.Kind)
        {
            case SidebarItemKind.Playlist
                when _selectedSidebarItem.Playlist?.Tracks.Any(t => t.Path == track.Path) != true:
                var songs = _sidebarItems.FirstOrDefault(i => i.Kind == SidebarItemKind.Songs);
                if (songs != null)
                    SelectedSidebarItem = songs;
                break;
            case SidebarItemKind.Albums:
                SelectedSubItem = track.Album;
                break;
            case SidebarItemKind.Artists:
                SelectedSubItem = track.Artists;
                break;
        }

        _filterCts?.Cancel();
        _filterCts = new CancellationTokenSource();
        try { await RebuildRowsAsync(_filterCts.Token); }
        catch (OperationCanceledException) { return; }

        NavigateToTrackRequested?.Invoke(this, track);
    }
}
