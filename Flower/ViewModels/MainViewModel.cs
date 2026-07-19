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
using Flower.ViewModels.Mobile;

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
    private NetworkDiscoveryService? _networkDiscovery;
    private LibraryStore? _libraryStore;
    private AppSettingsStore? _appSettingsStore;
    private PlaylistStore? _playlistStore;
    private DeviceIdentityStore? _deviceIdentityStore;
    private DeviceNicknameStore? _deviceNicknameStore;

    // Fingerprints of devices already sync'd (or currently syncing) this app
    // session, so DeviceDiscovered re-firing for the same peer (e.g. once with the
    // mDNS-name fallback alias, again once /info resolves) doesn't start a second,
    // overlapping sync session. Cleared per-device on DeviceLost so a peer that
    // drops off and comes back later gets a fresh sync. Concurrent dictionary
    // because discovery events aren't guaranteed to arrive on one fixed thread.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _syncedDeviceFingerprints = new();

    // Fingerprints of Servers this (unpaired Client) device has already
    // offered ServerDiscoveredForPairing for this session - see
    // CheckForNewPairableServer. Once per fingerprint per session regardless
    // of whether the user actually paired, so declining/dismissing the
    // prompt doesn't nag again every time that Server's /info re-resolves;
    // relaunching the app (a fresh session) is the reset. Same
    // ConcurrentDictionary-as-thread-safe-set idiom as _syncedDeviceFingerprints.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _promptedServerFingerprints = new();

    // Raised the first time a Server is discovered while this device is an
    // unpaired Client - the UI is expected to proactively offer pairing
    // (see MobileMainViewModel's subscription) rather than requiring the
    // user to dig into Settings' server list themselves. Deliberately still
    // just an offer, not automatic pairing - decision #3 (client picks its
    // server manually) still holds, this only changes *when* that choice is
    // surfaced to the user, not who makes it.
    public event EventHandler<DiscoveredDevice>? ServerDiscoveredForPairing;

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

        // Every currently-known, fingerprint-resolved peer this device should
        // bulk-sync with per SyncRolePolicy - not gated by
        // _syncedDeviceFingerprints (that dedup is specifically for "don't
        // double-sync from DeviceDiscovered re-firing at first contact" - see
        // TriggerSyncIfReady - and is orthogonal to resyncing on a later
        // change). Collapses to at most one device (the Client's paired
        // Server) under role gating; empty for a Server, which never initiates.
        var isServer = _appSettings?.IsServer ?? false;
        var pairedServerFingerprint = _appSettings?.PairedServerFingerprint;
        var devices = _sidebarItems
            .Where(i => i.Kind == SidebarItemKind.Device && i.Device is { Fingerprint.Length: > 0 } &&
                        SyncRolePolicy.ShouldInitiateSync(isServer, pairedServerFingerprint, i.Device.Fingerprint))
            .Select(i => i.Device!)
            .ToList();

        _logger.LogInformation("Content sync cooldown elapsed, syncing with {Count} known device(s): {Devices}",
            devices.Count, string.Join(", ", devices.Select(d => d.Alias)));

        foreach (var device in devices)
        {
            // forceInitiator: true - see TriggerSyncIfReady's identical
            // reasoning; every device here is already the Client's own
            // paired Server (ShouldInitiateSync above guarantees it).
            RunTrackedSync(() => _playlistSyncService?.SyncWithAsync(device, forceInitiator: true) ?? Task.CompletedTask);
            RunTrackedSync(() => _librarySyncService?.SyncWithAsync(device) ?? Task.CompletedTask);
        }
    }

    public ICommand? OpenAppDataLocationCommand  { get; private set; }
    public ICommand? RebuildDatabaseCommand      { get; private set; }
    public ICommand? SortByColumnCommand         { get; private set; }
    public ICommand? OpenSettingsCommand         { get; private set; }
    public ICommand? OpenColumnSelectorCommand   { get; private set; }
    public ICommand? NewPlaylistCommand          { get; private set; }
    public ICommand? RenamePlaylistCommand       { get; private set; }
    public ICommand? DeletePlaylistCommand       { get; private set; }
    public ICommand? ToggleAlbumExpandedCommand  { get; private set; }

    // Backing the "Controls" menu (MainWindow.axaml) - PlaylistControls' own
    // transport buttons call these same three operations directly on
    // _playlistControlViewModel (or, for play/pause, PlayOrPauseFromCurrentView
    // itself) rather than through an ICommand at all, since a plain UserControl
    // doesn't need one; a NativeMenuItem does, and MainWindow's DataContext is
    // this ViewModel, not PlaylistControlViewModel, so these just forward.
    public ICommand? PlayOrPauseCommand          { get; private set; }
    public ICommand? NextTrackCommand            { get; private set; }
    public ICommand? PreviousTrackCommand        { get; private set; }
    public ICommand? ToggleRepeatCommand         { get; private set; }
    public ICommand? ToggleShuffleCommand        { get; private set; }

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
            _ = (_deviceIdentityStore?.SaveAsync(_deviceIdentity) ?? Task.CompletedTask);
        }
    }

    // Whether to import per-track play counts from iTunes/Music.app on every
    // launch - see ITunesPlayCountImporter. Persisted immediately on change,
    // like SortArtistAlbumsByYear below, rather than gated behind Settings'
    // OK button (which is specifically about the library-paths list). The
    // sync itself, though, *is* OK-gated - see SettingsWindow.SaveButton_Click
    // - so checking the box mid-dialog doesn't kick off a multi-second
    // AppleScript export before the user has finished deciding what else to
    // change in Settings.
    public bool SyncPlayCountFromITunes
    {
        get => _appSettings?.SyncPlayCountFromITunes ?? false;
        set
        {
            _appSettings ??= new AppSettings();
            if (_appSettings.SyncPlayCountFromITunes == value)
                return;
            // Logged - the only writer of this flag is this setter, but a
            // user report of it having silently flipped off without them
            // touching the checkbox ("Settings > Library" is the only UI for
            // it) turned up nothing conclusive in the code; this at least
            // gives a timestamped trail (with a stack trace, to catch a
            // programmatic caller vs. the checkbox's own click handler) if it
            // happens again.
            _logger.LogInformation("SyncPlayCountFromITunes changed {Old} -> {New}\n{StackTrace}", _appSettings.SyncPlayCountFromITunes, value, Environment.StackTrace);
            _appSettings.SyncPlayCountFromITunes = value;
            _ = (_appSettingsStore?.SaveAsync(_appSettings) ?? Task.CompletedTask);
        }
    }

    // Whether to import per-track "Date Added" from iTunes/Music.app on every
    // launch - see ITunesDateAddedImporter. Same persist-immediately-but-
    // OK-gated-sync pattern as SyncPlayCountFromITunes above.
    public bool SyncDateAddedFromITunes
    {
        get => _appSettings?.SyncDateAddedFromITunes ?? false;
        set
        {
            _appSettings ??= new AppSettings();
            if (_appSettings.SyncDateAddedFromITunes == value)
                return;
            // See SyncPlayCountFromITunes's own comment on this same logging.
            _logger.LogInformation("SyncDateAddedFromITunes changed {Old} -> {New}\n{StackTrace}", _appSettings.SyncDateAddedFromITunes, value, Environment.StackTrace);
            _appSettings.SyncDateAddedFromITunes = value;
            _ = (_appSettingsStore?.SaveAsync(_appSettings) ?? Task.CompletedTask);
        }
    }

    // Whether this device accepts incoming bulk-sync from Client devices
    // (Server) or initiates bulk-sync toward exactly one chosen Server
    // (Client, the default) - see Settings' General tab, AppSettings.IsServer,
    // and SyncRolePolicy. Takes effect immediately, live - unlike
    // SyncHttpServer/mDNS (which keep running unconditionally on every
    // device regardless of role, so browsing/streaming stays unrestricted),
    // nothing here needs a restart.
    public bool IsServer
    {
        get => _appSettings?.IsServer ?? false;
        set
        {
            _appSettings ??= new AppSettings();
            if (_appSettings.IsServer == value)
                return;
            _appSettings.IsServer = value;
            if (value)
            {
                // Not syncing again with the old paired server (a deliberate
                // requirement, not an oversight) - library/playlists
                // themselves are untouched by this flip (nothing else reads
                // IsServer except the sync-trigger gating in
                // TriggerSyncIfReady/DebouncedContentSyncAsync), this only
                // clears the now-stale pairing pointer.
                _appSettings.PairedServerFingerprint = null;
                _appSettings.PairedServerAlias = null;
            }
            _logger.LogInformation("IsServer changed {Old} -> {New}", !value, value);
            _ = (_appSettingsStore?.SaveAsync(_appSettings) ?? Task.CompletedTask);
            OnPropertyChanged();
            OnPropertyChanged(nameof(PairedServerFingerprint));
            OnPropertyChanged(nameof(PairedServerAlias));
        }
    }

    public string? PairedServerFingerprint => _appSettings?.PairedServerFingerprint;
    public string? PairedServerAlias       => _appSettings?.PairedServerAlias;

    // Every currently-discovered peer advertising Server mode - the pool
    // ServerPickerView picks a pairing from. Unrelated to trust: an
    // untrusted server can still appear here, it just won't actually sync
    // until it approves this device (see SyncHttpServer.AuthorizeAsync).
    public IEnumerable<DiscoveredDevice> AvailableServers =>
        _networkDiscovery?.KnownDevices.Where(d => d.IsServer) ?? Enumerable.Empty<DiscoveredDevice>();

    // Manual pairing (see decision: a Client picks its one server explicitly,
    // no automatic first-found pairing) - called from ServerPickerView's
    // "Pair" action.
    public void PairWithServer(DiscoveredDevice device)
    {
        _appSettings ??= new AppSettings();
        _appSettings.PairedServerFingerprint = device.Fingerprint;
        _appSettings.PairedServerAlias = device.Alias;
        _ = (_appSettingsStore?.SaveAsync(_appSettings) ?? Task.CompletedTask);
        OnPropertyChanged(nameof(PairedServerFingerprint));
        OnPropertyChanged(nameof(PairedServerAlias));
        TriggerSyncIfReady(device); // sync immediately rather than waiting for the next discovery event
    }

    // Proactively offers pairing the moment a Server is found, rather than
    // only when the user happens to open Settings and look - see
    // ServerDiscoveredForPairing's own doc comment. Deliberately not raised
    // for a Server itself finding another Server (weAreServer==true here
    // means this device never pairs with anyone) or once already paired
    // (PairedServerFingerprint non-empty) - those cases have nothing to
    // offer.
    private void CheckForNewPairableServer(DiscoveredDevice device)
    {
        if (IsServer || !device.IsServer || string.IsNullOrEmpty(device.Fingerprint))
            return;
        if (!string.IsNullOrEmpty(PairedServerFingerprint))
            return;
        if (!_promptedServerFingerprints.TryAdd(device.Fingerprint, 0))
            return;

        ServerDiscoveredForPairing?.Invoke(this, device);
    }

    // ServerPickerView's "Unpair" action - must be called before pairing
    // with a different server (switching requires an explicit unpair-first
    // step, not a direct one-click switch).
    public void UnpairServer()
    {
        if (_appSettings == null)
            return;
        _appSettings.PairedServerFingerprint = null;
        _appSettings.PairedServerAlias = null;
        _ = (_appSettingsStore?.SaveAsync(_appSettings) ?? Task.CompletedTask);
        OnPropertyChanged(nameof(PairedServerFingerprint));
        OnPropertyChanged(nameof(PairedServerAlias));
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
    //
    // Both this and SyncITunesDateAddedAsync below can be triggered from more
    // than one place in close succession - the startup rescan (App.axaml.cs)
    // and Settings' OK button both fire unconditionally based on "is the
    // checkbox currently checked," not "did it just run" - so opening Settings
    // and clicking OK shortly after launch would otherwise re-run the same
    // ~1-2s AppleScript export twice back to back. ITunesSyncCooldown skips a
    // call that lands within a minute of the previous one finishing.
    private static readonly TimeSpan ITunesSyncCooldown = TimeSpan.FromMinutes(1);
    private DateTimeOffset? _lastPlayCountSyncAt;
    private DateTimeOffset? _lastDateAddedSyncAt;

    public async Task SyncITunesPlayCountAsync()
    {
        if (_lastPlayCountSyncAt is { } last && DateTimeOffset.UtcNow - last < ITunesSyncCooldown)
        {
            _logger.LogDebug("Skipping iTunes play count sync - ran {ElapsedSeconds:F0}s ago, inside the {CooldownSeconds:F0}s cooldown",
                (DateTimeOffset.UtcNow - last).TotalSeconds, ITunesSyncCooldown.TotalSeconds);
            return;
        }
        _lastPlayCountSyncAt = DateTimeOffset.UtcNow;

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

    // Same shape as SyncITunesPlayCountAsync above, but for Track.DateAdded via
    // ITunesDateAddedImporter - see that class for the oldest-wins conflict rule.
    public async Task SyncITunesDateAddedAsync()
    {
        if (_lastDateAddedSyncAt is { } last && DateTimeOffset.UtcNow - last < ITunesSyncCooldown)
        {
            _logger.LogDebug("Skipping iTunes date added sync - ran {ElapsedSeconds:F0}s ago, inside the {CooldownSeconds:F0}s cooldown",
                (DateTimeOffset.UtcNow - last).TotalSeconds, ITunesSyncCooldown.TotalSeconds);
            return;
        }
        _lastDateAddedSyncAt = DateTimeOffset.UtcNow;

        using var _ = BeginBusy("Syncing date added from Music.app…");
        await Task.Run(() => ITunesDateAddedImporter.Apply(Library.Tracks, _logger));
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

    // Backing the Controls menu's checkable Repeat/Shuffle items (MainWindow.axaml)
    // - same read-only passthrough + PropertyChanged-forwarding pattern
    // CurrentlyPlayingControlViewModel already uses for its own repeat/shuffle
    // icon buttons, so the menu's checkmarks and those icons never disagree
    // regardless of which one was used to toggle it.
    public bool IsRepeatEnabled => _playlistControlViewModel.IsRepeatEnabled;
    public bool IsShuffleEnabled => _playlistControlViewModel.IsShuffleEnabled;

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

    // Public entry point for App.axaml.cs's startup sequence, which needs to
    // keep the spinner up across the whole rescan + both iTunes syncs as one
    // continuous scope (nesting further BeginBusy calls inside it just updates
    // BusyMessage as each step starts - see NotifyBusyChanged) rather than
    // relying on each step's own brief individual scope, since the rescan
    // itself - the longest part by far, ~9s against a large real library - had
    // no busy coverage of its own at all.
    public IDisposable BeginBusyScope(string? message = null) => BeginBusy(message);

    // The count itself is bumped synchronously (needed immediately regardless
    // of caller thread, to correctly track overlapping scopes). The
    // notifications used to always go through Dispatcher.UIThread.Post, even
    // when the caller was already on the UI thread (the common case - every
    // button-click command runs synchronously up to its first await) - a real
    // bug once SyncITunesPlayCountAsync started also calling this from a
    // background Task.Run (App.axaml.cs's startup rescan): IsBusy's IsVisible
    // binding "worked" anyway (something else happened to force a UI-thread
    // re-evaluation around the same time), but BusyMessage's TextBlock
    // silently never updated. NotifyBusyChanged below fires the notification
    // immediately when already on the UI thread instead of unconditionally
    // deferring it, so the spinner/message show up as soon as this method
    // returns rather than depending on something else happening to pump the
    // dispatcher queue first.
    private IDisposable BeginBusy(string? message = null)
    {
        Interlocked.Increment(ref _busyCount);
        NotifyBusyChanged(message);
        return new BusyScope(this);
    }

    private void NotifyBusyChanged(string? message)
    {
        void Notify()
        {
            _busyMessage = message;
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(BusyMessage));
        }

        if (Dispatcher.UIThread.CheckAccess())
            Notify();
        else
            Dispatcher.UIThread.Post(Notify);
    }

    private sealed class BusyScope : IDisposable
    {
        private readonly MainViewModel _vm;
        internal BusyScope(MainViewModel vm) => _vm = vm;
        public void Dispose()
        {
            if (Interlocked.Decrement(ref _vm._busyCount) == 0)
                _vm.NotifyBusyChanged(null);
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

    // Artists still uses the plain-text picker; Albums was replaced by
    // AlbumGridTiles below (see IsShowingAlbumGrid) - same underlying
    // SelectedSubItem/_selectedSubItems mechanism either way, just a
    // different picker UI in front of it for Albums now.
    public bool IsSubListVisible => _selectedSidebarItem?.Kind == SidebarItemKind.Artists;

    // Album art tile grid - shown instead of the track list while on Albums/
    // Recently Added, mirroring mobile's own Albums/Recently Added tabs (see
    // MobileMainViewModel.AlbumGridRows/RecentlyAddedAlbumRows). Unlike the
    // old plain-text SubList this replaced, an album's songs are shown
    // in-place (see ExpandedAlbumName below) rather than by navigating to a
    // separate track-list view, so both of these are unconditional - always
    // true while on their respective sidebar item.
    public bool IsShowingAlbumGrid => _selectedSidebarItem?.Kind == SidebarItemKind.Albums;
    public bool IsShowingRecentlyAddedGrid => _selectedSidebarItem?.Kind == SidebarItemKind.RecentlyAdded;

    public bool IsShowingTrackList => !IsShowingDeviceDetail && !IsShowingAlbumGrid && !IsShowingRecentlyAddedGrid;

    // The one album (if any) currently expanded inline within whichever grid
    // is showing - see AlbumGridView/AlbumGridRowControl for the actual
    // expand/collapse rendering+animation. Deliberately independent of
    // _selectedSubItems (Ctrl/Shift multi-select for drag-to-playlist, see
    // MainView.axaml.cs's AlbumGrid_PointerPressed) - a plain click toggles
    // this and never touches multi-select; Ctrl/Shift-click never touches this.
    private string? _expandedAlbumName;
    public string? ExpandedAlbumName
    {
        get => _expandedAlbumName;
        private set { _expandedAlbumName = value; OnPropertyChanged(); }
    }

    private ObservableCollection<Track> _expandedAlbumTracks = new();
    public ObservableCollection<Track> ExpandedAlbumTracks
    {
        get => _expandedAlbumTracks;
        private set { _expandedAlbumTracks = value; OnPropertyChanged(); }
    }

    // Accordion behavior - clicking the already-expanded album collapses it;
    // clicking a different one switches straight to it. Both Albums' and
    // Recently Added's tiles route through here (see AlbumGrid_PointerPressed),
    // independent of which grid the click came from - the same album showing
    // up in both is exactly the same album either way.
    private void ToggleAlbumExpanded(string? albumName)
    {
        if (string.IsNullOrEmpty(albumName))
            return;

        if (_expandedAlbumName == albumName)
        {
            ExpandedAlbumName = null;
            ExpandedAlbumTracks = new ObservableCollection<Track>();
            return;
        }

        ExpandedAlbumName = albumName;
        ExpandedAlbumTracks = BuildExpandedAlbumTracks(albumName);
    }

    private ObservableCollection<Track> BuildExpandedAlbumTracks(string albumName) =>
        new(_allTracks.Where(t => t.Album == albumName)
            .OrderBy(t => t.DiscNumber)
            .ThenBy(t => t.TrackNumber));

    public bool IsShowingDeviceDetail => _selectedSidebarItem?.Kind == SidebarItemKind.Device;
    public DiscoveredDevice? SelectedDevice => _selectedSidebarItem?.Device;

    // Live browse/stream state for SelectedDevice, unrestricted by Client/
    // Server role - see PeerLibraryViewModel and OnSidebarSelectionChanged,
    // which triggers LoadAsync whenever SelectedDevice changes.
    public PeerLibraryViewModel PeerLibrary { get; }

    // Rebuilt in PopulateTracks (every TracksUpdated) - see AlbumGridBuilder/
    // RecentlyAddedAlbumsBuilder, the same shared builders mobile's own grids
    // use. Alphabetical for Albums, by-recency for Recently Added. Reassigned
    // wholesale rather than Clear()+Add() in a loop - same reasoning as
    // SubListItems below: one PropertyChanged per rebuild instead of one per
    // item, which matters on a library with a thousand-plus albums.
    private ObservableCollection<AlbumTileViewModel> _albumGridTiles = new();
    public ObservableCollection<AlbumTileViewModel> AlbumGridTiles
    {
        get => _albumGridTiles;
        private set { _albumGridTiles = value; OnPropertyChanged(); }
    }

    private ObservableCollection<AlbumTileViewModel> _recentlyAddedGridTiles = new();
    public ObservableCollection<AlbumTileViewModel> RecentlyAddedGridTiles
    {
        get => _recentlyAddedGridTiles;
        private set { _recentlyAddedGridTiles = value; OnPropertyChanged(); }
    }

    private void RebuildAlbumGrids()
    {
        AlbumGridTiles = new ObservableCollection<AlbumTileViewModel>(AlbumGridBuilder.Build(_allTracks));
        RecentlyAddedGridTiles = new ObservableCollection<AlbumTileViewModel>(RecentlyAddedAlbumsBuilder.Build(_allTracks));

        // An expanded album's tracks were resolved against the previous
        // _allTracks snapshot - refresh them so a library change (a rescan,
        // a download completing, a tag edit) while expanded doesn't leave it
        // showing stale Track references.
        if (_expandedAlbumName != null)
            ExpandedAlbumTracks = BuildExpandedAlbumTracks(_expandedAlbumName);
    }

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
        if (_selectedSidebarItem?.Kind == SidebarItemKind.Artists)
            _lastSelectedArtist = value;
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
        DeviceIdentityStore deviceIdentityStore,
        DeviceNicknameStore deviceNicknameStore,
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
        _networkDiscovery      = networkDiscovery;
        _deviceIdentity        = deviceIdentity;
        PeerLibrary            = new PeerLibraryViewModel(deviceIdentity, appSettings, playlistControlViewModel);
        _libraryStore          = libraryStore;
        _appSettingsStore      = appSettingsStore;
        _playlistStore         = playlistStore;
        _deviceIdentityStore   = deviceIdentityStore;
        _deviceNicknameStore   = deviceNicknameStore;
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
        NewPlaylistCommand          = new AsyncRelayCommand(() => CreatePlaylistWithTrack(null));
        PlayOrPauseCommand          = new RelayCommand(PlayOrPauseFromCurrentView);
        NextTrackCommand            = new RelayCommand(_playlistControlViewModel.Next);
        PreviousTrackCommand        = new RelayCommand(_playlistControlViewModel.Previous);
        ToggleRepeatCommand         = new RelayCommand(_playlistControlViewModel.ToggleRepeat);
        ToggleShuffleCommand        = new RelayCommand(_playlistControlViewModel.ToggleShuffle);

        _renamePlaylistCommand = new RelayCommand(
            () => RenamePlaylistRequested?.Invoke(this, EventArgs.Empty),
            CanRenameOrDeleteSelectedPlaylist);
        RenamePlaylistCommand = _renamePlaylistCommand;

        _deletePlaylistCommand = new AsyncRelayCommand(DeleteSelectedPlaylistAsync, CanRenameOrDeleteSelectedPlaylist);
        DeletePlaylistCommand = _deletePlaylistCommand;

        ToggleAlbumExpandedCommand = new RelayCommand<string>(ToggleAlbumExpanded);

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
            Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(AvailableServers)));
            Dispatcher.UIThread.Post(() => CheckForNewPairableServer(device));
            TriggerSyncIfReady(device);
        };
        networkDiscovery.DeviceLost += (_, instanceName) =>
        {
            Dispatcher.UIThread.Post(() => RemoveDeviceSidebarItem(instanceName));
            Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(AvailableServers)));
        };

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
            if (e.PropertyName == nameof(PlaylistControlViewModel.IsRepeatEnabled))
                OnPropertyChanged(nameof(IsRepeatEnabled));
            if (e.PropertyName == nameof(PlaylistControlViewModel.IsShuffleEnabled))
                OnPropertyChanged(nameof(IsShuffleEnabled));
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

    // Double-click on an album tile in the Albums/Recently Added grid (see
    // MainView.axaml.cs's AlbumGrid_PointerPressed) - queues the whole album
    // in track order and starts playing from the first track, and makes sure
    // it ends up expanded rather than toggling closed (unlike a plain click's
    // ToggleAlbumExpandedCommand).
    public void PlayAlbum(string albumName)
    {
        var tracks = BuildExpandedAlbumTracks(albumName);
        if (tracks.Count == 0)
            return;

        ExpandedAlbumName = albumName;
        ExpandedAlbumTracks = tracks;

        _playlistControlViewModel.SetCurrentPlaylist(new Playlist("Now Playing Queue", new List<Track>(tracks)));
        _playlistControlViewModel.Play(tracks[0]);
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

        // Restores whichever view (see AppSettings.LastSidebarKind/
        // LastPlaylistName's own doc comment) the user was on when the app
        // last closed, falling back to Songs the same way this always did -
        // on a genuine first run, when the saved view no longer exists (a
        // deleted playlist), or when nothing was ever saved at all.
        var restored = ResolveLastSidebarItem();
        WasLastViewRestored = restored != null;
        _selectedSidebarItem = restored ?? _sidebarItems.FirstOrDefault(i => i.Kind == SidebarItemKind.Songs);
        OnPropertyChanged(nameof(SelectedSidebarItem));
    }

    // Whether BuildSidebarItems above actually found and restored the saved
    // view, rather than falling back to Songs - consulted by MainView.axaml.cs
    // (SeedRestoredViewState) to decide whether LastScrollOffsetY below is
    // even meaningful for whatever SelectedSidebarItem ended up being: a
    // scroll offset captured for a since-deleted playlist has nothing to do
    // with the Songs view a failed restore falls back to.
    public bool WasLastViewRestored { get; private set; }

    public double LastScrollOffsetY => _appSettings?.LastScrollOffsetY ?? 0;

    private SidebarItem? ResolveLastSidebarItem()
    {
        if (_appSettings?.LastSidebarKind is not { } kindText || !Enum.TryParse<SidebarItemKind>(kindText, out var kind))
            return null;

        if (kind == SidebarItemKind.Playlist)
            return _appSettings.LastPlaylistName is { } name
                ? _sidebarItems.FirstOrDefault(i => i.Kind == SidebarItemKind.Playlist && i.Name == name)
                : null;

        // Header/Device are never a saved selection in practice (see
        // SaveLastView below, which only ever writes one of these four), but
        // guarded anyway since this reads straight back out of a JSON file a
        // user could hand-edit.
        return kind is SidebarItemKind.Songs or SidebarItemKind.Albums or SidebarItemKind.Artists or SidebarItemKind.RecentlyAdded
            ? _sidebarItems.FirstOrDefault(i => i.Kind == kind)
            : null;
    }

    // Called from MainView.axaml.cs (MainWindow.Closing, alongside the window
    // geometry save) with whichever scroll offset is relevant to the view
    // showing at that moment (MusicListView's or one of the album grids',
    // depending on IsShowingAlbumGrid/IsShowingRecentlyAddedGrid) - this
    // class has no visibility into either control's own scroll position
    // itself. Synchronous Save, not SaveAsync, for the same reason
    // MainWindow.SaveWindowGeometry uses it: the process may exit before an
    // async write completes.
    public void SaveLastView(double scrollOffsetY)
    {
        _appSettings ??= new AppSettings();
        _appSettings.LastSidebarKind = _selectedSidebarItem?.Kind.ToString();
        _appSettings.LastPlaylistName = _selectedSidebarItem?.Kind == SidebarItemKind.Playlist
            ? _selectedSidebarItem.Playlist?.Name
            : null;
        _appSettings.LastScrollOffsetY = scrollOffsetY;
        _appSettingsStore?.Save(_appSettings);
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
            RemoveDuplicateDeviceSidebarItems(existing, device);
            RefreshDeviceDisplayNames();
            return;
        }

        if (_sidebarItems.All(i => i.Kind != SidebarItemKind.Device))
            _sidebarItems.Add(new SidebarItem(SidebarItemKind.Header, "Devices"));

        var added = new SidebarItem(SidebarItemKind.Device, ResolveDeviceDisplayName(device), MaterialIconKind.Laptop, device: device);
        _sidebarItems.Add(added);
        RemoveDuplicateDeviceSidebarItems(added, device);
        RefreshDeviceDisplayNames();
    }

    // A peer can transiently be discovered under more than one mDNS instance
    // name for the same physical device - e.g. a prior run's advertisement
    // wasn't cleanly withdrawn before a fresh one republished under an
    // auto-renamed instance name (Bonjour's own collision-avoidance). Each
    // shows up as its own sidebar item (via FindDeviceSidebarItem's
    // InstanceName fallback, since neither has a resolved Fingerprint yet to
    // match on) until one of them resolves a Fingerprint that turns out to
    // match another already-tracked item - at which point they're revealed
    // to be duplicates of the same device. Removes every OTHER Device
    // sidebar item sharing that now-resolved Fingerprint, keeping only the
    // one AddOrUpdateDeviceSidebarItem just added/updated.
    private void RemoveDuplicateDeviceSidebarItems(SidebarItem keep, DiscoveredDevice device)
    {
        if (string.IsNullOrEmpty(device.Fingerprint))
            return;

        var duplicates = _sidebarItems
            .Where(i => i.Kind == SidebarItemKind.Device && i != keep && i.Device?.Fingerprint == device.Fingerprint)
            .ToList();
        foreach (var duplicate in duplicates)
            RemoveDeviceItem(duplicate, clearSyncDedup: false);
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
    // Rename Device context-menu item, TrustedDevicesView) always wins over
    // whatever the peer itself reports - otherwise the next DeviceDiscovered
    // re-fire (e.g. once /info resolves, or on periodic rediscovery) would
    // silently clobber a rename back to the peer's own alias.
    private string ResolveDeviceDisplayName(DiscoveredDevice device) =>
        !string.IsNullOrEmpty(device.Fingerprint) && _deviceNicknameStore?.Get(device.Fingerprint) is { Length: > 0 } nickname
            ? nickname
            : device.Alias;

    // The single place that re-derives a Device sidebar item's displayed name
    // from ResolveDeviceDisplayName - every place a device's nickname can
    // change (this sidebar's own "Rename Device" context menu, and
    // TrustedDevicesView's pencil-icon rename) calls this afterward, so
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

        RemoveDeviceItem(matches[0], clearSyncDedup: true);
    }

    // Shared by RemoveDeviceSidebarItem (a peer actually went offline, per
    // mDNS's own goodbye notification - clearSyncDedup: true, so a fresh
    // sync fires if it's discovered again later this session rather than
    // silently being ignored by the dedup check) and
    // RemoveDuplicateDeviceSidebarItems (the peer never went offline - it
    // just turned out to already have another sidebar item once its
    // Fingerprint resolved, so clearSyncDedup: false: the surviving item is
    // the exact same still-present device and shares that Fingerprint,
    // clearing it here would just trigger a redundant resync of it for no
    // reason). Either way: reselect away if this item was selected, remove
    // it, and drop the "Devices" header once no Device items remain.
    private void RemoveDeviceItem(SidebarItem item, bool clearSyncDedup)
    {
        if (clearSyncDedup && item.Device?.Fingerprint is { Length: > 0 } fingerprint)
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
        // Server never initiates bulk sync; Client only ever bulk-syncs with
        // its one paired Server - see SyncRolePolicy.
        if (!SyncRolePolicy.ShouldInitiateSync(_appSettings?.IsServer ?? false, _appSettings?.PairedServerFingerprint, device.Fingerprint))
            return;
        if (!_syncedDeviceFingerprints.TryAdd(device.Fingerprint, 0))
            return;

        _logger.LogInformation("First contact with {Alias} ({Fingerprint}) this session, triggering initial sync",
            device.Alias, device.Fingerprint);
        // forceInitiator: true - this is always the Client's own paired
        // Server here (ShouldInitiateSync above already guarantees that), and
        // a Server never calls SyncWithAsync back (its own trigger paths are
        // gated off) - without this, PlaylistSyncService's ordinal-fingerprint
        // election could decide the Client isn't the initiator for roughly
        // half of all possible fingerprint pairs, and since the Server never
        // reciprocates, that pair would permanently never sync playlists.
        RunTrackedSync(() => _playlistSyncService?.SyncWithAsync(device, forceInitiator: true) ?? Task.CompletedTask);
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
        // Every fresh visit to Albums/Recently Added starts with nothing
        // expanded, never a remembered one - matches mobile, where switching
        // tabs and back always starts at the flat grid too.
        ExpandedAlbumName = null;
        ExpandedAlbumTracks = new ObservableCollection<Track>();

        OnPropertyChanged(nameof(IsSubListVisible));
        OnPropertyChanged(nameof(IsShowingAlbumGrid));
        OnPropertyChanged(nameof(IsShowingRecentlyAddedGrid));
        OnPropertyChanged(nameof(IsShowingTrackList));
        OnPropertyChanged(nameof(IsShowingDeviceDetail));
        OnPropertyChanged(nameof(SelectedDevice));
        // Live browse, unrestricted by Client/Server role/pairing - see
        // PeerLibraryViewModel's own doc comment. Fire-and-forget: the VM
        // guards against a stale request winning a race if the selection
        // changes again before this completes.
        if (SelectedDevice is { } device)
            _ = PeerLibrary.LoadAsync(device);
        // Recently Added carries its own independent sort state (see SortColumn),
        // so switching to/from it changes what these computed properties report.
        OnPropertyChanged(nameof(SortColumn));
        OnPropertyChanged(nameof(SortAscending));
        RebuildSubListItems();
        _renamePlaylistCommand?.NotifyCanExecuteChanged();
        _deletePlaylistCommand?.NotifyCanExecuteChanged();

        // Albums no longer auto-selects an album on a fresh visit - it starts
        // at the grid instead (see IsShowingAlbumGrid), matching mobile's
        // Albums tab. Only Artists keeps the old auto-select-first/remembered
        // behavior, since its plain-text picker is unchanged.
        var initial = _selectedSidebarItem?.Kind == SidebarItemKind.Artists
            ? (_lastSelectedArtist != null && _subListItems.Contains(_lastSelectedArtist)
                ? _lastSelectedArtist
                : _subListItems.FirstOrDefault())
            : null;
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
            // RecentlyAdded here too, not just Albums - dragging a
            // multi-selection straight off the Recently Added grid (without
            // a plain click ever switching the sidebar to Albums first - see
            // SelectAlbumTile) still needs to resolve to real tracks by album
            // name, same as Albums' own grid does.
            SidebarItemKind.Albums or SidebarItemKind.RecentlyAdded
                => _allTracks.Where(t => t.Album != null && set.Contains(t.Album)),
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
        RebuildAlbumGrids();
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
        var allTracks  = _allTracks;

        // Albums/Recently Added show a tile grid instead of Rows (see
        // IsShowingAlbumGrid) built straight from _allTracks, not from
        // GetBaseTracksForFilter's (mostly Albums-view-irrelevant) result -
        // so without this, FilterText had no effect on either grid at all.
        // Rebuilt here, alongside Rows, on every filter/sort/view change
        // rather than only on a rescan (see RebuildAlbumGrids), so typing in
        // the search box while on Albums actually narrows the grid.
        var (rows, albumTiles, recentTiles) = await Task.Run(() =>
        {
            var builtRows = TrackListBuilder.Build(baseTracks, text, sortCol, sortAsc, playing, _sortArtistAlbumsByYear);
            var filteredForGrids = TrackListBuilder.Filter(allTracks, text).ToList();
            return (
                builtRows,
                AlbumGridBuilder.Build(filteredForGrids),
                RecentlyAddedAlbumsBuilder.Build(filteredForGrids));
        }, token);

        if (token.IsCancellationRequested)
            return;

        _currentFilteredTracks = rows.Select(r => r.Track).ToList();
        Rows = new ObservableCollection<TrackRowViewModel>(rows);
        AlbumGridTiles = new ObservableCollection<AlbumTileViewModel>(albumTiles);
        RecentlyAddedGridTiles = new ObservableCollection<AlbumTileViewModel>(recentTiles);
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
