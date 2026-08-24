using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

using Avalonia.Threading;

using CommunityToolkit.Mvvm.Input;

using Microsoft.Extensions.Logging;

using Flower.Controls;
using Flower.Importer;
using Flower.Logging;
using Flower.Models;
using Flower.Persistence;
using Flower.Services;
using Flower.ViewModels.Mobile;

using Material.Icons;

namespace Flower.ViewModels;

public partial class MainViewModel : ViewModelBase, IDisposable, IDeviceSidebarHost, IPeerSyncHost, IPlaylistManagementHost, ILibraryBrowseHost, ISidebarRenameHost
{
    // Defaults to a no-op logger for the parameterless design-time constructor
    // below, which never receives one via DI - overwritten by the real
    // constructor's injected ILogger<MainViewModel> otherwise.
    private ILogger _logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    private readonly PlaylistControlViewModel _playlistControlViewModel;
    private AppSettings _appSettings;
    private IMusicImporter? _importer;
    private MainPlaylist?      _mainPlaylist;
    private DeviceIdentity? _deviceIdentity;

    // Kept, not just consumed in the constructor: "Server Settings..." signs a
    // request against the selected server with it (see
    // OpenSelectedServerSettingsAsync). Null on Flower.Web/WASM, same as
    // _deviceIdentity - see the constructor's note on the sync-stack parameters.
    private readonly DeviceSigningKey? _signingKey;
    private PairedServerReachability? _reachability;
    private AppSettingsStore? _appSettingsStore;

    // The P2P sync coordinator - when to sync, with whom, and the
    // pairing/trust handshake around it. Everything below that touches sync is
    // a forwarder onto it, kept here because XAML (MainView, ServerPickerView,
    // mobile's SettingsView) and MobileMainViewModel bind to this ViewModel.
    public PeerSyncCoordinator Sync { get; } = null!;

    public bool IsSyncing => Sync.IsSyncing;

    public void ScheduleContentSync() => Sync.ScheduleContentSync();

    public ICommand? OpenAppDataLocationCommand  { get; private set; }
    public ICommand? RebuildDatabaseCommand      { get; private set; }
    public ICommand? SortByColumnCommand         { get; private set; }
    public ICommand? OpenSettingsCommand         { get; private set; }
    public ICommand? OpenColumnSelectorCommand   { get; private set; }
    public ICommand? OpenLogWindowCommand        { get; private set; }
    public ICommand? OpenEqualizerWindowCommand  { get; private set; }
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
    public event EventHandler? LogWindowRequested;
    public event EventHandler? EqualizerWindowRequested;
    public event EventHandler<Track>? NavigateToTrackRequested;
    public event EventHandler<PlaylistConflictEventArgs>? PlaylistConflictRequested;

    // Forwards PairedServerReachability.Changed - see the constructor's own
    // subscription to it. Lets MobileMainViewModel (SearchSongResults, a row
    // list that doesn't live on Rows) react without needing its own direct
    // reference to the reachability service.
    public event EventHandler? ReachabilityChanged;

    // Raised by the "Playlist > Rename Playlist" main-menu command - unlike
    // deleting, renaming needs the sidebar's own inline-rename textbox (see
    // MainView.axaml.cs's BeginRename), which is a View concern this ViewModel
    // cannot reach directly.
    public event EventHandler? RenamePlaylistRequested;

    // See DeletePlaylistConfirmationEventArgs above.
    public event EventHandler<DeletePlaylistConfirmationEventArgs>? DeletePlaylistConfirmationRequested;

    public Library Library { get; private set; }

    public IReadOnlyList<string> LibraryPaths => _appSettings.LibraryPaths;

    // What this device calls itself to peers - see PeerSyncCoordinator.DeviceAlias.
    public string DeviceAlias
    {
        get => Sync.DeviceAlias;
        set => Sync.DeviceAlias = value;
    }

    // Master switch for the whole iTunes/Music.app integration - see
    // AppSettings.IntegrateWithITunes. Persisted immediately on change, like
    // the two flags it gates below; the callers of those two
    // (App.axaml.cs's startup rescan and SettingsWindow.SaveButton_Click)
    // check this first, so nothing here has to reach out and clear them.
    public bool IntegrateWithITunes
    {
        get => _appSettings.IntegrateWithITunes;
        set
        {
            if (_appSettings.IntegrateWithITunes == value)
                return;
            _logger.LogInformation("IntegrateWithITunes changed {Old} -> {New}", _appSettings.IntegrateWithITunes, value);
            _appSettings.IntegrateWithITunes = value;
            SaveSettings();
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
        get => _appSettings.SyncPlayCountFromITunes;
        set
        {
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
            SaveSettings();
        }
    }

    // Whether to import per-track "Date Added" from iTunes/Music.app on every
    // launch - see ITunesDateAddedImporter. Same persist-immediately-but-
    // OK-gated-sync pattern as SyncPlayCountFromITunes above.
    public bool SyncDateAddedFromITunes
    {
        get => _appSettings.SyncDateAddedFromITunes;
        set
        {
                if (_appSettings.SyncDateAddedFromITunes == value)
                return;
            // See SyncPlayCountFromITunes's own comment on this same logging.
            _logger.LogInformation("SyncDateAddedFromITunes changed {Old} -> {New}\n{StackTrace}", _appSettings.SyncDateAddedFromITunes, value, Environment.StackTrace);
            _appSettings.SyncDateAddedFromITunes = value;
            SaveSettings();
        }
    }

    // Whether this device pushes its recent log lines to its paired server
    // after each library sync - on by default, so a listener who cannot be
    // asked to read a log file still leaves one where the owner can find it.
    // See AppSettings.ShareLogsWithPairedServer for what the payload carries.
    // Persist-immediately, same as the two above.
    public bool ShareLogsWithPairedServer
    {
        get => _appSettings.ShareLogsWithPairedServer;
        set
        {
                if (_appSettings.ShareLogsWithPairedServer == value)
                return;
            _appSettings.ShareLogsWithPairedServer = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    public string? PairedServerFingerprint => Sync.PairedServerFingerprint;
    public string? PairedServerAlias       => Sync.PairedServerAlias;

    public IEnumerable<DiscoveredDevice> AvailableServers => Sync.AvailableServers;

    // Whether that list has anything in it, so mobile's Settings sheet can say
    // "none found yet" instead of leaving an empty gap under a heading - a
    // phone that has never paired has no other clue about whether discovery is
    // working at all.
    public bool HasAvailableServers => AvailableServers.Any();

    public void PairWithServer(DiscoveredDevice device, string pairingCode) =>
        Sync.PairWithServer(device, pairingCode);

    public void UnpairServer() => Sync.UnpairServer();

    public bool IsPairedServerReachable => Sync.IsPairedServerReachable;

    // The two above as one bindable value, for the views that hand "what
    // counts as playable right now" down to rows they build themselves - see
    // AlbumGridView.Availability, which pushes it into an expanded album's
    // track rows. Everything else in the app gets availability already
    // computed onto its rows/tiles (TrackAvailability.Apply) and has no need
    // of this.
    public TrackAvailabilityContext Availability => new(PairedServerFingerprint, IsPairedServerReachable);

    // Gates mobile SettingsView's "Server not reachable" line - separate
    // from IsPairedServerReachable's own negation so it only shows once
    // actually paired with something; "not paired with a server at all" is
    // a different, non-error state that already has its own message there.
    public bool ShowPairedServerUnreachableWarning =>
        !string.IsNullOrEmpty(PairedServerFingerprint) && !IsPairedServerReachable;

    // See PeerSyncCoordinator.PairedServerRouteDescription - null while the
    // server is reached the ordinary way, on the local network.
    public string? PairedServerRouteDescription => Sync.PairedServerRouteDescription;

    // Servers reached by an address the user typed rather than by discovery -
    // see PeerSyncCoordinator.AddManualServerAsync.
    public IEnumerable<string> ManualServerAddresses => Sync.ManualServerAddresses;

    public Task<bool> AddManualServerAsync(string address) => Sync.AddManualServerAsync(address);

    public void RemoveManualServer(string address) => Sync.RemoveManualServer(address);

    public bool CanForceSync => Sync.CanForceSync;

    public string? LastForceSyncResult => Sync.LastForceSyncResult;

    public void ForceSyncNow() => Sync.ForceSyncNow();

    // Settings' Appearance picker (Follow System / Light / Dark) - see
    // Flower.Services.AppTheme for how this becomes an actual Avalonia
    // ThemeVariant. Same apply-immediately, persist-immediately pattern as
    // SyncPlayCountFromITunes above.
    public AppThemePreference ThemePreference
    {
        get => _appSettings.ThemePreference;
        set
        {
                if (_appSettings.ThemePreference == value)
                return;
            _appSettings.ThemePreference = value;
            SaveSettings();
            AppTheme.Apply(value);
        }
    }

    // Both delegate to ITunesImportCoordinator, which owns the cooldown and
    // the apply/notify/save sequence - kept here as forwarders because
    // App.axaml.cs's startup rescan and SettingsWindow both reach them through
    // this ViewModel.
    public Task SyncITunesPlayCountAsync() => ITunesImport.SyncPlayCountAsync();
    public Task SyncITunesDateAddedAsync() => ITunesImport.SyncDateAddedAsync();

    // ── Child ViewModels the top bar and status bar bind to ───────────────────

    // Exposed so MainView.axaml can hand each control its DataContext
    // declaratively ({Binding PlaybackControls} and friends) instead of the
    // control reaching into Ioc.Default for its own ViewModel from its
    // constructor - see docs/ARCHITECTURE-REVIEW.md Tier 2.3. Same forwarding
    // face 4.2 gave the collaborators split out of this class: the container
    // is consulted once, here, and everything below flows down the visual tree.
    public PlaylistControlViewModel PlaybackControls => _playlistControlViewModel;
    public VolumeControlViewModel Volume { get; }
    public CurrentlyPlayingControlViewModel NowPlaying { get; }

    // The two non-modal windows MainView opens on command (Log, Equalizer) and
    // the sidebar rename service it drives from a TextBox teardown. Same
    // reasoning as the three above: MainView is instantiated by XAML and has no
    // constructor to inject through, so what it needs arrives through the one
    // object it is already given - its DataContext.
    // The tag-edit views (TrackInfoWindow, mobile's TrackInfoView) persist
    // through this after writing tags back to disk. Exposed for the same
    // reason as the rest of this block - they are windows and screens with no
    // constructor the container reaches.

    public LogViewModel? Log { get; }
    public EqualizerViewModel Equalizer { get; }
    public SidebarRenameService Rename { get; }

    // Settings' Devices tab (ServerPickerView) used to resolve these out of
    // Ioc.Default in its own field initializers. They arrive the same way as
    // everything above now - through the one object the view is handed - which
    // is what lets a test build that view against fakes. The nullable one is
    // the discovery stack, which does not exist on WASM at all; the views that
    // read it are only ever constructed on a platform that has it.
    public DeviceNicknameStore DeviceNicknames { get; }
    public NetworkDiscoveryService? NetworkDiscovery { get; }

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

    // Every settings-backed property below persists immediately on change
    // rather than waiting for an OK button - this is the one place that write
    // happens, replacing the nine hand-repeated
    // "lazily create, mutate, fire-and-forget SaveAsync" triplets this class
    // used to carry (ARCHITECTURE-REVIEW Tier 4.2). The store is null only for
    // the design-time constructor, which has nothing to persist.
    private void SaveSettings() => _ = (_appSettingsStore?.SaveAsync(_appSettings) ?? Task.CompletedTask);

    // ── Library browsing ──────────────────────────────────────────────

    // Rows, the search filter, the three sort states, the tile grids and the
    // Artists sub-list all live in LibraryBrowserViewModel; the members below
    // forward onto it because MainView.axaml, MusicListView and
    // MobileMainViewModel all bind through this ViewModel.
    public LibraryBrowserViewModel Browser { get; } = null!;

    public ObservableCollection<TrackRowViewModel> Rows => Browser.Rows;
    public IReadOnlyList<Track> DisplayedTracks         => Browser.DisplayedTracks;
    public string StatusBarText                         => Browser.StatusBarText;

    public string? FilterText
    {
        get => Browser.FilterText;
        set => Browser.FilterText = value;
    }

    public string SortColumn   => Browser.SortColumn;
    public bool   SortAscending => Browser.SortAscending;

    public bool SortArtistAlbumsByYear
    {
        get => Browser.SortArtistAlbumsByYear;
        set => Browser.SortArtistAlbumsByYear = value;
    }

    public ObservableCollection<AlbumTileViewModel> AlbumGridTiles         => Browser.AlbumGridTiles;
    public ObservableCollection<AlbumTileViewModel> RecentlyAddedGridTiles => Browser.RecentlyAddedGridTiles;

    public string? ExpandedAlbumName                     => Browser.ExpandedAlbumName;
    public ObservableCollection<Track> ExpandedAlbumTracks => Browser.ExpandedAlbumTracks;

    public ObservableCollection<string> SubListItems => Browser.SubListItems;

    public IReadOnlyCollection<string> SelectedSubItems => Browser.SelectedSubItems;

    public string? SelectedSubItem
    {
        get => Browser.SelectedSubItem;
        set => Browser.SelectedSubItem = value;
    }

    public void SetSelectedSubItems(IReadOnlyList<string> items) => Browser.SetSelectedSubItems(items);

    public string CurrentViewKey => Browser.CurrentViewKey;

    public IEnumerable<Track> GetTracksForSubListItems(IEnumerable<string> items) =>
        Browser.GetTracksForSubListItems(items);

    public Task<bool> RebuildRowsImmediatelyAsync(bool includeGridTiles = true) =>
        Browser.RebuildRowsImmediatelyAsync(includeGridTiles);

    private void ScheduleFilter() => Browser.ScheduleFilter();

    // ── ILibraryBrowseHost ────────────────────────────────────────────────

    SidebarItemKind? ILibraryBrowseHost.CurrentKind => _selectedSidebarItem?.Kind;
    Playlist? ILibraryBrowseHost.CurrentPlaylist    => _selectedSidebarItem?.Playlist;

    void ILibraryBrowseHost.PersistSort(string column, bool ascending)
    {
        _appSettings.SortColumn    = column;
        _appSettings.SortAscending = ascending;
        SaveSettings();
    }

    void ILibraryBrowseHost.PersistSortArtistAlbumsByYear(bool value)
    {
        _appSettings.SortArtistAlbumsByYear = value;
        SaveSettings();
    }

    // ── Busy state ────────────────────────────────────────────────────

    // The counter itself lives in BusyState, shared with the collaborators
    // split out of this class (see ITunesImportCoordinator) so they can raise
    // the same one status-bar spinner without a back-reference here; these two
    // are the bindable face of it (MainView.axaml).
    private readonly BusyState _busy;

    // Owns the iTunes/Music.app play-count and Date Added imports (see that
    // class); public so App.axaml.cs's startup rescan can drive it directly
    // rather than through this ViewModel's own two forwarders below.
    public ITunesImportCoordinator ITunesImport { get; }

    public bool    IsBusy      => _busy.IsBusy;
    public string? BusyMessage => _busy.Message;

    // Public entry point for App.axaml.cs's startup sequence, which needs to
    // keep the spinner up across the whole rescan + both iTunes syncs as one
    // continuous scope (nesting further scopes inside it just updates
    // BusyMessage as each step starts - see BusyState) rather than relying on
    // each step's own brief individual scope, since the rescan itself - the
    // longest part by far, ~9s against a large real library - had no busy
    // coverage of its own at all.
    public IDisposable BeginBusyScope(string? message = null) => _busy.BeginScope(message);

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

    public bool IsShowingDeviceDetail => _selectedSidebarItem?.Kind == SidebarItemKind.Device;
    public DiscoveredDevice? SelectedDevice => _selectedSidebarItem?.Device;

    // The selected server's own settings, administered in place: the same
    // SettingsPanel its browser UI serves, over a RemoteServerSettingsBackend
    // signed as this device (see RefreshSelectedServerSettings). Null while
    // there is nothing to administer - no server row selected, or one that has
    // not approved this device yet.
    //
    // This is what the device-detail pane shows instead of what used to be
    // there, a browse view of that server's catalog. That view was the same
    // music twice: a paired server's tracks are merged into Library by
    // LibrarySyncService and stream from the ordinary track list through
    // IStreamUrlResolver, so the one thing about a server that had nowhere
    // else to live was its settings.
    private SettingsViewModel? _selectedServerSettings;
    public SettingsViewModel? SelectedServerSettings
    {
        get => _selectedServerSettings;
        private set
        {
            if (ReferenceEquals(_selectedServerSettings, value))
                return;
            _selectedServerSettings = value;
            OnPropertyChanged();
        }
    }

    // Whether the device-detail header's Pair/Unpair button should show at
    // all for SelectedDevice. Every discovered device is a server now, so the
    // only question left is whether there is a row to act on: a live one, or
    // the pinned paired-server row while it is unreachable (SelectedDevice
    // null - see RemoveDeviceItem), so "Unpair" stays available even with
    // nothing currently answering.
    public bool CanPairWithSelectedDevice =>
        SelectedDevice != null || (_selectedSidebarItem?.IsPairedServer ?? false);

    // Driven by the sidebar row's own IsPairedServer flag rather than
    // re-matching SelectedDevice's Fingerprint - stays correct even while
    // the pinned paired-server row is unreachable and SelectedDevice is
    // temporarily null (see RemoveDeviceItem).
    public bool IsSelectedDevicePaired => _selectedSidebarItem?.IsPairedServer ?? false;

    // True once the currently-paired server has actually approved this
    // device (see AppSettings.PairedServerTrustConfirmed), independent of
    // sidebar selection - drives the green checkmark next to the server's
    // name (desktop's device-detail header, via IsSelectedDeviceTrustConfirmed
    // below) and mobile's SettingsView server row, which has no concept of a
    // "selected" device to key off of.
    public bool IsPairedServerTrustConfirmed => !string.IsNullOrEmpty(PairedServerFingerprint) && _appSettings.PairedServerTrustConfirmed;

    // Paired but not yet approved - the request has been sent and is sitting
    // at the server's own approval popup. Drives the "Waiting for server..."
    // label/spinner on both surfaces above, until either the server approves
    // it (ConfirmServerTrust) or the user gives up and clicks Unpair.
    public bool IsPairedServerAwaitingApproval => !string.IsNullOrEmpty(PairedServerFingerprint) && !IsPairedServerTrustConfirmed;

    // SelectedDevice-scoped versions of the two above, for the device-detail
    // header specifically - SelectedDevice is only ever the paired server
    // here (IsSelectedDevicePaired), so these track IsPairedServerTrustConfirmed/
    // IsPairedServerAwaitingApproval 1:1 whenever they're true, but stay false
    // if the user is merely looking at some *other*, unpaired device.
    public bool IsSelectedDeviceTrustConfirmed => IsSelectedDevicePaired && IsPairedServerTrustConfirmed;
    public bool IsPairAwaitingApproval => IsSelectedDevicePaired && IsPairedServerAwaitingApproval;

    // Mirrors ServerRow's ActionLabel/IsActionEnabled/HintText
    // (ServerPickerView, Settings' Devices tab) - same states, surfaced
    // inline in the device-detail header so pairing doesn't need a trip to
    // Settings. Switching to a different server still requires an explicit
    // unpair-first step (PairActionHint), same as ServerPickerView. Clicking
    // "Waiting for server..." still runs the Unpair flow (PairActionButton_Click
    // branches on IsSelectedDevicePaired, not on trust) - it's the only way
    // to cancel a pending request.
    // "Pair" rather than "Ask to pair": nothing is being asked. The code the
    // admin already issued is the approval, so the action completes in one
    // round trip instead of leaving a request sitting at somebody else's
    // prompt - which is just as well, since a headless server has no prompt.
    public string PairActionLabel =>
        !IsSelectedDevicePaired ? "Pair" :
        IsSelectedDeviceTrustConfirmed ? "Unpair" :
        "Waiting for server...";

    // Whether to show the pairing-code box next to the button. A server already
    // paired with has nothing left to redeem.
    public bool IsPairingCodeRequired => !IsSelectedDevicePaired;

    // Whether the device-detail header offers "Server Settings...". Only for a
    // server that has already accepted this device, since that button opens its
    // browser UI.
    //
    // Deliberately not also gated on this device being an *admin* of that server:
    // nothing here can know that without asking, TrustedPeer.IsAdmin lives on the
    // server, and a hidden button is a worse answer than a button that says why it
    // did not work (see ServerSettingsError).
    public bool CanOpenSelectedServerSettings =>
        IsSelectedDeviceTrustConfirmed && _signingKey != null && _deviceIdentity != null;

    // Set when minting a browser session against the selected server failed -
    // most usefully when this device is paired but not an administrator, which is
    // an ordinary outcome and so is said inline next to the button rather than
    // raised as a dialog. Same treatment as PairingCodeError.
    private string? _serverSettingsError;
    public string? ServerSettingsError
    {
        get => _serverSettingsError;
        private set
        {
            if (_serverSettingsError == value)
                return;
            _serverSettingsError = value;
            OnPropertyChanged();
        }
    }

    private bool _isOpeningServerSettings;
    public bool IsOpeningServerSettings
    {
        get => _isOpeningServerSettings;
        private set
        {
            if (_isOpeningServerSettings == value)
                return;
            _isOpeningServerSettings = value;
            OnPropertyChanged();
        }
    }

    // Opens the selected server's own settings page in the OS browser.
    //
    // A browser tab holds its own WebCrypto keypair and pairs like any other
    // device (see BrowserPeerCredentials), so what this hands over is not a
    // credential for the tab to *use* but a one-time, admin-granting pairing
    // code for it to spend on becoming a device. In the URL fragment rather than
    // the query string because a fragment is never sent to the server as part of
    // the request, so the code cannot end up in an access log on the way in.
    //
    // Opening this a second time for a tab that already paired issues a code
    // nothing needs, which the tab's redeem attempt spends harmlessly. That is
    // cheap enough not to be worth a round-trip to find out first, and it is
    // also the way back in after site data is cleared.
    public async Task OpenSelectedServerSettingsAsync()
    {
        if (SelectedDevice is not { } device || _signingKey == null || _deviceIdentity == null)
            return;

        ServerSettingsError = null;
        IsOpeningServerSettings = true;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var client = CreateServerAdminClient(http, device);

            var pairing = await client.IssuePairingCodeAsync(grantsAdmin: true);
            OpenUrlInBrowser(pairing.BrowserUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open the settings page for {Device}", device.BaseUri);
            ServerSettingsError = ex is ServerAdminException ? ex.Message : "Could not reach that server.";
        }
        finally
        {
            IsOpeningServerSettings = false;
        }
    }

    // Same per-OS launch shape as OpenAppDataLocation below, against a URL rather
    // than a folder. UseShellExecute is what makes a non-Windows Process.Start
    // hand a URL to the desktop's default handler at all.
    private static void OpenUrlInBrowser(string url)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            Process.Start(new ProcessStartInfo { FileName = "open", ArgumentList = { url } });
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        else
            Process.Start(new ProcessStartInfo { FileName = "xdg-open", ArgumentList = { url } });
    }


    // ── Handing out pairing codes ─────────────────────────────────────────────

    // "Generate Pairing Code" on mobile's Settings sheet: the same one-time code
    // the server hands out through its own settings screen (see
    // SettingsViewModel's IssuePairingCodeCommand and PairingCodeService).
    //
    // Desktop no longer has a second one of these. It used to sit in the
    // device-detail header, next to a pane that browsed the server's catalog;
    // that pane now *is* the server's settings screen (SelectedServerSettings),
    // whose Devices tab already offers this - and two identical buttons on one
    // page is worse than the trip the header one was saving. A phone has no
    // such pane, which is why this state stays.
    //
    // An ordinary listener's code unless PairingCodeGrantsAdmin is ticked: the
    // common case is adding somebody's phone, and a device that only listens has
    // no business handing out codes of its own - but an owner's second device is
    // a real case too.
    private string? _issuedPairingCode;
    public string? IssuedPairingCode
    {
        get => _issuedPairingCode;
        private set
        {
            if (_issuedPairingCode == value)
                return;
            _issuedPairingCode = value;
            OnPropertyChanged();
        }
    }

    // Whether the code currently on screen is an administrator's. Said out loud
    // next to it rather than left to the checkbox above: the checkbox describes
    // the *next* press, and the two disagree the moment it is un-ticked with a
    // code still showing.
    private bool _issuedPairingCodeGrantsAdmin;
    public bool IssuedPairingCodeGrantsAdmin
    {
        get => _issuedPairingCodeGrantsAdmin;
        private set
        {
            if (_issuedPairingCodeGrantsAdmin == value)
                return;
            _issuedPairingCodeGrantsAdmin = value;
            OnPropertyChanged();
        }
    }

    // Same treatment as ServerSettingsError: "paired, but not an administrator of
    // that server" is the most likely answer here and is an ordinary outcome of
    // clicking the button, not a fault worth a dialog.
    private string? _issuedPairingCodeError;
    public string? IssuedPairingCodeError
    {
        get => _issuedPairingCodeError;
        private set
        {
            if (_issuedPairingCodeError == value)
                return;
            _issuedPairingCodeError = value;
            OnPropertyChanged();
        }
    }

    private bool _isIssuingPairingCode;
    public bool IsIssuingPairingCode
    {
        get => _isIssuingPairingCode;
        private set
        {
            if (_isIssuingPairingCode == value)
                return;
            _isIssuingPairingCode = value;
            OnPropertyChanged();
        }
    }

    // Whether mobile's Settings sheet offers to hand out a pairing code. A phone
    // has no sidebar to select a server in, so it asks the one server it is
    // paired with - resolved through PairedServerReachability, which knows the
    // server's current address whether it was found on this link or remembered
    // from home (see docs/REMOTE-ACCESS-PLAN.md).
    //
    // Gated on this device being one of that server's administrators
    // (DiscoveredDevice.WeAreAdmin, answered on the same 5s /info poll
    // everything else here rides on): a control whose every press returns the
    // server's 403 reads as broken rather than as forbidden. Still only a
    // display decision - AdminEndpoints re-checks TrustedPeer.IsAdmin on every
    // request, so a client that lies to itself about this gains nothing.
    public bool CanInviteDeviceToPairedServer =>
        _reachability?.PairedServerDevice is { WeAreAdmin: true }
        && IsPairedServerTrustConfirmed && _signingKey != null && _deviceIdentity != null;

    // Whether the code the next "Generate Pairing Code" press asks for should
    // grant administrator rights - the checkbox next to that button, on both
    // the desktop sidebar and mobile's Settings sheet.
    //
    // Off by default and deliberately not remembered between presses: adding a
    // listener's phone is the ordinary case, and a checkbox that stayed ticked
    // from an hour ago would quietly turn the next guest into an administrator.
    // More than one device may hold it at once, though - somebody's own desktop
    // and their own phone both being admins is the expected shape, not an
    // exception (see TrustedPeerStore.TrustedPeer.IsAdmin).
    private bool _pairingCodeGrantsAdmin;
    public bool PairingCodeGrantsAdmin
    {
        get => _pairingCodeGrantsAdmin;
        set
        {
            if (_pairingCodeGrantsAdmin == value)
                return;
            _pairingCodeGrantsAdmin = value;
            OnPropertyChanged();
        }
    }

    public Task InviteDeviceToPairedServerAsync() => IssuePairingCodeAgainstAsync(_reachability?.PairedServerDevice);

    private async Task IssuePairingCodeAgainstAsync(DiscoveredDevice? device)
    {
        if (device == null || _signingKey == null || _deviceIdentity == null)
            return;

        IssuedPairingCodeError = null;
        // The previous code is dropped before the request rather than after it:
        // a stale code left on screen next to a spinner reads as the new one.
        IssuedPairingCode = null;
        IssuedPairingCodeGrantsAdmin = false;
        IsIssuingPairingCode = true;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var client = CreateServerAdminClient(http, device);
            var pairing = await client.IssuePairingCodeAsync(PairingCodeGrantsAdmin);
            IssuedPairingCode = pairing.Code;
            IssuedPairingCodeGrantsAdmin = PairingCodeGrantsAdmin;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not issue a pairing code on {Device}", device.BaseUri);
            IssuedPairingCodeError = ex is ServerAdminException ? ex.Message : "Could not reach that server.";
        }
        finally
        {
            IsIssuingPairingCode = false;
        }
    }

    // Both admin calls this ViewModel makes sign as this device with the same
    // identity; only the route differs.
    private ServerAdminClient CreateServerAdminClient(HttpClient http, DiscoveredDevice device) =>
        new(http, device.BaseUri,
            ServerAdminClient.SignWith(new SignedDeviceCredentials(_deviceIdentity!, _signingKey!)));

    // One client for every settings panel this window ever shows, rather than
    // one per selection: a SettingsViewModel outlives the call that created it
    // (it saves, reloads and tails a log on demand), so the disposable-per-call
    // shape the two one-shot admin calls above use does not work here.
    private HttpClient? _adminHttp;

    // Which server the panel currently on screen was built against - both
    // halves matter: a different server obviously needs a new panel, and so
    // does the same server reached at a new address (a LAN-to-tailnet handover
    // moves BaseUri under a fingerprint that never changes).
    private (string Fingerprint, Uri BaseUri)? _selectedServerSettingsFor;

    // Rebuilds SelectedServerSettings for whatever server row is selected now.
    // Called both when the selection moves and whenever the pair-button state
    // changes, since pairing with the selected server - or its approval landing
    // a moment later - is what turns an unadministerable row into one whose
    // settings can be read at all.
    private void RefreshSelectedServerSettings()
    {
        var device = CanOpenSelectedServerSettings ? SelectedDevice : null;
        var target = device == null ? ((string, Uri)?)null : (device.Fingerprint, device.BaseUri);
        if (target == _selectedServerSettingsFor)
            return;

        _selectedServerSettingsFor = target;
        if (device == null)
        {
            SelectedServerSettings = null;
            return;
        }

        _adminHttp ??= new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        SelectedServerSettings = new SettingsViewModel(
            new RemoteServerSettingsBackend(CreateServerAdminClient(_adminHttp, device)),
            _appSettings, _appSettingsStore);
    }

    // What the user typed into that box. Cleared on a successful pair and
    // whenever the sidebar selection moves, so a code left over from one
    // attempt is never silently submitted against a different server.
    private string _pairingCode = "";
    public string PairingCode
    {
        get => _pairingCode;
        set
        {
            if (_pairingCode == value)
                return;
            _pairingCode = value;
            OnPropertyChanged();
            // The error is about one attempt, not a standing condition -
            // editing the code is the user retrying, so it clears.
            PairingCodeError = null;
            OnPropertyChanged(nameof(IsPairSubmittable));
        }
    }

    // Set when a redeem came back rejected (see PeerSyncCoordinator's
    // PairingCodeRejected), cleared the moment the user edits the code -
    // the message is about one attempt, not a standing condition.
    private string? _pairingCodeError;
    public string? PairingCodeError
    {
        get => _pairingCodeError;
        set
        {
            if (_pairingCodeError == value)
                return;
            _pairingCodeError = value;
            OnPropertyChanged();
        }
    }

    // The button is only actionable once there is something to submit -
    // otherwise "Pair" on an empty box would round-trip just to be rejected.
    public bool IsPairSubmittable =>
        !IsPairingCodeRequired || !string.IsNullOrWhiteSpace(PairingCode);
    public bool IsPairActionEnabled => IsSelectedDevicePaired || string.IsNullOrEmpty(PairedServerFingerprint);
    public string? PairActionHint =>
        !IsSelectedDevicePaired && !string.IsNullOrEmpty(PairedServerFingerprint) ? $"Unpair from {PairedServerAlias} first" : null;

    // Single place raising every pair-button property's PropertyChanged -
    // called whenever any input to them changes: the sidebar selection
    // (OnSidebarSelectionChanged), the paired server
    // (PairWithServer/UnpairServer), server
    // approval (ConfirmServerTrust), or SelectedDevice's underlying
    // DiscoveredDevice being refreshed (RefreshDeviceDisplayNames).
    //
    // Diffed against the last-notified values rather than re-raised
    // unconditionally: RefreshDeviceDisplayNames is one of the call sites and
    // runs off the 5s peer poll, where the answer is the same every time, and
    // each of these is a computed property the bindings then re-evaluate.
    // See docs/ARCHITECTURE-REVIEW.md Tier 1.5.
    private (bool, bool, bool, bool, bool, bool, string?, bool, string?, bool, bool, bool)? _lastPairButtonState;

    private void NotifyPairButtonPropertiesChanged()
    {
        var state = (
            CanPairWithSelectedDevice,
            IsSelectedDevicePaired,
            IsPairedServerTrustConfirmed,
            IsPairedServerAwaitingApproval,
            IsSelectedDeviceTrustConfirmed,
            IsPairAwaitingApproval,
            PairActionLabel,
            IsPairActionEnabled,
            PairActionHint,
            IsPairingCodeRequired,
            CanOpenSelectedServerSettings,
            // Carries its own term because it is gated on being an administrator
            // of the server in question, which the 5s /info poll can flip on its
            // own - see DiscoveredDevice.WeAreAdmin.
            CanInviteDeviceToPairedServer);

        if (_lastPairButtonState == state)
            return;

        _lastPairButtonState = state;

        // Before the notifications: CanOpenSelectedServerSettings is part of
        // the state tuple above, so this is the edge on which a freshly
        // approved server gains a settings panel - or a just-unpaired one
        // loses it.
        RefreshSelectedServerSettings();

        OnPropertyChanged(nameof(CanPairWithSelectedDevice));
        OnPropertyChanged(nameof(IsSelectedDevicePaired));
        OnPropertyChanged(nameof(IsPairedServerTrustConfirmed));
        OnPropertyChanged(nameof(IsPairedServerAwaitingApproval));
        OnPropertyChanged(nameof(IsSelectedDeviceTrustConfirmed));
        OnPropertyChanged(nameof(IsPairAwaitingApproval));
        OnPropertyChanged(nameof(PairActionLabel));
        OnPropertyChanged(nameof(IsPairActionEnabled));
        OnPropertyChanged(nameof(PairActionHint));
        OnPropertyChanged(nameof(IsPairingCodeRequired));
        OnPropertyChanged(nameof(IsPairSubmittable));
        OnPropertyChanged(nameof(CanOpenSelectedServerSettings));
        // Mobile's Settings sheet asks the *paired* server rather than the
        // selected one.
        OnPropertyChanged(nameof(CanInviteDeviceToPairedServer));
    }

    // ── Constructors ──────────────────────────────────────────────────────────

    // Design-time only, so the Avalonia XAML previewer/designer can construct
    // a DataContext instance via reflection - never invoked at runtime, so
    // the non-nullable fields/properties below are never actually observed
    // unpopulated.
#pragma warning disable CS8618
    public MainViewModel()
    {
        // The collaborators this class forwards to are constructed even here:
        // most of what MainView.axaml binds (Rows, StatusBarText, SubListItems,
        // the tile grids) now reads through Browser, so leaving it null would
        // make the previewer throw on the first binding rather than render an
        // empty window. Over an empty Library, so nothing touches disk.
        _appSettings = new AppSettings();
        Library   = new Library(new List<Track>());
        Browser   = new LibraryBrowserViewModel(Library, this, AppLogging.CreateTypedLogger<LibraryBrowserViewModel>());
        Playlists = new PlaylistManagementViewModel(Library, _sidebarItems, this);
    }
#pragma warning restore CS8618

    public MainViewModel(
        PlaylistControlViewModel playlistControlViewModel,
        Library library,
        AppSettings appSettings,
        IMusicImporter importer,
        MainPlaylist mainPlaylist,
        AppSettingsStore appSettingsStore,
        DeviceIdentityStore deviceIdentityStore,
        DeviceNicknameStore deviceNicknameStore,
        TrustedPeerStore trustedPeerStore,
        BusyState busy,
        ITunesImportCoordinator iTunesImport,
        AnimationClock animationClock,
        VolumeControlViewModel volume,
        CurrentlyPlayingControlViewModel nowPlaying,
        EqualizerViewModel equalizer,
        SidebarRenameService rename,
        ILogger<MainViewModel> logger,
        // Trailing + defaulted (not just nullable-typed) deliberately: these
        // don't exist at all on Flower.Web/WASM (no P2P sync stack there - see
        // DeviceSigningKey, which .NET-for-WASM's crypto backend cannot
        // produce at all), and aren't registered in that
        // platform's DI container. A bare "T? x" parameter with no "= null"
        // is NOT enough for the container to pick this constructor over the
        // parameterless one above when T isn't registered - verified directly
        // against Microsoft.Extensions.DependencyInjection's actual constructor-
        // selection behavior, which only treats a parameter as satisfiable-
        // when-unregistered if it has a real default value (nullable
        // annotations alone aren't consulted). Every platform that does have a
        // P2P sync stack still gets its real, non-null instances here exactly
        // as before - registered services always win over the default.
        NetworkDiscoveryService? networkDiscovery = null,
        PairedServerReachability? reachability = null,
        PlaylistSyncService? playlistSyncService = null,
        LibrarySyncService? librarySyncService = null,
        LibraryDownloadService? libraryDownloadService = null,
        PeerPairingService? peerPairingService = null,
        PeerTrackResolver? peerTrackResolver = null,
        DeviceIdentity? deviceIdentity = null,
        DeviceSigningKey? signingKey = null,
        // Trailing + defaulted for the same reason as the sync stack above,
        // though for a different reason on the other side: a test that only
        // wants a MainViewModel should not have to stand up the log buffer and
        // the settings store to get one.
        LogViewModel? log = null)
    {
        Library                = library;
        _playlistControlViewModel = playlistControlViewModel;
        Volume                 = volume;
        NowPlaying             = nowPlaying;
        Log                    = log;
        Equalizer              = equalizer;
        Rename                 = rename;
        _appSettings           = appSettings;
        _importer              = importer;
        _mainPlaylist          = mainPlaylist;
        _reachability          = reachability;
        _deviceIdentity        = deviceIdentity;
        _signingKey            = signingKey;
        DeviceNicknames        = deviceNicknameStore;
        NetworkDiscovery       = networkDiscovery;
        _appSettingsStore      = appSettingsStore;
        _busy                  = busy;
        ITunesImport           = iTunesImport;
        _logger                = logger;

        Browser = new LibraryBrowserViewModel(library, this, AppLogging.CreateTypedLogger<LibraryBrowserViewModel>(), animationClock);
        Browser.RestoreSort(
            appSettings.SortColumn ?? "TrackNumber",
            appSettings.SortColumn is null || appSettings.SortAscending,
            appSettings.SortArtistAlbumsByYear);

        // The browser owns the state; these re-raise it on this ViewModel,
        // which is what every binding actually watches.
        _subscriptions.Add<PropertyChangedEventHandler>(
            (_, e) => OnPropertyChanged(e.PropertyName),
            h => Browser.PropertyChanged += h, h => Browser.PropertyChanged -= h);

        Playlists = new PlaylistManagementViewModel(library, _sidebarItems, this);
        _subscriptions.Add<EventHandler<DeletePlaylistConfirmationEventArgs>>(
            (_, e) => DeletePlaylistConfirmationRequested?.Invoke(this, e),
            h => Playlists.DeleteConfirmationRequested += h, h => Playlists.DeleteConfirmationRequested -= h);

        OpenAppDataLocationCommand  = new RelayCommand(OpenAppDataLocation);
        RebuildDatabaseCommand      = new AsyncRelayCommand(RebuildDatabaseAsync);
        SortByColumnCommand         = new RelayCommand<string>(Browser.SortByColumn);
        OpenSettingsCommand         = new RelayCommand(() => SettingsRequested?.Invoke(this, EventArgs.Empty));
        OpenColumnSelectorCommand   = new RelayCommand(() => ColumnSelectorRequested?.Invoke(this, EventArgs.Empty));
        OpenLogWindowCommand        = new RelayCommand(() => LogWindowRequested?.Invoke(this, EventArgs.Empty));
        OpenEqualizerWindowCommand  = new RelayCommand(() => EqualizerWindowRequested?.Invoke(this, EventArgs.Empty));
        NewPlaylistCommand          = new AsyncRelayCommand(() => CreatePlaylistWithTrack(null));
        PlayOrPauseCommand          = new RelayCommand(PlayOrPauseFromCurrentView);
        NextTrackCommand            = new RelayCommand(_playlistControlViewModel.Next);
        PreviousTrackCommand        = new RelayCommand(_playlistControlViewModel.Previous);
        ToggleRepeatCommand         = new RelayCommand(_playlistControlViewModel.ToggleRepeat);
        ToggleShuffleCommand        = new RelayCommand(_playlistControlViewModel.ToggleShuffle);

        _renamePlaylistCommand = new RelayCommand(
            () => RenamePlaylistRequested?.Invoke(this, EventArgs.Empty),
            Playlists.CanRenameOrDeleteSelected);
        RenamePlaylistCommand = _renamePlaylistCommand;

        _deletePlaylistCommand = new AsyncRelayCommand(Playlists.DeleteSelectedAsync, Playlists.CanRenameOrDeleteSelected);
        DeletePlaylistCommand = _deletePlaylistCommand;

        ToggleAlbumExpandedCommand = new RelayCommand<string>(Browser.ToggleAlbumExpanded);

        Sync = new PeerSyncCoordinator(
            this, appSettings, appSettingsStore, deviceIdentityStore,
            AppLogging.CreateTypedLogger<PeerSyncCoordinator>(),
            networkDiscovery, reachability, playlistSyncService, librarySyncService,
            libraryDownloadService, peerPairingService, peerTrackResolver, trustedPeerStore, deviceIdentity, signingKey,
            Library);

        _deviceSidebar = new DeviceSidebarSection(_sidebarItems, this, deviceNicknameStore, reachability);

        // The coordinator owns the state; these re-raise it on this ViewModel,
        // which is what every binding actually watches.
        _subscriptions.Add<PropertyChangedEventHandler>(
            (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(PeerSyncCoordinator.IsSyncing):
                    OnPropertyChanged(nameof(IsSyncing));
                    _deviceSidebar.SetPairedServerSyncing(Sync.IsSyncing);
                    break;
                case nameof(PeerSyncCoordinator.LastForceSyncResult):
                    OnPropertyChanged(nameof(LastForceSyncResult));
                    break;
            }
        },
            h => Sync.PropertyChanged += h, h => Sync.PropertyChanged -= h);

        _subscriptions.Add<EventHandler>((_, _) =>
        {
            OnPropertyChanged(nameof(PairedServerFingerprint));
            OnPropertyChanged(nameof(PairedServerAlias));
            OnPropertyChanged(nameof(Availability));
            NotifyPairButtonPropertiesChanged();
            // Identity first, then the reachability glyph - SyncPairedServerRow
            // only ever touches a row that is already pinned, so pinning has to
            // happen here rather than inside it (see DeviceSidebarSection).
            if (Sync.PairedServerFingerprint is { Length: > 0 } fingerprint)
                _deviceSidebar.PinPairedServerRow(fingerprint);
            else
                _deviceSidebar.UnpinPairedServerRow();
            _deviceSidebar.SyncPairedServerRow();
        },
            h => Sync.PairingChanged += h, h => Sync.PairingChanged -= h);

        // The redeem happens off the UI thread, so the message it produces has
        // to come back onto it before a binding reads it.
        _subscriptions.Add<EventHandler<string>>((_, reason) => Dispatcher.UIThread.Post(() =>
                PairingCodeError = reason),
            h => Sync.PairingCodeRejected += h, h => Sync.PairingCodeRejected -= h);

        _subscriptions.Add<EventHandler>((_, _) =>
        {
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(BusyMessage));
        },
            h => _busy.Changed += h, h => _busy.Changed -= h);

        BuildSidebarItems();
        Browser.Repopulate();

        // Everything here runs on the UI thread, not just PopulateTracks.
        // TracksUpdated can be raised from a LibVLC decode-callback thread (see
        // Library's own _lock comment; PlaylistControlViewModel's EndReached
        // handler gets there via NotifyTrackChanged), and ScheduleContentSync's
        // continuation goes on to enumerate _sidebarItems - a plain
        // ObservableCollection the UI thread mutates through
        // AddOrUpdateDeviceSidebarItem/RemoveDeviceItem.
        _subscriptions.Add<EventHandler>((_, _) => Dispatcher.UIThread.Post(() =>
        {
            Browser.Repopulate();
            // A merge in flight means this fired because one of our own
            // syncs just merged something (see RunTrackedSync's doc comment) -
            // not a genuine local change - so don't treat it as one.
            if (!Sync.IsMergingOwnSync)
                ScheduleContentSync();
            else
                _logger.LogDebug("TracksUpdated fired mid-sync - not scheduling a resync");
        }),
            h => library.TracksUpdated += h, h => library.TracksUpdated -= h);
        // A play-count / LastPlayedAt bump used to arrive as TracksUpdated, so
        // playing a song cost a full PopulateTracks (16k row allocations, a
        // full album regroup) plus a peer sync, twice. Only two columns can
        // actually have changed, and only on one row, so re-raise exactly those
        // - and don't schedule a content sync at all: another device does not
        // need to hear about a local play count the moment it happens (the next
        // genuine library change carries it along anyway).
        _subscriptions.Add<EventHandler<TrackStatsChangedEventArgs>>(
            (_, e) => Dispatcher.UIThread.Post(() => Browser.NotifyTrackStatsChanged(e.Track)),
            h => library.TrackStatsChanged += h, h => library.TrackStatsChanged -= h);

        // Same reasoning as TracksUpdated above - PlaylistsUpdated is raised
        // from the sync path, off the UI thread.
        _subscriptions.Add<EventHandler>((_, _) => Dispatcher.UIThread.Post(() =>
        {
            Playlists.RefreshSidebarItems();
            if (!Sync.IsMergingOwnSync)
                ScheduleContentSync();
            else
                _logger.LogDebug("PlaylistsUpdated fired mid-sync - not scheduling a resync");
        }),
            h => library.PlaylistsUpdated += h, h => library.PlaylistsUpdated -= h);

        // Reachability itself is handled entirely by PairedServerReachability's
        // own DeviceDiscovered/DeviceLost subscription + this single Changed
        // handler below - these two lambdas keep only their other,
        // unrelated responsibilities (general device-list sidebar upkeep,
        // trust-change handling, sync triggering).
        if (networkDiscovery != null)
        {
            _subscriptions.Add<EventHandler<DiscoveredDevice>>((_, device) =>
        {
            Dispatcher.UIThread.Post(() => AddOrUpdateDeviceSidebarItem(device));
            Dispatcher.UIThread.Post(() =>
            {
                OnPropertyChanged(nameof(AvailableServers));
                OnPropertyChanged(nameof(HasAvailableServers));
                OnPropertyChanged(nameof(ManualServerAddresses));
            });
            Sync.HandlePeerTrustChanged(device);
            TriggerSyncIfPeerCatalogChanged(device);
            TriggerSyncIfReady(device);
        },
                h => networkDiscovery.DeviceDiscovered += h, h => networkDiscovery.DeviceDiscovered -= h);

            _subscriptions.Add<EventHandler<string>>((_, instanceName) =>
        {
            Dispatcher.UIThread.Post(() => RemoveDeviceSidebarItem(instanceName));
            Dispatcher.UIThread.Post(() =>
            {
                OnPropertyChanged(nameof(AvailableServers));
                OnPropertyChanged(nameof(HasAvailableServers));
                OnPropertyChanged(nameof(ManualServerAddresses));
            });
        },
                h => networkDiscovery.DeviceLost += h, h => networkDiscovery.DeviceLost -= h);
        }

        // The one place reachability propagates outward - see
        // PairedServerReachability's own doc comment. Fires already on the UI
        // thread.
        if (reachability != null)
            _subscriptions.Add<EventHandler>((_, _) =>
        {
            OnPropertyChanged(nameof(IsPairedServerReachable));
            OnPropertyChanged(nameof(Availability));
            OnPropertyChanged(nameof(ShowPairedServerUnreachableWarning));
            OnPropertyChanged(nameof(PairedServerRouteDescription));
            OnPropertyChanged(nameof(CanForceSync));
            // Whether the paired server can be asked for a code at all turns on
            // it being reachable right now - see CanInviteDeviceToPairedServer,
            // which resolves the server through this same service.
            OnPropertyChanged(nameof(CanInviteDeviceToPairedServer));
            _deviceSidebar.SyncPairedServerRow();
            Browser.ApplyTrackAvailability(PairedServerFingerprint, reachability.IsReachable);
            ReachabilityChanged?.Invoke(this, EventArgs.Empty);
        },
                h => reachability.Changed += h, h => reachability.Changed -= h);

        // On mobile, MainViewModel is still constructed (App.axaml.cs resolves it
        // unconditionally) but MainView - the only subscriber to
        // PlaylistConflictRequested - never is, since mobile shows MobileMainView
        // instead. Without this check, a conflict during a mobile-initiated sync
        // would await e.Resolution forever. Until mobile gets its own conflict UI,
        // fail safe by keeping the local version rather than hanging the sync.
        if (playlistSyncService != null)
            _subscriptions.Add<EventHandler<PlaylistConflictEventArgs>>((_, e) =>
        {
            if (PlaylistConflictRequested == null)
            {
                e.Resolution.TrySetResult(PlaylistConflictChoice.KeepLocal);
                return;
            }
            Dispatcher.UIThread.Post(() => PlaylistConflictRequested?.Invoke(this, e));
        },
                h => playlistSyncService.ConflictDetected += h, h => playlistSyncService.ConflictDetected -= h);

        // A paired Server no longer trusting us surfaces here every time this
        // device is in any kind of contact with it - not just while actively
        // syncing - so it's noticed on (roughly) the same timetable regardless
        // of whether the revoke happened while this device was reachable or
        // not: NetworkDiscoveryService.ResolveAliasAsync polls every known
        // peer's /info roughly every 5s independent of any sync attempt (see
        // DiscoveredDevice.TrustsUs and Flower.Server's DiscoveryEndpoints
        // trustsCaller field), and DeviceDiscovered re-fires whenever that (or
        // anything else in a device's /info) changes - handled by
        // HandlePeerTrustChanged below. PlaylistSyncService/LibrarySyncService's
        // PeerTrustRejected is the same information arriving slightly earlier,
        // opportunistically, off the 403 an actual gated sync request gets in
        // the meantime - both converge on the exact same check, so a revoke is
        // never missed just because the two devices happened not to be
        // "connecting" in one specific sense of the word at the right moment.
        void HandlePeerTrustRejected(object? _, PeerTrustRejectedEventArgs e) =>
            Sync.HandleTrustRevoked(e.Alias, e.Fingerprint);
        if (playlistSyncService != null)
            _subscriptions.Add<EventHandler<PeerTrustRejectedEventArgs>>(HandlePeerTrustRejected,
                h => playlistSyncService.PeerTrustRejected += h, h => playlistSyncService.PeerTrustRejected -= h);
        if (librarySyncService != null)
            _subscriptions.Add<EventHandler<PeerTrustRejectedEventArgs>>(HandlePeerTrustRejected,
                h => librarySyncService.PeerTrustRejected += h, h => librarySyncService.PeerTrustRejected -= h);
        _subscriptions.Add<PropertyChangedEventHandler>((_, e) =>
        {
            if (e.PropertyName == nameof(PlaylistControlViewModel.SelectedTrack))
                OnPropertyChanged(nameof(SelectedTrack));
            if (e.PropertyName == nameof(PlaylistControlViewModel.CurrentlyPlayingTrack))
            {
                OnPropertyChanged(nameof(CurrentlyPlayingTrack));
                Browser.UpdatePlayingIndicators();
            }
            if (e.PropertyName == nameof(PlaylistControlViewModel.IsRepeatEnabled))
                OnPropertyChanged(nameof(IsRepeatEnabled));
            if (e.PropertyName == nameof(PlaylistControlViewModel.IsShuffleEnabled))
                OnPropertyChanged(nameof(IsShuffleEnabled));
        },
            h => _playlistControlViewModel.PropertyChanged += h, h => _playlistControlViewModel.PropertyChanged -= h);
    }

    // Every event this class attaches to in its constructor, paired with its
    // teardown - see SubscriptionBag, and docs/ARCHITECTURE-REVIEW.md Tier 2.3.
    private readonly SubscriptionBag _subscriptions = new();

    // Registered in the container as a singleton, so in the app this runs at
    // process exit and never matters. It exists for the case that used to be
    // impossible: constructing a MainViewModel, using it, and letting go of it
    // without leaving sixteen handlers attached to services that outlive it.
    public void Dispose()
    {
        _subscriptions.Dispose();
        Sync.Dispose();
    }

    // ── Playback ─────────────────────────────────────────────────────────────

    // Plays a specific track the user picked from whatever's currently displayed
    // (Songs, an album/artist drill-down, or a playlist), establishing that view's
    // current order as the Next/Previous queue - see PlaylistControlViewModel's
    // SetCurrentPlaylist. Unconditional: activating a row is always "start a new
    // queue from here," regardless of what was playing before.
    public void PlayTrack(Track track) => PlayTrack(track, -1);

    // queueIndex is the row's position in the list the user activated it from,
    // which SyncPlayQueueToCurrentView has just made the queue - Rows and
    // DisplayedTracks are both built from the same TrackListBuilder plan, in
    // the same order, so a row index is a queue index. Passing it through is
    // what lets the second of two identical playlist entries play as the second
    // entry rather than re-anchoring onto the first (ARCHITECTURE-REVIEW 0.2).
    public void PlayTrack(Track track, int queueIndex)
    {
        SyncPlayQueueToCurrentView();
        _playlistControlViewModel.Play(track, queueIndex);
    }

    // Double-click on an album tile in the Albums/Recently Added grid (see
    // MainView.axaml.cs's AlbumGrid_PointerPressed) - queues the whole album
    // in track order and starts playing from the first track, and makes sure
    // it ends up expanded rather than toggling closed (unlike a plain click's
    // ToggleAlbumExpandedCommand).
    public void PlayAlbum(string albumName)
    {
        var tracks = Browser.BuildExpandedAlbumTracks(albumName);
        if (tracks.Count == 0)
            return;

        // Unlike a plain click's ToggleAlbumExpandedCommand, this always ends
        // expanded rather than toggling closed.
        if (Browser.ExpandedAlbumName != albumName)
            Browser.ToggleAlbumExpanded(albumName);

        _playlistControlViewModel.SetCurrentPlaylist(new Playlist("Now Playing Queue", new List<Track>(tracks)));
        _playlistControlViewModel.Play(tracks[0], 0);
    }

    // Enter/double-click on an individual track row inside the inline-
    // expanded album (AlbumGridRowControl), as opposed to double-clicking
    // the album tile itself (PlayAlbum above). Deliberately does NOT go
    // through PlayTrack/SyncPlayQueueToCurrentView: that sources the queue
    // from DisplayedTracks, which for the Albums/Recently Added grid
    // is driven by _selectedSubItems - the Ctrl/Shift multi-select used for
    // drag-to-playlist (see ExpandedAlbumName's remarks), not by which
    // album is actually expanded on screen. Left-over multi-selected tiles
    // from that gesture made the queue silently become empty, or a union of
    // tracks across every selected album - playback would run off the end
    // of the displayed album straight into a different one. Queuing
    // ExpandedAlbumTracks directly - the same list PlayAlbum uses - keeps
    // the queue matching what's actually on screen.
    public void PlayTrackInExpandedAlbum(Track track)
    {
        if (ExpandedAlbumTracks.Count == 0)
            return;

        _playlistControlViewModel.SetCurrentPlaylist(new Playlist("Now Playing Queue", new List<Track>(ExpandedAlbumTracks)));
        _playlistControlViewModel.Play(track, ExpandedAlbumTracks.IndexOf(track));
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
        {
            SyncPlayQueueToCurrentView();

            // PlaylistControlViewModel.PlayOrPause()'s own SelectedTrack-or-
            // first-track fallback used to be re-implemented here, because that
            // one went straight into IAudioManager.Play and crashed on an
            // undownloaded placeholder. Play() resolves placeholders itself now
            // (see IStreamUrlResolver), so the fallback below is enough - except
            // that this view's first track, not the queue's, is the right one to
            // fall back to.
            var trackToPlay = _playlistControlViewModel.SelectedTrack ?? DisplayedTracks.FirstOrDefault();
            if (trackToPlay != null)
            {
                _playlistControlViewModel.Play(trackToPlay);
                return;
            }
        }

        _playlistControlViewModel.PlayOrPause();
    }

    private void SyncPlayQueueToCurrentView() => SetPlayQueue(DisplayedTracks);

    // Re-anchors Next/Previous/auto-advance to a specific list of tracks.
    // Public because mobile needs the same thing over a different source: its
    // search results are a separate mirror of Rows rather than Rows itself, so
    // it cannot just call SyncPlayQueueToCurrentView above - which is what left
    // it with its own copy of this until now.
    public void SetPlayQueue(IEnumerable<Track> tracks) =>
        _playlistControlViewModel.SetCurrentPlaylist(new Playlist("Now Playing Queue", new List<Track>(tracks)));

    // ── Sidebar ───────────────────────────────────────────────────────────────

    private void BuildSidebarItems()
    {
        _sidebarItems.Clear();
        _sidebarItems.Add(new SidebarItem(SidebarItemKind.Header,        "Library"));
        _sidebarItems.Add(new SidebarItem(SidebarItemKind.RecentlyAdded, "Recently Added", MaterialIconKind.ClockPlusOutline));
        _sidebarItems.Add(new SidebarItem(SidebarItemKind.History, "History", MaterialIconKind.ClockTimeEightOutline));
        _sidebarItems.Add(new SidebarItem(SidebarItemKind.Songs,   "Songs",   MaterialIconKind.Music));
        _sidebarItems.Add(new SidebarItem(SidebarItemKind.Albums,  "Albums",  MaterialIconKind.Album));
        _sidebarItems.Add(new SidebarItem(SidebarItemKind.Artists, "Artists", MaterialIconKind.AccountMusic));

        if (Library.Playlists.Count > 0)
        {
            _sidebarItems.Add(new SidebarItem(SidebarItemKind.Header, "Playlists"));
            foreach (var pl in Library.Playlists)
                _sidebarItems.Add(new SidebarItem(SidebarItemKind.Playlist, pl.Name, MaterialIconKind.PlaylistPlay, pl));
        }

        // The paired server's row is pinned in place for the whole session
        // (see RemoveDeviceItem) instead of only existing while mDNS
        // currently has it in view like an ordinary row - added here up
        // front so it shows immediately at launch, before this
        // session's first DeviceDiscovered has had a chance to find it (or
        // even if it never does, this run). AddOrUpdateDeviceSidebarItem's
        // FindDeviceSidebarItem claims this same row once the peer actually
        // is (re)discovered, rather than creating a second one for it.
        if (_appSettings.PairedServerFingerprint is { Length: > 0 } && _appSettings.PairedServerAlias is { } pairedAlias)
        {
            _sidebarItems.Add(new SidebarItem(SidebarItemKind.Header, "Servers"));
            _sidebarItems.Add(new SidebarItem(SidebarItemKind.Device, pairedAlias, MaterialIconKind.Server)
            {
                IsPairedServer = true,
                IsReachable = false,
            });
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

    public double LastScrollOffsetY => _appSettings.LastScrollOffsetY;

    private SidebarItem? ResolveLastSidebarItem()
    {
        if (_appSettings.LastSidebarKind is not { } kindText || !Enum.TryParse<SidebarItemKind>(kindText, out var kind))
            return null;

        if (kind == SidebarItemKind.Playlist)
            return _appSettings.LastPlaylistName is { } name
                ? _sidebarItems.FirstOrDefault(i => i.Kind == SidebarItemKind.Playlist && i.Name == name)
                : null;

        // Header/Device are never a saved selection in practice (see
        // SaveLastView below, which only ever writes one of these four), but
        // guarded anyway since this reads straight back out of a JSON file a
        // user could hand-edit.
        return kind is SidebarItemKind.Songs or SidebarItemKind.Albums or SidebarItemKind.Artists or SidebarItemKind.RecentlyAdded or SidebarItemKind.History
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
        _appSettings.LastSidebarKind = _selectedSidebarItem?.Kind.ToString();
        _appSettings.LastPlaylistName = _selectedSidebarItem?.Kind == SidebarItemKind.Playlist
            ? _selectedSidebarItem.Playlist?.Name
            : null;
        _appSettings.LastScrollOffsetY = scrollOffsetY;
        _appSettingsStore?.Save(_appSettings);
    }

    // The Devices/Server sections of the sidebar - the whole row state machine
    // lives in DeviceSidebarSection, which operates over _sidebarItems and
    // reaches back here only through IDeviceSidebarHost below.
    private DeviceSidebarSection _deviceSidebar = null!;

    // internal, not private, so MainViewModelDeviceSidebarTests can drive the
    // device-row state machine through this ViewModel as it always did.
    internal void AddOrUpdateDeviceSidebarItem(DiscoveredDevice device) => _deviceSidebar.AddOrUpdate(device);

    internal void RemoveDeviceSidebarItem(string instanceName) => _deviceSidebar.Remove(instanceName);

    // Called by every place a device's nickname can change (the sidebar's own
    // "Rename Device" context menu, TrustedDevicesView's pencil-icon rename).
    public void RefreshDeviceDisplayNames() => _deviceSidebar.RefreshDisplayNames();

    // ── IDeviceSidebarHost ────────────────────────────────────────────────

    // Where selection lands when whatever was selected disappears - shared by
    // IDeviceSidebarHost and IPlaylistManagementHost, which ask the same
    // question.
    private SidebarItem? DefaultSelection =>
        _sidebarItems.FirstOrDefault(i => i.Kind == SidebarItemKind.Songs);

    SidebarItem? IDeviceSidebarHost.DefaultSelection => DefaultSelection;
    SidebarItem? IPlaylistManagementHost.DefaultSelection => DefaultSelection;

    void IDeviceSidebarHost.ForgetSyncedDevice(string fingerprint) => Sync.ForgetSyncedDevice(fingerprint);

    // ── IPeerSyncHost ─────────────────────────────────────────────────────

    // The sidebar's own view of who is out there, rather than
    // NetworkDiscoveryService.KnownDevices - see IPeerSyncHost.ListedPeers.
    IReadOnlyList<DiscoveredDevice> IPeerSyncHost.ListedPeers =>
        _sidebarItems.Where(i => i.Kind == SidebarItemKind.Device && i.Device != null)
            .Select(i => i.Device!)
            .ToList();

    void IDeviceSidebarHost.DeviceRowsChanged()
    {
        OnPropertyChanged(nameof(SelectedDevice));
        // A code typed for one server must never be submitted against
        // another, so the box empties whenever the selection moves.
        PairingCode = "";
        PairingCodeError = null;
        NotifyPairButtonPropertiesChanged();
    }

    public Task<TrackDownloadResult> DownloadTrackAsync(Track track) => Sync.DownloadTrackAsync(track);

    public Task DeleteDownloadedFileAsync(Track track) => Sync.DeleteDownloadedFileAsync(track);


    // internal for MainViewModelSyncTriggerTests, which drive the discovery
    // handlers directly - reaching them through the real
    // NetworkDiscoveryService would mean standing up an mDNS backend AND an
    // HTTP /info endpoint per case, just to choose a Fingerprint.
    internal void TriggerSyncIfPeerCatalogChanged(DiscoveredDevice device) => Sync.TriggerSyncIfPeerCatalogChanged(device);

    internal void TriggerSyncIfReady(DiscoveredDevice device) => Sync.TriggerSyncIfReady(device);

    // Playlist CRUD, membership and the sidebar's Playlists section all live
    // in PlaylistManagementViewModel; these forward because MainView's context
    // menus, the Playlist main menu and MobileMainViewModel all reach them
    // through this ViewModel.
    public PlaylistManagementViewModel Playlists { get; } = null!;

    public Task CreatePlaylistWithTrack(Track? track) => Playlists.CreateWithTrack(track);

    public Task CreatePlaylistWithTracks(IEnumerable<Track> tracks) => Playlists.CreateWithTracks(tracks);

    public Task DeletePlaylistAsync(Playlist playlist) => Playlists.DeleteAsync(playlist);

    public Task AddTrackToPlaylist(Track track, Playlist playlist) => Playlists.AddTrack(track, playlist);

    public Task AddTracksToPlaylist(IEnumerable<Track> tracks, Playlist playlist) => Playlists.AddTracks(tracks, playlist);

    public Task ReorderPlaylistTrack(Playlist playlist, Track dragged, Track? insertBefore) =>
        Playlists.ReorderTrack(playlist, dragged, insertBefore);

    // ── IPlaylistManagementHost ───────────────────────────────────────────

    // A playlist currently on screen gained/lost/reordered tracks - the debounce
    // is right here, this is not a rapid-fire path.
    void IPlaylistManagementHost.PlaylistContentChanged() => ScheduleFilter();

    private void OnSidebarSelectionChanged()
    {
        Browser.CollapseExpandedAlbum();

        OnPropertyChanged(nameof(IsSubListVisible));
        OnPropertyChanged(nameof(IsShowingAlbumGrid));
        OnPropertyChanged(nameof(IsShowingRecentlyAddedGrid));
        OnPropertyChanged(nameof(IsShowingTrackList));
        OnPropertyChanged(nameof(IsShowingDeviceDetail));
        OnPropertyChanged(nameof(SelectedDevice));
        // A code typed for one server must never be submitted against
        // another, so the box empties whenever the selection moves.
        PairingCode = "";
        PairingCodeError = null;
        // Likewise for a code handed *out* for one server: it is worthless
        // against another, and leaving it on screen under a different server's
        // name says otherwise. See IssuedPairingCode. Cleared only here and not
        // in DeviceRowsChanged, which also runs on the 5s peer poll - a code
        // that vanished every few seconds could not be read out loud.
        IssuedPairingCode = null;
        IssuedPairingCodeError = null;
        ServerSettingsError = null;
        NotifyPairButtonPropertiesChanged();
        RefreshSelectedServerSettings();
        // Recently Added carries its own independent sort state (see SortColumn),
        // so switching to/from it changes what these computed properties report.
        Browser.NotifySortChanged();
        Browser.RebuildSubListItems();
        _renamePlaylistCommand?.NotifyCanExecuteChanged();
        _deletePlaylistCommand?.NotifyCanExecuteChanged();

        var initial = Browser.InitialSubItemForCurrentView();
        Browser.ApplySubItemSelection(initial != null ? new[] { initial } : Array.Empty<string>(), immediate: true);
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
        using var _ = _busy.BeginScope("Rebuilding library…");
        var libraryPaths = _appSettings.LibraryPaths;
        var freshTracks = await _importer.ImportAsync(libraryPaths);
        _mainPlaylist.ReplaceAll(freshTracks);
        Library.UpdateTracks(freshTracks);
    }

    // Persists the path list only - deliberately doesn't also rescan, so
    // SettingsWindow can close its dialog immediately on OK instead of
    // blocking on however long the (potentially large) library scan takes;
    // it calls RescanLibraryAsync separately, unawaited, after closing.
    public async Task SaveLibraryPathsAsync(List<string> paths)
    {
        var added = paths.Except(_appSettings.LibraryPaths, StringComparer.OrdinalIgnoreCase).ToList();
        var removed = _appSettings.LibraryPaths.Except(paths, StringComparer.OrdinalIgnoreCase).ToList();
        if (added.Count > 0 || removed.Count > 0)
            _logger.LogInformation("Library folders changed - added: [{Added}], removed: [{Removed}]",
                string.Join(", ", added), string.Join(", ", removed));

        _appSettings.LibraryPaths = paths;
        await (_appSettingsStore?.SaveAsync(_appSettings) ?? Task.CompletedTask);
    }

    // Mobile has no library-paths UI to rescan as a side effect of (desktop's
    // SettingsWindow OK button) — it needs to trigger a rescan directly,
    // e.g. after granting a previously-denied Android media permission.
    public Task RescanLibraryAsync() => RebuildDatabaseAsync();

    // ── Go to currently playing track (Cmd/Ctrl+L) ───────────────────────────

    public async Task GoToCurrentlyPlayingTrackAsync()
    {
        var track = CurrentlyPlayingTrack;
        if (track == null)
            return;

        // By Id: a streamed placeholder is playing as a transient copy carrying
        // a stream URL (Track.Clone keeps the Id - see
        // PlaylistControlViewModel.ResolveForPlaybackAsync), so its Path matches
        // nothing in the library, while every *undownloaded* placeholder's null
        // Path matches every other one.
        if (DisplayedTracks.Any(t => t.Id == track.Id))
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
                when _selectedSidebarItem.Playlist?.Tracks.Any(t => t.Id == track.Id) != true:
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

        if (!await RebuildRowsImmediatelyAsync())
            return; // Superseded by a newer filter/navigation change - let that one win.

        NavigateToTrackRequested?.Invoke(this, track);
    }
}
