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

using Microsoft.Extensions.Logging;

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
    // Defaults to a no-op logger for the parameterless design-time constructor
    // below, which never receives one via DI - overwritten by the real
    // constructor's injected ILogger<MainViewModel> otherwise.
    private ILogger _logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    private readonly PlaylistControlViewModel _playlistControlViewModel;
    private AppSettings? _appSettings;
    private IMusicImporter? _importer;
    private MainPlaylist?      _mainPlaylist;
    private PlaylistSyncService? _playlistSyncService;
    private LibrarySyncService? _librarySyncService;
    private LibraryDownloadService? _libraryDownloadService;
    private DeviceIdentity? _deviceIdentity;
    private LibraryStore? _libraryStore;
    private AppSettingsStore? _appSettingsStore;
    private PlaylistStore? _playlistStore;

    // Fingerprints of devices already sync'd (or currently syncing) this app
    // session, so DeviceDiscovered re-firing for the same peer (e.g. once with the
    // mDNS-name fallback alias, again once /info resolves) doesn't start a second,
    // overlapping sync session. Cleared per-device on DeviceLost so a peer that
    // drops off and comes back later gets a fresh sync. Concurrent dictionary
    // because discovery events aren't guaranteed to arrive on one fixed thread.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _syncedDeviceFingerprints = new();

    // Non-zero while at least one PlaylistSyncService/LibrarySyncService call
    // is in flight (see RunTrackedSync) - both services' merges fire
    // Library.TracksUpdated/PlaylistsUpdated unconditionally, even when
    // nothing actually changed (e.g. every song a peer reports already exists
    // locally). Without this guard, the debounced resync below (ScheduleContentSync)
    // would treat a sync's own merge as "a local change just happened" and
    // schedule another sync, which would merge again and reschedule again,
    // forever - two devices perpetually re-triggering each other.
    private int _activeSyncCount;

    private void RunTrackedSync(Func<Task> syncCall)
    {
        Interlocked.Increment(ref _activeSyncCount);
        _ = RunTrackedSyncAsync(syncCall);
    }

    private async Task RunTrackedSyncAsync(Func<Task> syncCall)
    {
        try
        {
            await syncCall();
        }
        finally
        {
            Interlocked.Decrement(ref _activeSyncCount);
        }
    }

    private CancellationTokenSource? _contentSyncCts;

    // "A few seconds" per the user request - long enough that a burst of rapid
    // local edits (e.g. reordering a playlist track-by-track, or a rescan
    // finding many files) settles into one sync instead of one per edit, short
    // enough that a peer notices a real change reasonably promptly.
    private static readonly TimeSpan ContentSyncCooldown = TimeSpan.FromSeconds(5);

    // Called whenever a genuine local change happens to this device's library
    // or playlists: a rescan or download completing (Library.TracksUpdated),
    // or a playlist being created/renamed/deleted/reordered/added-to (called
    // directly at each of those call sites - unlike TracksUpdated,
    // Library.PlaylistsUpdated only fires for a *sync's own* ReplacePlaylists
    // call, never for these ordinary local actions, per its own doc comment,
    // so there is no single event to hook for playlists the way there is for
    // tracks). Debounced: every call restarts the cooldown rather than queuing
    // another, so only the last change in a burst actually triggers a sync.
    public void ScheduleContentSync()
    {
        _logger.LogInformation("Content sync scheduled, cooldown restarted ({Cooldown}s)", ContentSyncCooldown.TotalSeconds);
        _contentSyncCts?.Cancel();
        _contentSyncCts = new CancellationTokenSource();
        _ = DebouncedContentSyncAsync(_contentSyncCts.Token);
    }

    private async Task DebouncedContentSyncAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(ContentSyncCooldown, token);
        }
        catch (OperationCanceledException)
        {
            return; // A newer change restarted the cooldown - that call's own delay will fire instead.
        }

        // Every currently-known, fingerprint-resolved peer - not gated by
        // _syncedDeviceFingerprints (that dedup is specifically for "don't
        // double-sync from DeviceDiscovered re-firing at first contact" - see
        // TriggerSyncIfReady - and is orthogonal to resyncing on a later change).
        var devices = _sidebarItems
            .Where(i => i.Kind == SidebarItemKind.Device && i.Device is { Fingerprint.Length: > 0 })
            .Select(i => i.Device!)
            .ToList();

        _logger.LogInformation("Content sync cooldown elapsed, syncing with {Count} known device(s): {Devices}",
            devices.Count, string.Join(", ", devices.Select(d => d.Alias)));

        foreach (var device in devices)
        {
            RunTrackedSync(() => _playlistSyncService?.SyncWithAsync(device) ?? Task.CompletedTask);
            RunTrackedSync(() => _librarySyncService?.SyncWithAsync(device) ?? Task.CompletedTask);
        }
    }

    public ICommand? OpenAppDataLocationCommand  { get; private set; }
    public ICommand? RebuildDatabaseCommand      { get; private set; }
    public ICommand? OpenTrustedDevicesCommand   { get; private set; }
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
    public event EventHandler? TrustedDevicesRequested;
    public event EventHandler<Track>? NavigateToTrackRequested;
    public event EventHandler<PlaylistConflictEventArgs>? PlaylistConflictRequested;
    public event EventHandler<PeerApprovalRequestedEventArgs>? PeerApprovalRequested;

    // Raised by the "Playlist > Rename Playlist" main-menu command - unlike
    // deleting, renaming needs the sidebar's own inline-rename textbox (see
    // MainView.axaml.cs's BeginRename), which is a View concern this ViewModel
    // cannot reach directly.
    public event EventHandler? RenamePlaylistRequested;

    // See DeletePlaylistConfirmationEventArgs above.
    public event EventHandler<DeletePlaylistConfirmationEventArgs>? DeletePlaylistConfirmationRequested;

    public Library Library { get; private set; }

    public IReadOnlyList<string> LibraryPaths => _appSettings?.LibraryPaths ?? [];

    // What this device calls itself to peers (shown in the sidebar's Devices
    // section on the other end, and in the trust-gate approval prompt) - see
    // DeviceIdentity.Alias for why this has to be user-editable rather than
    // read from the OS. The same DeviceIdentity instance is shared with
    // SyncHttpServer/PlaylistSyncService/LibrarySyncService/LibraryDownloadService
    // (see App.axaml.cs), so mutating it here takes effect immediately - no
    // restart needed for a rename to reach the next peer that asks.
    public string DeviceAlias
    {
        get => _deviceIdentity?.Alias ?? "";
        set
        {
            var trimmed = value.Trim();
            if (_deviceIdentity == null || string.IsNullOrEmpty(trimmed) || _deviceIdentity.Alias == trimmed)
                return;
            _deviceIdentity.Alias = trimmed;
            _ = new DeviceIdentityStore().SaveAsync(_deviceIdentity);
        }
    }

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
            _ = (_appSettingsStore?.SaveAsync(_appSettings) ?? Task.CompletedTask);

            // Apply right away rather than only at the next launch, so turning
            // this on gives visible feedback immediately instead of looking
            // like it silently did nothing until the app is restarted.
            if (value)
                _ = SyncITunesPlayCountAsync();
        }
    }

    // Settings' Appearance picker (Follow System / Light / Dark) - see
    // Flower.Services.AppTheme for how this becomes an actual Avalonia
    // ThemeVariant. Same apply-immediately, persist-immediately pattern as
    // SyncPlayCountFromITunes above.
    public AppThemePreference ThemePreference
    {
        get => _appSettings?.ThemePreference ?? AppThemePreference.System;
        set
        {
            _appSettings ??= new AppSettings();
            if (_appSettings.ThemePreference == value)
                return;
            _appSettings.ThemePreference = value;
            _ = (_appSettingsStore?.SaveAsync(_appSettings) ?? Task.CompletedTask);
            AppTheme.Apply(value);
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
        await Task.Run(() => ITunesPlayCountImporter.Apply(Library.Tracks, _logger));
        // Same list, same Track instances mutated in place - just need
        // TracksUpdated to fire so the Plays column reflects the new
        // ImportedPlayCount values immediately. NotifyTrackChanged (not
        // UpdateTracks(Library.Tracks)) specifically - see its own doc
        // comment: passing Tracks back into UpdateTracks as if it were a
        // fresh scan result double-counts every placeholder (Path == null)
        // track, since UpdateTracks' own carry-forward step re-adds them a
        // second time on top of their copy already sitting in the argument.
        Library.NotifyTrackChanged();
        await (_libraryStore?.SaveAsync(Library.Tracks) ?? Task.CompletedTask);
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
            _ = (_appSettingsStore?.SaveAsync(_appSettings) ?? Task.CompletedTask);
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
        PlaylistSyncService playlistSyncService,
        LibrarySyncService librarySyncService,
        LibraryDownloadService libraryDownloadService,
        SyncHttpServer syncHttpServer,
        DeviceIdentity deviceIdentity,
        LibraryStore libraryStore,
        AppSettingsStore appSettingsStore,
        PlaylistStore playlistStore,
        ILogger<MainViewModel> logger)
    {
        Library                = library;
        _playlistControlViewModel = playlistControlViewModel;
        _appSettings           = appSettings;
        _importer              = importer;
        _mainPlaylist          = mainPlaylist;
        _playlistSyncService   = playlistSyncService;
        _librarySyncService    = librarySyncService;
        _libraryDownloadService = libraryDownloadService;
        _deviceIdentity        = deviceIdentity;
        _libraryStore          = libraryStore;
        _appSettingsStore      = appSettingsStore;
        _playlistStore         = playlistStore;
        _logger                = logger;

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
        OpenTrustedDevicesCommand   = new RelayCommand(() => TrustedDevicesRequested?.Invoke(this, EventArgs.Empty));
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
        {
            Dispatcher.UIThread.Post(PopulateTracks);
            // _activeSyncCount > 0 means this fired because one of our own
            // syncs just merged something (see RunTrackedSync's doc comment) -
            // not a genuine local change - so don't treat it as one.
            if (_activeSyncCount == 0)
                ScheduleContentSync();
            else
                _logger.LogDebug("TracksUpdated fired mid-sync ({ActiveSyncCount} active) - not scheduling a resync", _activeSyncCount);
        };
        library.PlaylistsUpdated += (_, _) =>
        {
            Dispatcher.UIThread.Post(RefreshPlaylistSidebarItems);
            if (_activeSyncCount == 0)
                ScheduleContentSync();
            else
                _logger.LogDebug("PlaylistsUpdated fired mid-sync ({ActiveSyncCount} active) - not scheduling a resync", _activeSyncCount);
        };

        networkDiscovery.DeviceDiscovered += (_, device) =>
        {
            Dispatcher.UIThread.Post(() => AddOrUpdateDeviceSidebarItem(device));
            TriggerSyncIfReady(device);
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

        // Same no-UI-listening fallback shape as ConflictDetected above, but fails
        // *closed* (deny) rather than defaulting to "keep local" - granting a
        // stranger access to this device's playlists/library is a security
        // decision, not a content merge, so an unattended device shouldn't ever
        // silently trust an unrecognized peer. See SyncHttpServer.AuthorizeAsync.
        syncHttpServer.PeerApprovalRequested += (_, e) =>
        {
            if (PeerApprovalRequested == null)
            {
                e.Resolution.TrySetResult(false);
                return;
            }
            Dispatcher.UIThread.Post(() => PeerApprovalRequested?.Invoke(this, e));
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
        _ = (_appSettingsStore?.SaveAsync(_appSettings) ?? Task.CompletedTask);
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
        var existing = FindDeviceSidebarItem(device);
        if (existing != null)
        {
            existing.Device = device;
            RefreshDeviceDisplayNames();
            return;
        }

        if (_sidebarItems.All(i => i.Kind != SidebarItemKind.Device))
            _sidebarItems.Add(new SidebarItem(SidebarItemKind.Header, "Devices"));

        _sidebarItems.Add(new SidebarItem(SidebarItemKind.Device, ResolveDeviceDisplayName(device), MaterialIconKind.Laptop, device: device));
        RefreshDeviceDisplayNames();
    }

    // Matches primarily by Fingerprint - the peer's own stable per-install
    // identity (see DeviceIdentityStore) - once its /info handshake has
    // resolved one, since InstanceName alone ({MachineName}-{Platform} - see
    // NetworkDiscoveryService.OwnInstanceName) can collide between two
    // genuinely distinct devices that both happen to still have the same
    // unrenamed default computer name. Matching on InstanceName regardless of
    // that would silently conflate two different devices into one sidebar
    // entry - whichever was discovered first would then keep this item's
    // Device pinned to the wrong endpoint even after its displayed name
    // updated to the second device's.
    //
    // Before a device's own Fingerprint resolves, InstanceName is the only
    // thing to go on - but such a match is only trusted against another item
    // that ALSO hasn't resolved a Fingerprint yet; an item that already has a
    // different, resolved Fingerprint is treated as a distinct device that
    // merely shares the same not-yet-renamed computer name, not the same one.
    private SidebarItem? FindDeviceSidebarItem(DiscoveredDevice device)
    {
        var deviceItems = _sidebarItems.Where(i => i.Kind == SidebarItemKind.Device).ToList();

        if (!string.IsNullOrEmpty(device.Fingerprint))
        {
            var byFingerprint = deviceItems.FirstOrDefault(i => i.Device?.Fingerprint == device.Fingerprint);
            if (byFingerprint != null)
                return byFingerprint;
        }

        return deviceItems.FirstOrDefault(i =>
            i.Device?.InstanceName == device.InstanceName && string.IsNullOrEmpty(i.Device.Fingerprint));
    }

    // A user-set local nickname (see DeviceNicknameStore, MainView.axaml.cs's
    // Rename Device context-menu item, TrustedDevicesWindow) always wins over
    // whatever the peer itself reports - otherwise the next DeviceDiscovered
    // re-fire (e.g. once /info resolves, or on periodic rediscovery) would
    // silently clobber a rename back to the peer's own alias.
    private static string ResolveDeviceDisplayName(DiscoveredDevice device) =>
        !string.IsNullOrEmpty(device.Fingerprint) && new DeviceNicknameStore().Get(device.Fingerprint) is { Length: > 0 } nickname
            ? nickname
            : device.Alias;

    // The single place that re-derives a Device sidebar item's displayed name
    // from ResolveDeviceDisplayName - every place a device's nickname can
    // change (this sidebar's own "Rename Device" context menu, and
    // TrustedDevicesWindow's pencil-icon rename) calls this afterward, so
    // there is exactly one code path computing "what do we call this device"
    // and every UI surface (the sidebar row, and the device-detail pane, which
    // binds to SelectedSidebarItem.Name - the same SidebarItem instance)
    // reflects it immediately rather than waiting for the next mDNS
    // rediscovery to happen to notice.
    public void RefreshDeviceDisplayNames()
    {
        var deviceItems = _sidebarItems.Where(i => i.Kind == SidebarItemKind.Device && i.Device != null).ToList();

        foreach (var item in deviceItems)
            item.Name = ResolveDeviceDisplayName(item.Device!);

        // A subtitle (this device's IP) only shows when its name collides
        // with another currently-listed device - two distinct devices
        // legitimately sharing a display name is purely cosmetic (sync/trust
        // both key off Fingerprint, never name), but the user still needs
        // some way to tell them apart in the sidebar itself.
        foreach (var group in deviceItems.GroupBy(i => i.Name))
        {
            var showSubtitle = group.Count() > 1;
            foreach (var item in group)
                item.Subtitle = showSubtitle ? item.Device!.EndPoint.Address.ToString() : null;
        }

        // SidebarItem.Device can end up re-pointed at a different
        // DiscoveredDevice instance than the one SelectedDevice last read
        // (see FindDeviceSidebarItem) - DiscoveredDevice itself doesn't raise
        // property-changed, so the device-detail pane's EndPoint binding
        // needs an explicit nudge to notice.
        OnPropertyChanged(nameof(SelectedDevice));
    }

    private void RemoveDeviceSidebarItem(string instanceName)
    {
        // mDNS's "goodbye" notification only ever carries the withdrawn
        // record's raw instance name, never a Fingerprint - so if two
        // genuinely distinct devices are colliding on InstanceName (see
        // FindDeviceSidebarItem), there is no way to tell from this signal
        // alone which of them actually went offline. Removing either
        // unconditionally risks dropping the one that is still there just as
        // easily as the one that isn't, so this deliberately does nothing
        // rather than guess wrong in that rare case - it will get cleaned up
        // for real the moment a Fingerprint-disambiguated event (a fresh
        // DeviceDiscovered, or that peer eventually being forgotten) sorts it
        // out instead.
        var matches = _sidebarItems.Where(i =>
            i.Kind == SidebarItemKind.Device && i.Device?.InstanceName == instanceName).ToList();
        if (matches.Count != 1)
            return;

        var item = matches[0];

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
        else
        {
            RefreshDeviceDisplayNames();
        }
    }

    // Downloads one placeholder track's audio from whichever peer currently holds
    // it - see LibraryDownloadService, SYNC-PLAN.md Phase 3's mobile download
    // button. Resolves the peer against currently-discovered devices (the same
    // Devices sidebar list Cmd/Ctrl-independent code above already maintains) -
    // a peer that's gone offline/out of range since it was last seen results in
    // TrackDownloadResult.PeerUnavailable rather than guessing an address.
    public Task<TrackDownloadResult> DownloadTrackAsync(Track track)
    {
        var peer = track.OriginDeviceFingerprint is { } fingerprint
            ? _sidebarItems.FirstOrDefault(i => i.Kind == SidebarItemKind.Device && i.Device?.Fingerprint == fingerprint)?.Device
            : null;

        return _libraryDownloadService?.DownloadAsync(track, peer) ?? Task.FromResult(TrackDownloadResult.Failed);
    }

    // Runs a playlist sync session (Phase 2) and a library sync session (Phase 3 -
    // see LibrarySyncService) with a newly (re-)discovered device once each.
    // DeviceDiscovered fires more than once per peer (mDNS fallback alias, then
    // the resolved /info alias+fingerprint), so this only fires once the
    // fingerprint is known and only the first time per session. Both share this
    // one dedup gate/trigger even though library sync itself has no initiator
    // election (see LibrarySyncService) - there's still only one "first contact"
    // per peer per session worth reacting to.
    private void TriggerSyncIfReady(DiscoveredDevice device)
    {
        if (string.IsNullOrEmpty(device.Fingerprint))
            return;
        if (!_syncedDeviceFingerprints.TryAdd(device.Fingerprint, 0))
            return;

        _logger.LogInformation("First contact with {Alias} ({Fingerprint}) this session, triggering initial sync",
            device.Alias, device.Fingerprint);
        RunTrackedSync(() => _playlistSyncService?.SyncWithAsync(device) ?? Task.CompletedTask);
        RunTrackedSync(() => _librarySyncService?.SyncWithAsync(device) ?? Task.CompletedTask);
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

        await (_playlistStore?.SaveAsync(Library.Playlists) ?? Task.CompletedTask);
        ScheduleContentSync();
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

        await (_playlistStore?.SaveAsync(Library.Playlists) ?? Task.CompletedTask);
        ScheduleContentSync();
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

        await (_playlistStore?.SaveAsync(Library.Playlists) ?? Task.CompletedTask);
        ScheduleContentSync();
    }

    public async Task ReorderPlaylistTrack(Playlist playlist, Track dragged, Track? insertBefore)
    {
        if (!playlist.Tracks.Remove(dragged))
            return;

        var index = insertBefore != null ? playlist.Tracks.IndexOf(insertBefore) : -1;
        playlist.Tracks.Insert(index < 0 ? playlist.Tracks.Count : index, dragged);

        if (_selectedSidebarItem?.Playlist == playlist)
            ScheduleFilter();

        await (_playlistStore?.SaveAsync(Library.Playlists) ?? Task.CompletedTask);
        ScheduleContentSync();
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
        if (_importer == null || _mainPlaylist == null || _libraryStore == null)
            return;
        using var _ = BeginBusy("Rebuilding library…");
        var libraryPaths = _appSettings?.LibraryPaths;
        var freshTracks = await _importer.ImportAsync(libraryPaths);
        _mainPlaylist.ReplaceAll(freshTracks);
        Library.UpdateTracks(freshTracks);
        await _libraryStore.SaveAsync(freshTracks);
    }

    // Persists the path list only - deliberately doesn't also rescan, so
    // SettingsWindow can close its dialog immediately on OK instead of
    // blocking on however long the (potentially large) library scan takes;
    // it calls RescanLibraryAsync separately, unawaited, after closing.
    public async Task SaveLibraryPathsAsync(List<string> paths)
    {
        _appSettings ??= new AppSettings();
        _appSettings.LibraryPaths = paths;
        await (_appSettingsStore?.SaveAsync(_appSettings) ?? Task.CompletedTask);
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
