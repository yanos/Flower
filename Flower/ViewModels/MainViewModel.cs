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

// Raised by DeletePlaylistAsync before it actually deletes anything - the view
// is expected to confirm with the user and report back via Confirmed, the same
// TaskCompletionSource-based handoff PlaylistConflictEventArgs uses (see
// Services/PlaylistSyncService.cs) so the ViewModel never has to know a dialog
// is involved.
public sealed class DeletePlaylistConfirmationEventArgs : EventArgs
{
    public required Playlist Playlist { get; init; }
    public required TaskCompletionSource<bool> Confirmed { get; init; }
}

public partial class MainViewModel : ViewModelBase
{
    private readonly PlaylistControlViewModel _playlistControlViewModel;
    private AppSettings? _appSettings;
    private IMusicImporter? _importer;
    private MainPlaylist?      _mainPlaylist;
    private PlaylistSyncService? _playlistSyncService;

    // Fingerprints of devices already sync'd (or currently syncing) this app
    // session, so DeviceDiscovered re-firing for the same peer (e.g. once with the
    // mDNS-name fallback alias, again once /info resolves) doesn't start a second,
    // overlapping sync session. Cleared per-device on DeviceLost so a peer that
    // drops off and comes back later gets a fresh sync. Concurrent dictionary
    // because discovery events aren't guaranteed to arrive on one fixed thread.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _syncedDeviceFingerprints = new();

    public ICommand? OpenAppDataLocationCommand  { get; private set; }
    public ICommand? RebuildDatabaseCommand      { get; private set; }
    public ICommand? SortByColumnCommand         { get; private set; }
    public ICommand? OpenSettingsCommand         { get; private set; }
    public ICommand? OpenColumnSelectorCommand   { get; private set; }
    public ICommand? NewPlaylistCommand          { get; private set; }
    public ICommand? RenamePlaylistCommand       { get; private set; }
    public ICommand? DeletePlaylistCommand       { get; private set; }

    // Concrete-typed twins of RenamePlaylistCommand/DeletePlaylistCommand, kept
    // alongside the public ICommand? properties above (same pattern as the rest
    // of this class) purely so OnSidebarSelectionChanged can re-query CanExecute
    // - a plain ICommand reference cannot call NotifyCanExecuteChanged itself.
    private RelayCommand?      _renamePlaylistCommand;
    private AsyncRelayCommand? _deletePlaylistCommand;

    public event EventHandler? SettingsRequested;
    public event EventHandler? ColumnSelectorRequested;
    public event EventHandler<Track>? NavigateToTrackRequested;
    public event EventHandler<PlaylistConflictEventArgs>? PlaylistConflictRequested;

    // Raised by the "Playlist > Rename Playlist" main-menu command - unlike
    // deleting, renaming needs the sidebar's own inline-rename textbox (see
    // MainView.axaml.cs's BeginRename), which is a View concern this ViewModel
    // cannot reach directly.
    public event EventHandler? RenamePlaylistRequested;

    // See DeletePlaylistConfirmationEventArgs above.
    public event EventHandler<DeletePlaylistConfirmationEventArgs>? DeletePlaylistConfirmationRequested;

    public Library Library { get; private set; }

    public IReadOnlyList<string> LibraryPaths => _appSettings?.LibraryPaths ?? [];

    // Whether to import per-track play counts from iTunes/Music.app on every
    // launch - see ITunesPlayCountImporter. Persisted immediately on change,
    // like SortArtistAlbumsByYear below, rather than gated behind Settings'
    // OK button (which is specifically about the library-paths list).
    public bool SyncPlayCountFromITunes
    {
        get => _appSettings?.SyncPlayCountFromITunes ?? false;
        set
        {
            _appSettings ??= new AppSettings();
            if (_appSettings.SyncPlayCountFromITunes == value)
                return;
            _appSettings.SyncPlayCountFromITunes = value;
            _ = new AppSettingsStore().SaveAsync(_appSettings);

            // Apply right away rather than only at the next launch, so turning
            // this on gives visible feedback immediately instead of looking
            // like it silently did nothing until the app is restarted.
            if (value)
                _ = SyncITunesPlayCountAsync();
        }
    }

    // Exports a fresh XML snapshot from Music.app (via AppleScript - see
    // ITunesPlayCountImporter) and applies its play counts to
    // Track.ImportedPlayCount. Shared by the SyncPlayCountFromITunes setter
    // above (apply-immediately-on-toggle) and App.axaml.cs's startup rescan
    // (apply-on-every-launch), both of which run this off the UI thread
    // already - BeginBusy drives the status bar spinner either way.
    public async Task SyncITunesPlayCountAsync()
    {
        using var _ = BeginBusy("Syncing play counts from Music.app…");
        await Task.Run(() => ITunesPlayCountImporter.Apply(Library.Tracks));
        // Same list, same Track instances - this call's only real purpose
        // here is firing TracksUpdated so the Plays column reflects the new
        // ImportedPlayCount values immediately.
        Library.UpdateTracks(Library.Tracks);
        await new LibraryStore().SaveAsync(Library.Tracks);
    }

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

    // The count itself is bumped synchronously (needed immediately regardless
    // of caller thread, to correctly track overlapping scopes), but the
    // notifications are dispatched - every prior caller happened to already be
    // on the UI thread (button-click commands, where AsyncRelayCommand runs
    // synchronously up to its first await), so this was never actually
    // exercised off it until SyncITunesPlayCountAsync started calling this
    // from within a background Task.Run: IsBusy's IsVisible binding "worked"
    // anyway (something else happened to force a UI-thread re-evaluation
    // around the same time), but BusyMessage's TextBlock silently never
    // updated - a real cross-thread notification bug, not just this one caller.
    private IDisposable BeginBusy(string? message = null)
    {
        Interlocked.Increment(ref _busyCount);
        Dispatcher.UIThread.Post(() =>
        {
            _busyMessage = message;
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(BusyMessage));
        });
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

    // Recently Added has its own independent sort state (defaulting to newest-first)
    // rather than sharing Songs/Albums/Artists' single sort column - so clicking a
    // header there doesn't change what Songs is sorted by, and vice versa.
    private string _recentlyAddedSortColumn    = "DateAdded";
    private bool   _recentlyAddedSortAscending = false;

    private bool IsViewingRecentlyAdded => _selectedSidebarItem?.Kind == SidebarItemKind.RecentlyAdded;

    public string SortColumn => IsViewingRecentlyAdded ? _recentlyAddedSortColumn : _sortColumn;

    public bool SortAscending => IsViewingRecentlyAdded ? _recentlyAddedSortAscending : _sortAscending;

    private bool _sortArtistAlbumsByYear;

    // When sorting by Artist, order each artist's albums by year (then disc/
    // track number within an album) instead of by whichever order they
    // happened to appear in - so an artist's discography reads
    // chronologically. Surfaced as a checkbox in ColumnSelectorWindow.
    public bool SortArtistAlbumsByYear
    {
        get => _sortArtistAlbumsByYear;
        set
        {
            if (_sortArtistAlbumsByYear == value)
                return;
            _sortArtistAlbumsByYear = value;
            OnPropertyChanged();
            _appSettings ??= new AppSettings();
            _appSettings.SortArtistAlbumsByYear = value;
            _ = new AppSettingsStore().SaveAsync(_appSettings);
            if (SortColumn == "Artist")
                ScheduleFilter();
        }
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
        // Keyed on the whole set (sorted, so order doesn't matter) rather than
        // just the primary item - otherwise two different multi-selections that
        // happen to share the same first-selected item would collide and
        // incorrectly share saved scroll/selection state in ApplyRows.
        SidebarItemKind.Albums        => $"album:{string.Join('\u0001', _selectedSubItems.OrderBy(s => s))}",
        SidebarItemKind.Artists       => $"artist:{string.Join('\u0001', _selectedSubItems.OrderBy(s => s))}",
        SidebarItemKind.Playlist      => $"playlist:{_selectedSidebarItem.Playlist?.Name}",
        SidebarItemKind.RecentlyAdded => "recently-added",
        _                             => "songs"
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
    private HashSet<string> _selectedSubItems = new();

    // The full multi-selection of album/artist names in SubList - drives both
    // the track-list union filter (GetBaseTracksForFilter) and what gets
    // dragged onto a playlist (GetTracksForSubListItems). SelectedSubItem below
    // stays the "primary" (first) item for single-item consumers.
    public IReadOnlyCollection<string> SelectedSubItems => _selectedSubItems;

    public string? SelectedSubItem
    {
        get => _selectedSubItem;
        set => ApplySubItemSelection(value != null ? new[] { value } : Array.Empty<string>());
    }

    // Used by SubList's multi-select drag/selection-sync code in MainView.axaml.cs.
    public void SetSelectedSubItems(IReadOnlyList<string> items) => ApplySubItemSelection(items);

    private void ApplySubItemSelection(IReadOnlyList<string> items)
    {
        _selectedSubItems = new HashSet<string>(items);
        _selectedSubItem  = items.Count > 0 ? items[0] : null;
        RememberSubItemSelection(_selectedSubItem);
        OnPropertyChanged(nameof(SelectedSubItem));
        OnPropertyChanged(nameof(SelectedSubItems));
        ScheduleFilter();
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
        AppSettings appSettings,
        IMusicImporter importer,
        MainPlaylist mainPlaylist,
        NetworkDiscoveryService networkDiscovery,
        PlaylistSyncService playlistSyncService)
    {
        Library                = library;
        _playlistControlViewModel = playlistControlViewModel;
        _appSettings           = appSettings;
        _importer              = importer;
        _mainPlaylist          = mainPlaylist;
        _playlistSyncService   = playlistSyncService;

        if (appSettings.SortColumn is { } savedSortColumn)
        {
            _sortColumn    = savedSortColumn;
            _sortAscending = appSettings.SortAscending;
        }
        _sortArtistAlbumsByYear = appSettings.SortArtistAlbumsByYear;

        OpenAppDataLocationCommand  = new RelayCommand(OpenAppDataLocation);
        RebuildDatabaseCommand      = new AsyncRelayCommand(RebuildDatabaseAsync);
        SortByColumnCommand         = new RelayCommand<string>(SortByColumn);
        OpenSettingsCommand         = new RelayCommand(() => SettingsRequested?.Invoke(this, EventArgs.Empty));
        OpenColumnSelectorCommand   = new RelayCommand(() => ColumnSelectorRequested?.Invoke(this, EventArgs.Empty));
        NewPlaylistCommand          = new AsyncRelayCommand(() => CreatePlaylistWithTrack(null));

        _renamePlaylistCommand = new RelayCommand(
            () => RenamePlaylistRequested?.Invoke(this, EventArgs.Empty),
            CanRenameOrDeleteSelectedPlaylist);
        RenamePlaylistCommand = _renamePlaylistCommand;

        _deletePlaylistCommand = new AsyncRelayCommand(DeleteSelectedPlaylistAsync, CanRenameOrDeleteSelectedPlaylist);
        DeletePlaylistCommand = _deletePlaylistCommand;

        BuildSidebarItems();
        PopulateTracks();

        library.TracksUpdated += (_, _) =>
            Dispatcher.UIThread.Post(PopulateTracks);
        library.PlaylistsUpdated += (_, _) =>
            Dispatcher.UIThread.Post(RefreshPlaylistSidebarItems);

        networkDiscovery.DeviceDiscovered += (_, device) =>
        {
            Dispatcher.UIThread.Post(() => AddOrUpdateDeviceSidebarItem(device));
            TriggerPlaylistSyncIfReady(device);
        };
        networkDiscovery.DeviceLost += (_, instanceName) =>
            Dispatcher.UIThread.Post(() => RemoveDeviceSidebarItem(instanceName));

        // On mobile, MainViewModel is still constructed (App.axaml.cs resolves it
        // unconditionally) but MainView - the only subscriber to
        // PlaylistConflictRequested - never is, since mobile shows MobileMainView
        // instead. Without this check, a conflict during a mobile-initiated sync
        // would await e.Resolution forever. Until mobile gets its own conflict UI,
        // fail safe by keeping the local version rather than hanging the sync.
        playlistSyncService.ConflictDetected += (_, e) =>
        {
            if (PlaylistConflictRequested == null)
            {
                e.Resolution.TrySetResult(PlaylistConflictChoice.KeepLocal);
                return;
            }
            Dispatcher.UIThread.Post(() => PlaylistConflictRequested?.Invoke(this, e));
        };

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

    // ── Playback ─────────────────────────────────────────────────────────────

    // Plays a specific track the user picked from whatever's currently displayed
    // (Songs, an album/artist drill-down, or a playlist), establishing that view's
    // current order as the Next/Previous queue - see PlaylistControlViewModel's
    // SetCurrentPlaylist. Unconditional: activating a row is always "start a new
    // queue from here," regardless of what was playing before.
    public void PlayTrack(Track track)
    {
        SyncPlayQueueToCurrentView();
        _playlistControlViewModel.Play(track);
    }

    // Space bar / toolbar play-pause button. Only snapshots a fresh queue when
    // PlaylistControlViewModel.PlayOrPause is actually about to start a track from
    // scratch (nothing currently playing or paused) - mirrors the exact condition
    // under which it calls Play(track) internally. Toggling pause/resume of an
    // already-playing/paused track must never touch the queue, or switching views
    // while paused would silently redirect Next/Previous to the new view (the bug
    // this whole thing exists to avoid).
    public void PlayOrPauseFromCurrentView()
    {
        if (!_playlistControlViewModel.IsPlaying && !_playlistControlViewModel.CanResume)
            SyncPlayQueueToCurrentView();
        _playlistControlViewModel.PlayOrPause();
    }

    private void SyncPlayQueueToCurrentView() =>
        _playlistControlViewModel.SetCurrentPlaylist(new Playlist("Now Playing Queue", new List<Track>(_currentFilteredTracks)));

    // ── Sort ──────────────────────────────────────────────────────────────────

    private void SortByColumn(string? columnId)
    {
        if (columnId == null)
            return;

        if (IsViewingRecentlyAdded)
        {
            if (_recentlyAddedSortColumn == columnId)
                _recentlyAddedSortAscending = !_recentlyAddedSortAscending;
            else
            {
                _recentlyAddedSortColumn    = columnId;
                _recentlyAddedSortAscending = true;
            }
            OnPropertyChanged(nameof(SortColumn));
            OnPropertyChanged(nameof(SortAscending));
            ScheduleFilter();
            return;
        }

        if (_sortColumn == columnId)
            _sortAscending = !_sortAscending;
        else
        {
            _sortColumn    = columnId;
            _sortAscending = true;
        }
        OnPropertyChanged(nameof(SortColumn));
        OnPropertyChanged(nameof(SortAscending));
        ScheduleFilter();
        _appSettings ??= new AppSettings();
        _appSettings.SortColumn    = _sortColumn;
        _appSettings.SortAscending = _sortAscending;
        _ = new AppSettingsStore().SaveAsync(_appSettings);
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
        _sidebarItems.Add(new SidebarItem(SidebarItemKind.Header,        "Library"));
        _sidebarItems.Add(new SidebarItem(SidebarItemKind.RecentlyAdded, "Recently Added", MaterialIconKind.ClockPlusOutline));
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

        // Allow a fresh sync if this device is discovered again later this session.
        if (item.Device?.Fingerprint is { Length: > 0 } fingerprint)
            _syncedDeviceFingerprints.TryRemove(fingerprint, out _);

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

    // Runs a playlist sync session with a newly (re-)discovered device once - see
    // SYNC-PLAN.md Phase 2. DeviceDiscovered fires more than once per peer (mDNS
    // fallback alias, then the resolved /info alias+fingerprint), so this only
    // fires once the fingerprint is known and only the first time per session.
    private void TriggerPlaylistSyncIfReady(DiscoveredDevice device)
    {
        if (string.IsNullOrEmpty(device.Fingerprint))
            return;
        if (!_syncedDeviceFingerprints.TryAdd(device.Fingerprint, 0))
            return;

        _ = _playlistSyncService?.SyncWithAsync(device);
    }

    // Rebuilds just the "Playlists" section in place, preserving the current
    // selection by playlist Id when possible - PlaylistsUpdated replaces the whole
    // Library.Playlists list (see Library.ReplacePlaylists), so the previously
    // selected Playlist object reference may no longer be the one shown.
    private void RefreshPlaylistSidebarItems()
    {
        var selectedPlaylistId = _selectedSidebarItem?.Kind == SidebarItemKind.Playlist
            ? _selectedSidebarItem.Playlist?.Id
            : null;

        foreach (var stale in _sidebarItems.Where(i => i.Kind == SidebarItemKind.Playlist).ToList())
            _sidebarItems.Remove(stale);

        var header = _sidebarItems.FirstOrDefault(i => i.Kind == SidebarItemKind.Header && i.Name == "Playlists");
        if (Library.Playlists.Count == 0)
        {
            if (header != null)
                _sidebarItems.Remove(header);
            if (selectedPlaylistId != null)
                SelectedSidebarItem = _sidebarItems.FirstOrDefault(i => i.Kind == SidebarItemKind.Songs);
            return;
        }

        var insertAt = header != null ? _sidebarItems.IndexOf(header) + 1 : _sidebarItems.Count;
        if (header == null)
            _sidebarItems.Insert(insertAt++, new SidebarItem(SidebarItemKind.Header, "Playlists"));

        foreach (var pl in Library.Playlists)
            _sidebarItems.Insert(insertAt++, new SidebarItem(SidebarItemKind.Playlist, pl.Name, MaterialIconKind.PlaylistPlay, pl));

        if (selectedPlaylistId != null)
        {
            var reselected = _sidebarItems.FirstOrDefault(i => i.Kind == SidebarItemKind.Playlist && i.Playlist?.Id == selectedPlaylistId);
            SelectedSidebarItem = reselected ?? _sidebarItems.FirstOrDefault(i => i.Kind == SidebarItemKind.Songs);
        }
    }

    public Task CreatePlaylistWithTrack(Track? track)
        => CreatePlaylistWithTracks(track != null ? new List<Track> { track } : new List<Track>());

    public async Task CreatePlaylistWithTracks(IEnumerable<Track> tracks)
    {
        var playlist = new Playlist("New Playlist", tracks.ToList());
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

    public async Task DeletePlaylistAsync(Playlist playlist)
    {
        // Gated here rather than at each call site (the sidebar's context menu
        // and the Playlist main-menu command both land here) so neither one can
        // forget to confirm. No subscriber (e.g. no window yet) means proceed
        // unconfirmed, matching how PlaylistConflictRequested degrades elsewhere
        // in this class.
        if (DeletePlaylistConfirmationRequested is { } handler)
        {
            var confirmed = new TaskCompletionSource<bool>();
            handler.Invoke(this, new DeletePlaylistConfirmationEventArgs { Playlist = playlist, Confirmed = confirmed });
            if (!await confirmed.Task)
                return;
        }

        Library.RemovePlaylist(playlist);

        // Reuses the sidebar-rebuild logic sync already needed to reflect a
        // changed Library.Playlists (see PlaylistSyncService) - it also handles
        // falling back to Songs if the deleted playlist was selected.
        RefreshPlaylistSidebarItems();

        await new PlaylistStore().SaveAsync(Library.Playlists);
    }

    // Backs the "Playlist" main-menu's Rename/Delete entries, which - unlike the
    // sidebar's own right-click menu - have no specific row to operate on, only
    // whichever playlist is currently selected.
    private bool CanRenameOrDeleteSelectedPlaylist() => SelectedSidebarItem?.Kind == SidebarItemKind.Playlist;

    private async Task DeleteSelectedPlaylistAsync()
    {
        if (SelectedSidebarItem?.Playlist is { } playlist)
            await DeletePlaylistAsync(playlist);
    }

    public Task AddTrackToPlaylist(Track track, Playlist playlist)
        => AddTracksToPlaylist(new[] { track }, playlist);

    public async Task AddTracksToPlaylist(IEnumerable<Track> tracks, Playlist playlist)
    {
        foreach (var track in tracks)
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
        // Recently Added carries its own independent sort state (see SortColumn),
        // so switching to/from it changes what these computed properties report.
        OnPropertyChanged(nameof(SortColumn));
        OnPropertyChanged(nameof(SortAscending));
        RebuildSubListItems();
        _renamePlaylistCommand?.NotifyCanExecuteChanged();
        _deletePlaylistCommand?.NotifyCanExecuteChanged();

        var lastSelected = _selectedSidebarItem?.Kind switch
        {
            SidebarItemKind.Albums  => _lastSelectedAlbum,
            SidebarItemKind.Artists => _lastSelectedArtist,
            _ => null
        };
        var initial = lastSelected != null && _subListItems.Contains(lastSelected)
            ? lastSelected
            : _subListItems.FirstOrDefault();
        ApplySubItemSelection(initial != null ? new[] { initial } : Array.Empty<string>());
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

    // Resolves the tracks behind a set of SubListItems entries (album or artist
    // names, depending on the current sidebar view) - used by the drag-albums/
    // artists-onto-a-playlist gesture in MainView.axaml.cs, which drags the
    // sub-list's selected string items rather than specific Tracks.
    public IEnumerable<Track> GetTracksForSubListItems(IEnumerable<string> items)
    {
        var set = new HashSet<string>(items);
        return _selectedSidebarItem?.Kind switch
        {
            SidebarItemKind.Albums  => _allTracks.Where(t => t.Album != null && set.Contains(t.Album)),
            SidebarItemKind.Artists => _allTracks.Where(t => t.Artists != null && set.Contains(t.Artists)),
            _ => Enumerable.Empty<Track>()
        };
    }

    private List<Track> GetBaseTracksForFilter()
    {
        return _selectedSidebarItem?.Kind switch
        {
            SidebarItemKind.Playlist when _selectedSidebarItem.Playlist != null
                => new List<Track>(_selectedSidebarItem.Playlist.Tracks),
            SidebarItemKind.Albums when _selectedSubItems.Count > 0
                => _allTracks.Where(t => t.Album != null && _selectedSubItems.Contains(t.Album)).ToList(),
            SidebarItemKind.Albums
                => new List<Track>(),
            SidebarItemKind.Artists when _selectedSubItems.Count > 0
                => _allTracks.Where(t => t.Artists != null && _selectedSubItems.Contains(t.Artists)).ToList(),
            SidebarItemKind.Artists
                => new List<Track>(),
            SidebarItemKind.Device
                => new List<Track>(),
            _ => _allTracks
        };
    }

    // ── Database ops ──────────────────────────────────────────────────────────

    private void OpenAppDataLocation()
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

    // Persists the path list only - deliberately doesn't also rescan, so
    // SettingsWindow can close its dialog immediately on OK instead of
    // blocking on however long the (potentially large) library scan takes;
    // it calls RescanLibraryAsync separately, unawaited, after closing.
    public async Task SaveLibraryPathsAsync(List<string> paths)
    {
        _appSettings ??= new AppSettings();
        _appSettings.LibraryPaths = paths;
        await new AppSettingsStore().SaveAsync(_appSettings);
    }

    // Mobile has no library-paths UI to rescan as a side effect of (desktop's
    // SettingsWindow OK button) — it needs to trigger a rescan directly,
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
        // Recently Added uses its own independent sort state (see SortColumn).
        var sortCol    = _selectedSidebarItem?.Kind == SidebarItemKind.Playlist ? "PlaylistOrder" : SortColumn;
        var sortAsc    = SortAscending;
        var playing    = CurrentlyPlayingTrack;
        var baseTracks = GetBaseTracksForFilter();

        var rows = await Task.Run(() =>
            TrackListBuilder.Build(baseTracks, text, sortCol, sortAsc, playing, _sortArtistAlbumsByYear), token);

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
