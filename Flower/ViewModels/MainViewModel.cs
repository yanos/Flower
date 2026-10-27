using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

using Avalonia.Media;
using Avalonia.Threading;

using CommunityToolkit.Mvvm.Input;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly FlowerFolder? _flowerFolder;
    private MainPlaylist?      _mainPlaylist;
    private DeviceIdentity? _deviceIdentity;

    // Kept, not just consumed in the constructor: "Server Settings..." signs a
    // request against the selected server with it (see
    // OpenSelectedServerSettingsAsync). Null on Flower.Web/WASM, same as
    // _deviceIdentity - see the constructor's note on the sync-stack parameters.
    private readonly DeviceSigningKey? _signingKey;

    // Kept for ArtEditorFor: changing the cover of a song that lives on the
    // paired server means finding that server and signing a request to it.
    private readonly PeerTrackResolver? _peerTrackResolver;
    private readonly IPeerCredentials? _peerCredentials;
    private PairedServerReachability? _reachability;

    // The paired server's icon (ServerIcon), shown on its sidebar row and the
    // phone's server row. Null on a head with no peer stack - the browser,
    // whose one server is the page it is on.
    public PairedServerIcon? PairedIcon { get; }

    // The same, for the phone's server row to bind: one of the two, or
    // neither.
    public IImage? PairedServerIconImage => PairedIcon?.Image;
    public string PairedServerIconEmoji => PairedIcon?.Image == null ? PairedIcon?.Emoji ?? "" : "";
    public bool HasPairedServerIconEmoji => PairedServerIconEmoji.Length > 0;
    public bool HasPairedServerIconImage => PairedServerIconImage != null;
    private AppSettingsStore? _appSettingsStore;

    // The P2P sync coordinator - when to sync, with whom, and the
    // pairing/trust handshake around it. Everything below that touches sync is
    // a forwarder onto it, kept here because XAML (MainView, ServerPickerView,
    // mobile's SettingsView) and MobileMainViewModel bind to this ViewModel.
    public PeerSyncCoordinator Sync { get; } = null!;

    public bool IsSyncing => Sync.IsSyncing;

    // Changing a cover, for the desktop Track Info window. Here because this
    // holds what that needs: the library, and the paired server and the
    // credentials to reach it.
    public AlbumArtEditor ArtEditorFor(IReadOnlyList<Track> tracks, ILogger logger) =>
        new(tracks, Library, _appSettings, _peerTrackResolver, _peerCredentials, logger);

    // What Track Info shows and edits, on either head - see TrackInfoViewModel.
    // Built here for the same reason as ArtEditorFor: this holds who may edit
    // what and which server a song comes from.
    public TrackInfoViewModel TrackInfo() =>
        new(Library, CanEditTagsOf, PairedServerSourceName, LoggerFactory.CreateLogger<TrackInfoViewModel>());

    // What decides a song or album menu's entries for these songs - see
    // ActionMenus. removal is null on a head with no LibraryRemovalService,
    // which then offers no Remove from Library.
    public MenuFacts MenuFactsFor(IReadOnlyList<Track> tracks, LibraryRemovalService? removal) =>
        new(tracks.Count(Availability.IsDownloadable),
            Downloads.IsBulkDownloading,
            LocalFileDeletion.LocalFiles(tracks).Count,
            removal?.Removable(tracks).Count ?? 0);

    // The paired server's name when every one of these tracks is one of its
    // songs rather than a file here; null otherwise. Track Info names it where
    // a file path would go.
    public string? PairedServerSourceName(IReadOnlyList<Track> tracks) =>
        AlbumArtEditor.PairedServerSourceName(tracks, _appSettings);

    // Whether Track Info may edit this song - see SyncRolePolicy.MayEditSong
    // for the rule. A song of this device's own, always; a song of the paired
    // server's, only on a device that server made an administrator, and then
    // whether or not there is a file of it here: the edit is sent to the
    // server (LibraryMirrorService.PushTagEditsAsync), written into its file,
    // and comes back to every device from there. Track Info used to accept an
    // edit to a song with no file and silently drop it, and to let a guest
    // retitle a downloaded copy that the next sync then put back.
    public bool CanEditTagsOf(Track track) =>
        SyncRolePolicy.MayEditSong(
            track, _appSettings.PairedServerFingerprint,
            _reachability?.PairedServerDevice?.WeAreAdmin ?? _appSettings.PairedServerGrantsAdmin);

    // The pairing as last heard, for Settings to say which Music.app imports
    // this device may run (SyncRolePolicy.MayImportFromMusicApp).
    public bool AdministersPairedServer => _appSettings.PairedServerGrantsAdmin;
    public bool PairedServerImportsMusicAppPlays => _appSettings.PairedServerImportsMusicAppPlays;
    public bool MayImportFromMusicApp => SyncRolePolicy.MayImportFromMusicApp(_appSettings);
    public bool MayImportPlayCountsFromMusicApp => SyncRolePolicy.MayImportPlayCountsFromMusicApp(_appSettings);
    public bool MayAddSongsFromMusicApp => SyncRolePolicy.MayAddSongsFromMusicApp(_appSettings);

    // Whether this administrator's Mac takes the songs in its Music.app
    // library in, and sends the server the ones it lacks - see
    // AppSettings.AddSongsFromMusicApp. Persist-immediately, like the two
    // switches below, and the folder is scanned (or let go of) at once.
    public bool AddSongsFromMusicApp
    {
        get => _appSettings.AddSongsFromMusicApp;
        set
        {
            if (_appSettings.AddSongsFromMusicApp == value)
                return;
            _logger.LogInformation("AddSongsFromMusicApp changed {Old} -> {New}", _appSettings.AddSongsFromMusicApp, value);
            _appSettings.AddSongsFromMusicApp = value;
            OnPropertyChanged();
            SaveSettings();
            FollowScanFolders();
        }
    }

    public void ScheduleContentSync() => Sync.ScheduleContentSync();

    public ICommand? OpenAppDataLocationCommand  { get; private set; }
    public ICommand? RebuildDatabaseCommand      { get; private set; }
    public ICommand? SortByColumnCommand         { get; private set; }
    public ICommand? OpenSettingsCommand         { get; private set; }
    public ICommand? OpenColumnSelectorCommand   { get; private set; }
    public ICommand? TogglePlaybackControlsPlacementCommand { get; private set; }
    public ICommand? OpenLogWindowCommand        { get; private set; }
    public ICommand? OpenEqualizerWindowCommand  { get; private set; }
    public ICommand? NewPlaylistCommand          { get; private set; }
    public ICommand? NewSmartPlaylistCommand     { get; private set; }
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
    public ICommand? PlayViewCommand             { get; private set; }
    public ICommand? ShuffleViewCommand          { get; private set; }
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

    // The view is asked to put the rule editor on screen; nothing here knows
    // what a window is. IsNew tells it that cancelling should also undo the
    // playlist that was created to hold the rules - see
    // SmartPlaylistEditorViewModel.Cancel.
    public event EventHandler<SmartPlaylistEditorEventArgs>? SmartPlaylistEditorRequested;

    // Forwards PairedServerReachability.Changed - see the constructor's own
    // subscription to it. Lets MobileMainViewModel (Search.SongResults, a row
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
            // Logged because of a user report of this flag silently flipping
            // off without the checkbox ("Settings > Library" is its only UI)
            // being touched, which turned up nothing conclusive in the code -
            // a timestamped trail if it happens again. This used to append
            // Environment.StackTrace to catch a programmatic caller; that never
            // caught anything and put a full stack trace in the log on every
            // toggle, so it is gone. The setter is the only writer, so the
            // timestamp and the old/new pair are the part that carries the
            // information.
            _logger.LogInformation("SyncPlayCountFromITunes changed {Old} -> {New}", _appSettings.SyncPlayCountFromITunes, value);
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
            _logger.LogInformation("SyncDateAddedFromITunes changed {Old} -> {New}", _appSettings.SyncDateAddedFromITunes, value);
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

    // Whether this device uploads its own songs to its paired server over a
    // connection that is not the server's local network - off by default. See
    // AppSettings.UploadWhenAwayFromHome. Persist-immediately, same as above.
    public bool UploadWhenAwayFromHome
    {
        get => _appSettings.UploadWhenAwayFromHome;
        set
        {
            if (_appSettings.UploadWhenAwayFromHome == value)
                return;
            _appSettings.UploadWhenAwayFromHome = value;
            OnPropertyChanged();
            SaveSettings();

            // Turning it on is a reason to go and look: the songs that were
            // waiting for home can go now.
            if (value)
                ScheduleContentSync();
        }
    }

    public string? PairedServerFingerprint => Sync.PairedServerFingerprint;
    public string? PairedServerAlias       => Sync.PairedServerAlias;

    // Shown on the paired server's own detail page so it can be compared
    // against what that server displays about itself - see
    // PeerSyncCoordinator.PairedServerPinnedFingerprint for why it is the
    // pinned value and not the announced one. Only meaningful for the server
    // this device is paired with, so it is blank on every other row.
    public string? SelectedDevicePinnedFingerprint =>
        IsSelectedDevicePaired ? Sync.PairedServerPinnedFingerprint : null;

    public bool HasSelectedDevicePinnedFingerprint => !string.IsNullOrEmpty(SelectedDevicePinnedFingerprint);

    public IEnumerable<DiscoveredDevice> AvailableServers => Sync.AvailableServers;

    // Whether that list has anything in it, so mobile's Settings sheet can say
    // "none found yet" instead of leaving an empty gap under a heading - a
    // phone that has never paired has no other clue about whether discovery is
    // working at all.
    public bool HasAvailableServers => AvailableServers.Any();

    public void PairWithServer(DiscoveredDevice device, string pairingCode) =>
        Sync.PairWithServer(device, pairingCode);

    public void UnpairServer() => Sync.UnpairServer();

    // What unpairing costs, said the same way by all three places that ask
    // before doing it: the server's detail page, Settings' Devices tab, and
    // the phone's Settings sheet. What it says is PeerSyncCoordinator.
    // UnpairServer's behaviour: a downloaded file is this device's own and
    // stays, a placeholder was only ever a promise the server would stream it
    // and goes (see Library.RemoveTracksFromOrigin).
    public static string UnpairConsequences(string alias) =>
        $"Songs you downloaded from \"{alias}\" stay in your library. The rest of its songs will be removed from this device, since nothing can play them once it stops being paired. "
        + "To get them back, pair again with a new code from the server.";

    public bool IsPairedServerReachable => Sync.IsPairedServerReachable;

    // The two above as one bindable value, for the views that hand "what
    // counts as playable right now" down to rows they build themselves - see
    // AlbumGridView.Availability, which pushes it into an expanded album's
    // track rows. Everything else in the app gets availability already
    // computed onto its rows/tiles (TrackAvailability.Apply) and has no need
    // of this.
    public TrackAvailabilityContext Availability => new(StreamingServerFingerprint, IsStreamingServerReachable);

    // The server a streamed track plays from, and whether it can right now -
    // what every row's and tile's dimming is worked out against. The paired
    // server everywhere but a browser tab, which pairs with nothing through
    // PeerSyncCoordinator and so has no paired server or reachability at all:
    // its server is the origin it was loaded from, and asked through those
    // two instead, every track in it read as a placeholder with nobody to
    // stream it and the whole library came up greyed out. Counted as
    // reachable for as long as the tab is open - a tab whose origin has gone
    // cannot do anything else either, and has no second address to wait on.
    private string? StreamingServerFingerprint => BrowserOriginFingerprint ?? PairedServerFingerprint;
    private bool IsStreamingServerReachable => BrowserOriginFingerprint != null || IsPairedServerReachable;

    string? ILibraryBrowseHost.PairedServerFingerprint => StreamingServerFingerprint;
    bool ILibraryBrowseHost.IsPairedServerReachable => IsStreamingServerReachable;

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

    public Task<DiscoveredDevice?> AddManualServerAsync(string address) => Sync.AddManualServerAsync(address);

    public void RemoveManualServer(string address) => Sync.RemoveManualServer(address);

    public bool CanForceSync => Sync.CanForceSync;

    public string? LastForceSyncResult => Sync.LastForceSyncResult;

    public string? UnwrittenWarning => Sync.UnwrittenWarning;

    // The phone shows UnwrittenWarning on its server card; desktop puts it
    // in the banner the browser uses for the same news from the server's side
    // (ServerLibraryWriteWarning). Only when the text itself changes, so a
    // dismissed banner stays dismissed until the count does, and a browser
    // tab - which never has any - keeps the server's warning it was given.
    private string? _lastUnwrittenWarning;

    private void OnUnwrittenChanged()
    {
        var warning = UnwrittenWarning;
        if (warning == _lastUnwrittenWarning)
            return;

        _lastUnwrittenWarning = warning;
        OnPropertyChanged(nameof(UnwrittenWarning));
        ServerLibraryWriteWarning = warning;
    }

    // When the paired server last successfully synced - see
    // PeerSyncCoordinator.LastSyncedAt.
    public DateTimeOffset? LastSyncedAt => Sync.LastSyncedAt;

    // Plain English rather than a timestamp: the question this answers is "is
    // what I'm looking at current?", and "8 min ago" answers it without the
    // reader subtracting anything. Falls back to a date once it is old enough
    // that the exact gap has stopped mattering. Null before a pairing's first
    // successful sync - which is exactly while the screen is still saying
    // "Waiting for server...". Shown on both settings screens (desktop's
    // ServerPickerView row, mobile's SettingsView).
    public string? LastSyncedDisplay
    {
        get
        {
            if (LastSyncedAt is not { } at)
                return null;

            var ago = DateTimeOffset.Now - at;
            return ago switch
            {
                { TotalSeconds: < 60 } => "Synced just now",
                { TotalMinutes: < 60 } => $"Synced {(int)ago.TotalMinutes} min ago",
                { TotalHours: < 24 } => $"Synced {(int)ago.TotalHours} hour{((int)ago.TotalHours == 1 ? "" : "s")} ago",
                { TotalDays: < 7 } => $"Synced {(int)ago.TotalDays} day{((int)ago.TotalDays == 1 ? "" : "s")} ago",
                _ => $"Synced {at.LocalDateTime:d}",
            };
        }
    }

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
            OnPropertyChanged();
            AppTheme.Apply(value);
        }
    }

    // Settings' Playback Controls picker and View > Playback Controls at
    // Bottom - see Flower.Persistence.PlaybackControlsPlacement. MainView
    // binds PlaybackControlsAtBottom to move its player bar to the window's
    // foot; the menu item binds it as its check.
    public PlaybackControlsPlacement PlaybackControlsPlacement
    {
        get => _appSettings.PlaybackControlsPlacement;
        set
        {
            if (_appSettings.PlaybackControlsPlacement == value)
                return;
            _appSettings.PlaybackControlsPlacement = value;
            SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(PlaybackControlsAtBottom));
        }
    }

    // Home's three settings - see AppSettings.HomeContinuePlayingLimit. Set
    // from Settings on either head; applied to the shelves at once, and saved.
    public int HomeContinuePlayingLimit
    {
        get => _appSettings.HomeContinuePlayingLimit;
        set => SetHomeSetting(value, v => _appSettings.HomeContinuePlayingLimit = v, v => Home.ContinuePlayingLimit = v,
            () => Home.ContinuePlayingLimit);
    }

    public int HomeRecentlyPlayedLimit
    {
        get => _appSettings.HomeRecentlyPlayedLimit;
        set => SetHomeSetting(value, v => _appSettings.HomeRecentlyPlayedLimit = v, v => Home.RecentlyPlayedLimit = v,
            () => Home.RecentlyPlayedLimit);
    }

    public int HomeRecentlyAddedMonths
    {
        get => _appSettings.HomeRecentlyAddedMonths;
        set => SetHomeSetting(value, v => _appSettings.HomeRecentlyAddedMonths = v, v => Home.RecentlyAddedMonths = v,
            () => Home.RecentlyAddedMonths);
    }

    // The shelves as Settings lists them on either head, in Home's order,
    // each with its amount - dragged by a handle to move one (MoveHomeShelf).
    public ObservableCollection<HomeShelfSettingRow> HomeShelfSettings { get; } = new();

    public void MoveHomeShelf(HomeShelf shelf, int index)
    {
        var order = HomeShelfOrder.Move(Home.ShelfOrder, shelf, index);
        if (order.SequenceEqual(Home.ShelfOrder))
            return;

        Home.ShelfOrder = order;
        _appSettings.HomeShelfOrder = order.Select(s => s.ToString()).ToList();
        SaveSettings();

        var from = HomeShelfSettings.ToList().FindIndex(row => row.Shelf == shelf);
        HomeShelfSettings.Move(from, order.ToList().IndexOf(shelf));
    }

    // Home clamps; what it kept is what gets saved, so a value typed past the
    // ceiling is stored as the ceiling rather than as what was typed.
    private void SetHomeSetting(int value, Action<int> store, Action<int> apply, Func<int> applied,
        [System.Runtime.CompilerServices.CallerMemberName] string? property = null)
    {
        apply(value);
        store(applied());
        SaveSettings();
        OnPropertyChanged(property);
    }

    // The desktop's page filter, on Home, narrows the shelves. Only while Home
    // is the page: the phone shares this view model, and sets Home's filter
    // from its own pull-down box instead (MobileMainViewModel.ApplyScreenFilter),
    // which a word typed into a track list's filter must not overwrite.
    private void SyncHomeFilter()
    {
        if (IsShowingHome)
            Home.Filter = FilterText;
    }

    public bool PlaybackControlsAtBottom => PlaybackControlsPlacement == PlaybackControlsPlacement.Bottom;

    // Both delegate to ITunesImportCoordinator, which owns the cooldown and
    // the apply/notify/save sequence - kept here as forwarders because
    // App.axaml.cs's startup rescan and SettingsWindow both reach them through
    // this ViewModel.
    public Task SyncITunesPlayCountAsync() => ITunesImport.SyncPlayCountAsync();
    public Task SyncITunesDateAddedAsync() => ITunesImport.SyncDateAddedAsync();

    // ── Child ViewModels the player bar binds to ──────────────────────────────

    // Exposed so MainView.axaml can hand each control its DataContext
    // declaratively ({Binding PlaybackControls} and friends) instead of the
    // control reaching into Ioc.Default for its own ViewModel from its
    // constructor - see docs/ARCHITECTURE-REVIEW.md Tier 2.3. Same forwarding
    // face 4.2 gave the collaborators split out of this class: the container
    // is consulted once, here, and everything below flows down the visual tree.
    public PlaylistControlViewModel PlaybackControls => _playlistControlViewModel;
    public VolumeControlViewModel Volume { get; }
    public OutputDeviceControlViewModel OutputDevice { get; }
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

    // ── Recent searches ───────────────────────────────────────────────

    // How many are kept. More than the dropdown under the search box shows
    // at once, so narrowing it by typing still has older ones to find.
    public const int MaxRecentSearches = 20;

    public IReadOnlyList<string> RecentSearches => _appSettings.RecentSearches;

    // Moves a query to the front, adding it if it is new. Matched ignoring
    // case, so "Radiohead" and "radiohead" are one entry, spelled the latest way.
    public void RememberSearch(string? query)
    {
        var trimmed = query?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return;

        var recent = _appSettings.RecentSearches;
        if (recent.Count > 0 && recent[0] == trimmed)
            return;
        recent.RemoveAll(q => string.Equals(q, trimmed, StringComparison.OrdinalIgnoreCase));
        recent.Insert(0, trimmed);
        if (recent.Count > MaxRecentSearches)
            recent.RemoveRange(MaxRecentSearches, recent.Count - MaxRecentSearches);
        SaveSettings();
        OnPropertyChanged(nameof(RecentSearches));
    }

    public void ClearRecentSearches()
    {
        if (_appSettings.RecentSearches.Count == 0)
            return;
        _appSettings.RecentSearches.Clear();
        SaveSettings();
        OnPropertyChanged(nameof(RecentSearches));
    }

    // ── Library browsing ──────────────────────────────────────────────

    // Rows, the search filter, the three sort states, the tile grids and the
    // Artists sub-list all live in LibraryBrowserViewModel; the members below
    // forward onto it because MainView.axaml, MusicListView and
    // MobileMainViewModel all bind through this ViewModel.
    public LibraryBrowserViewModel Browser { get; } = null!;

    // The Home screen's shelves - Continue Playing, Recently Played, Recently
    // Added. Shared with the phone, whose Home tab binds the same instance.
    public HomeViewModel Home { get; } = null!;

    // What is playing and what plays after it, as rows - the desktop's Queue
    // sidebar row and the phone's Queue tab draw the same instance.
    public QueueViewModel Queue { get; } = null!;

    public ObservableCollection<TrackRowViewModel> Rows => Browser.Rows;
    public IReadOnlyList<Track> DisplayedTracks         => Browser.DisplayedTracks;

    public string? FilterText
    {
        get => Browser.FilterText;
        set => Browser.FilterText = value;
    }

    // The Artists column's box, over the names alone - see
    // LibraryBrowserViewModel.ArtistFilterText.
    public string? ArtistFilterText
    {
        get => Browser.ArtistFilterText;
        set => Browser.ArtistFilterText = value;
    }

    public string SortColumn   => Browser.SortColumn;
    public bool   SortAscending => Browser.SortAscending;

    public bool SortArtistAlbumsByYear
    {
        get => Browser.SortArtistAlbumsByYear;
        set => Browser.SortArtistAlbumsByYear = value;
    }

    public ObservableCollection<AlbumTileViewModel> AlbumGridTiles         => Browser.AlbumGridTiles;

    public AlbumTileKey? ExpandedAlbumKey                 => Browser.ExpandedAlbumKey;
    public string? ExpandedAlbumName                     => Browser.ExpandedAlbumName;
    public ObservableCollection<Track> ExpandedAlbumTracks => Browser.ExpandedAlbumTracks;

    public ObservableCollection<string> SubListItems => Browser.SubListItems;

    // What the desktop Artists column lists: SubListItems narrowed by the
    // filter - see LibraryBrowserViewModel.VisibleSubListItems.
    public ObservableCollection<string> VisibleSubListItems => Browser.VisibleSubListItems;

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

    // ── Page header (desktop) ─────────────────────────────────────────────

    // The desktop Artists page, for the one artist picked - see
    // ArtistPageViewModel.
    public ArtistPageViewModel ArtistPage { get; } = new();

    private void RefreshArtistPage(bool force = false) =>
        ArtistPage.Show(
            _selectedSidebarItem?.Kind == SidebarItemKind.Artists && SelectedSubItems.Count == 1
                ? SelectedSubItems.First()
                : null,
            Library.Tracks, FilterText, Availability, CurrentlyPlayingTrack?.Id, SortOf(BrowseSortScreen.ArtistAlbums), force);

    // The band above a list on desktop: the page's name in the display serif,
    // how much is in it, and the filter box. Over every list and over Home -
    // not over a server's page, which is about the server, nor over Artists,
    // whose column of names heads itself and whose artist has a header of
    // their own.
    public bool ShowsPageHeader => _selectedSidebarItem?.Kind is
        SidebarItemKind.Home or SidebarItemKind.Queue or SidebarItemKind.Songs or SidebarItemKind.Albums
        or SidebarItemKind.History or SidebarItemKind.Playlist;

    // Home's name stands alone; its shelves say what they hold.
    public bool ShowsPageSubtitle => !IsShowingHome;

    // Play and Shuffle in the header, over the pages that are a list of songs.
    public bool ShowsPagePlay => _selectedSidebarItem?.Kind is
        SidebarItemKind.Songs or SidebarItemKind.History or SidebarItemKind.Playlist;

    // Not over the queue, as on the phone: it is in the order it plays in,
    // and a filtered one would hide what is next.
    public bool ShowsPageFilter => !IsShowingQueue;

    // The Sort button beside it, over the two pages whose order is a choice
    // (BrowseSort): the Albums grid, and the Artists page - its column of
    // names and the picked artist's albums. Songs sorts by its column headers.
    public bool ShowsSortButton => IsShowingAlbumGrid || IsShowingArtistPage;

    public string? PageTitle => _selectedSidebarItem?.Name;

    // What the page holds once filtered: albums over the grid, songs
    // anywhere else. Not over Home (ShowsPageSubtitle) or Artists, whose
    // column of names counts them itself.
    public string PageSubtitle => _selectedSidebarItem?.Kind switch
    {
        SidebarItemKind.Albums => CountOf(AlbumGridTiles.Count, "album"),
        // How long the queue is, and - when what plays next is not simply the
        // next row down - why not.
        SidebarItemKind.Queue => Queue.Caption is { } caption
            ? $"{CountOf(Queue.UpcomingCount, "song")} · {caption}"
            : CountOf(Queue.UpcomingCount, "song"),
        _ => CountOf(DisplayedTracks.Count, "song"),
    };

    private static string CountOf(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count:N0} {noun}s";

    // The order an album or artist screen is in - picked from a phone
    // screen's header menu or the desktop's Sort button (BrowseSort). Read
    // through to settings every time rather than cached: it is asked on a
    // rebuild and on a menu opening, neither of which is a hot path.
    public (BrowseSortOrder Order, bool Ascending) SortOf(BrowseSortScreen screen) =>
        BrowseSort.Parse(_appSettings.ScreenSorts.GetValueOrDefault(screen.ToString()), screen);

    // Picks an order for a screen - picked again, it turns round - and puts
    // the desktop's own screens in it. The phone rebuilds its own.
    public void ChooseSort(BrowseSortScreen screen, BrowseSortOrder order)
    {
        var (picked, ascending) = BrowseSort.Pick(SortOf(screen), order);
        _appSettings.ScreenSorts[screen.ToString()] = BrowseSort.Format(picked, ascending);
        SaveSettings();

        switch (screen)
        {
            case BrowseSortScreen.Albums:
                Browser.ResortAlbumGrid();
                OnPropertyChanged(nameof(AlbumGridIsAlphabetical));
                OnPropertyChanged(nameof(AlbumGridRunsZToA));
                break;
            case BrowseSortScreen.Artists:
                Browser.ResortArtists();
                OnPropertyChanged(nameof(ArtistsAreAlphabetical));
                OnPropertyChanged(nameof(ArtistsRunZToA));
                break;
            case BrowseSortScreen.ArtistAlbums:
                RefreshArtistPage(force: true);
                break;
        }
    }

    // Whether the album grid and the artists are sorted on something with a
    // letter, and so whether their index bar can be letters (ScrollIndexBar.
    // ShowsLetters) - on both heads. Any other order - a year, a date, a
    // count - has no letter to jump to, and the bar is dots, a scrubber over
    // the whole scroll. The ZToA pair turns the bar round with a reversed
    // sort (ScrollIndexBar.LettersDescending).
    public bool AlbumGridIsAlphabetical =>
        SortOf(BrowseSortScreen.Albums).Order is BrowseSortOrder.Name or BrowseSortOrder.Artist;

    public bool AlbumGridRunsZToA => AlbumGridIsAlphabetical && !SortOf(BrowseSortScreen.Albums).Ascending;

    public bool ArtistsAreAlphabetical =>
        SortOf(BrowseSortScreen.Artists).Order == BrowseSortOrder.Name;

    public bool ArtistsRunZToA => ArtistsAreAlphabetical && !SortOf(BrowseSortScreen.Artists).Ascending;

    // What an album on the grid is filed under on the bar: its artist when the
    // grid is sorted by artist, its own name otherwise.
    public string? AlbumGridLetterText(AlbumTileViewModel tile) =>
        SortOf(BrowseSortScreen.Albums).Order == BrowseSortOrder.Artist ? tile.Artist : tile.Name;

    (BrowseSortOrder Order, bool Ascending) ILibraryBrowseHost.SortOf(BrowseSortScreen screen) => SortOf(screen);

    // The tabs a phone's bar shows, in order - null when never chosen. See
    // MobileMainViewModel's "Tab bar".
    public IReadOnlyList<string>? MobileTabsSetting => _appSettings.MobileTabs;

    public void PersistMobileTabs(IEnumerable<string> tabs)
    {
        _appSettings.MobileTabs = tabs.ToList();
        SaveSettings();
    }

    public bool MobileTabLabelsSetting => _appSettings.MobileTabLabels;

    public void PersistMobileTabLabels(bool value)
    {
        _appSettings.MobileTabLabels = value;
        SaveSettings();
    }

    public bool MobileAlbumButtonsSetting => _appSettings.MobileAlbumButtons;

    public void PersistMobileAlbumButtons(bool value)
    {
        _appSettings.MobileAlbumButtons = value;
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

    // A sentence about a song that would not play, where the person can see
    // it: the desktop's status bar, and on the phone the mini player's second
    // line, in place of the artist. Skipping one is said for a few seconds;
    // giving up on a run of them (PlaylistControlViewModel.
    // MaxConsecutiveFailures) stays until something plays, because that is
    // the moment the app looks like it is playing with nothing coming out.
    private string? _playbackNotice;
    public string? PlaybackNotice
    {
        get => _playbackNotice;
        private set => SetProperty(ref _playbackNotice, value);
    }

    private static readonly TimeSpan SkippedNoticeDuration = TimeSpan.FromSeconds(5);
    private DispatcherTimer? _playbackNoticeTimer;
    private bool _playbackNoticeLastsUntilPlaying;

    private void ShowPlaybackNotice(PlaybackFailedEventArgs e)
    {
        _playbackNoticeTimer?.Stop();
        _playbackNoticeLastsUntilPlaying = e.StoppedPlayback;
        var title = string.IsNullOrWhiteSpace(e.Track.Title) ? "a song" : $"\"{e.Track.Title}\"";
        PlaybackNotice = e.StoppedPlayback
            ? "Stopped: the last few songs could not be played."
            : $"Could not play {title}. Skipped it.";

        if (e.StoppedPlayback)
            return;

        if (_playbackNoticeTimer == null)
        {
            _playbackNoticeTimer = new DispatcherTimer { Interval = SkippedNoticeDuration };
            _playbackNoticeTimer.Tick += (_, _) =>
            {
                _playbackNoticeTimer.Stop();
                PlaybackNotice = null;
            };
        }
        _playbackNoticeTimer.Start();
    }

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
            // Before the page changes, so the album it closes on the way out
            // stays open in the page left behind (RecordOpenAlbum).
            ScheduleRecordPlace();
            OnPropertyChanged();
            OnSidebarSelectionChanged();
        }
    }

    // Artists still uses the plain-text picker; Albums was replaced by
    // AlbumGridTiles below (see IsShowingAlbumGrid) - same underlying
    // SelectedSubItem/_selectedSubItems mechanism either way, just a
    // different picker UI in front of it for Albums now.
    public bool IsSubListVisible => _selectedSidebarItem?.Kind == SidebarItemKind.Artists;

    // Album art tile grid - shown instead of the track list while on Albums,
    // mirroring mobile's own Albums tab (see MobileMainViewModel.AlbumGridRows).
    // Unlike the old plain-text SubList this replaced, an album's songs are
    // shown in-place (see ExpandedAlbumName below) rather than by navigating
    // to a separate track-list view, so this is unconditional - always true
    // while on Albums.
    public bool IsShowingAlbumGrid => _selectedSidebarItem?.Kind == SidebarItemKind.Albums;

    // The Home shelves (see HomeViewModel) in place of any list.
    public bool IsShowingHome => _selectedSidebarItem?.Kind == SidebarItemKind.Home;

    // The queue (see QueueViewModel) in the track list, in place of the
    // browser's rows - MainView swaps which rows the list is handed.
    public bool IsShowingQueue => _selectedSidebarItem?.Kind == SidebarItemKind.Queue;

    public bool IsShowingTrackList =>
        !IsShowingDeviceDetail && !IsShowingAlbumGrid && !IsShowingHome && !IsShowingSearch && !IsShowingServerSettings && !IsShowingArtistPage;

    // ── Search (desktop) ─────────────────────────────────────────────────────
    //
    // The phone's Search tab as a page of its own: one box, and what it finds
    // across the whole library as albums, artists and songs - the same
    // SearchViewModel, suggestions and past searches included. Not the page
    // header's filter, which narrows the page it heads; this is a lookup that
    // leads to an album or an artist, or plays a song.
    public bool IsShowingSearch => _selectedSidebarItem?.Kind == SidebarItemKind.Search;

    // Made the first time the page is shown: the phone has its own, and
    // never opens this page.
    private SearchViewModel? _search;
    public SearchViewModel Search => _search ??= CreateSearch();

    // The songs found, as the Albums grid's open album lists its own
    // (AlbumTrackList): selected, played, downloaded and given the song menu
    // the same way. Played from, they are the queue.
    public SearchSongsViewModel SearchSongs { get; } = new();

    private SearchViewModel CreateSearch()
    {
        var search = new SearchViewModel(this);
        search.ResultsChanged += (_, _) =>
        {
            SearchSongs.Availability = Availability;
            SearchSongs.CurrentlyPlayingTrackId = CurrentlyPlayingTrack?.Id;
            SearchSongs.ExpandedTracks = search.SongResults.Select(r => r.Track).ToList();
        };
        PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(RecentSearches):
                    search.RefreshSuggestions();
                    break;
                case nameof(CurrentlyPlayingTrack):
                    SearchSongs.CurrentlyPlayingTrackId = CurrentlyPlayingTrack?.Id;
                    break;
                case nameof(Availability):
                    search.ApplyAvailability(StreamingServerFingerprint, IsStreamingServerReachable);
                    SearchSongs.Availability = Availability;
                    break;
            }
        };
        return search;
    }


    // Artists: the column of names beside the picked artist's page - their
    // header and an album card each (ArtistHeader, ArtistAlbums) - in place of
    // a track list.
    public bool IsShowingArtistPage => _selectedSidebarItem?.Kind == SidebarItemKind.Artists;

    public bool IsShowingPlaylist => _selectedSidebarItem?.Kind == SidebarItemKind.Playlist;

    // ── Download All (desktop) ───────────────────────────────────────────────
    //
    // One icon for every song on the page not yet on this device - a
    // playlist's, or the picked artist's - the phone's album and playlist
    // headers' Download All. Not over Songs: the whole library behind one
    // click is a trap rather than a shortcut. Its own indicator, so it spins
    // for as long as its batch runs (TrackDownloadRunner.DownloadAlbumAsync);
    // an album opened in the grid has its tile's.
    public BulkDownloadIndicatorViewModel PageDownload { get; } = new();

    private IReadOnlyList<Track> PageDownloadTracks =>
        IsShowingArtistPage ? ArtistPage.AllTracks
        : IsShowingPlaylist ? DisplayedTracks
        : [];

    // Not while its own batch runs: a song landing mid-batch is a library
    // change, and the last one going local would take the spinner away
    // before the batch is over.
    private void RefreshPageDownload()
    {
        if (PageDownload.IsDownloading)
            return;
        var availability = Availability;
        PageDownload.IsDownloadable = PageDownloadTracks.Any(availability.IsDownloadable);
    }

    public Task DownloadPageAsync() => Downloads.DownloadAlbumAsync(PageDownload, PageDownloadTracks.ToList());

    public bool IsShowingDeviceDetail => _selectedSidebarItem?.Kind == SidebarItemKind.Device;

    // The browser's Server Settings page, shown in the content area beside the
    // sidebar rather than over the whole view - so the top bar's transport,
    // seek and volume stay where they are and a track keeps playing while its
    // server is being configured. The panel itself is built by
    // MainView.axaml.cs, which is where the other server's-settings pane
    // (ServerSettingsHost) is filled in too and for the same reason:
    // SettingsPanel takes its ViewModel through a constructor.
    public bool IsShowingServerSettings => _selectedSidebarItem?.Kind == SidebarItemKind.ServerSettings;
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

    // The two ways the paired server can be mid-conversation, as the one thing
    // a reader sees: ConnectionStatusIcon shows a single glyph, so waiting for
    // approval and syncing have to arrive as one state rather than two that
    // could both be true. See that control's remarks - it was a lit check with
    // a spinner beside it that prompted the whole thing.
    public bool IsSelectedDeviceBusy => IsPairAwaitingApproval || (IsSelectedDevicePaired && IsSyncing);
    public bool IsPairedServerBusy => IsPairedServerAwaitingApproval || IsSyncing;

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

    // ── Browser pairing ───────────────────────────────────────────────────
    //
    // The pairing screen a browser tab the server does not know shows instead
    // of everything else - see BrowserPairingViewModel. Left as it is on every
    // other head.
    public BrowserPairingViewModel BrowserPairing { get; } = new();

    // The server this tab was loaded from, once it has said who it is: the
    // one server a tab's tracks can stream from - see
    // StreamingServerFingerprint.
    private string? _browserOriginFingerprint;
    public string? BrowserOriginFingerprint
    {
        get => _browserOriginFingerprint;
        set
        {
            if (_browserOriginFingerprint == value)
                return;
            _browserOriginFingerprint = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Availability));
            Browser.ApplyTrackAvailability(StreamingServerFingerprint, IsStreamingServerReachable);
            Home.ApplyAvailability();
            Queue.ApplyAvailability();
        }
    }

    // Whether the server made this tab one of its administrators. Decides
    // whether the sidebar offers Server Settings: a listener's tab can play
    // everything, and would only be refused on that page. False until the
    // server has said otherwise.
    private bool _isBrowserAdmin;
    public bool IsBrowserAdmin
    {
        get => _isBrowserAdmin;
        set
        {
            if (_isBrowserAdmin == value)
                return;
            _isBrowserAdmin = value;
            OnPropertyChanged();
            UpdateServerSettingsSidebarRows();
        }
    }

    // What the server said about writing to its music folders, for an
    // administrator's tab - see LibraryWriteWarning - or, on a desktop, edits
    // and files from here that it could not write (OnUnwrittenChanged). Null when
    // there is nothing to say, and after the banner's own close button: a
    // server left read-only on purpose should be able to stop being told so,
    // and the page asks again on its next load.
    private string? _serverLibraryWriteWarning;
    public string? ServerLibraryWriteWarning
    {
        get => _serverLibraryWriteWarning;
        set
        {
            if (_serverLibraryWriteWarning == value)
                return;
            _serverLibraryWriteWarning = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasServerLibraryWriteWarning));
        }
    }

    public bool HasServerLibraryWriteWarning => !string.IsNullOrEmpty(_serverLibraryWriteWarning);

    private RelayCommand? _dismissServerLibraryWriteWarningCommand;
    public ICommand DismissServerLibraryWriteWarningCommand =>
        _dismissServerLibraryWriteWarningCommand ??= new RelayCommand(() => ServerLibraryWriteWarning = null);

    // A page opened at #page=settings asks for the settings before the server
    // has said whether this tab may see them - see RequestServerSettingsPage.
    private bool _serverSettingsPageRequested;

    // Lands on Server Settings now if this tab may see it, or as soon as the
    // server says it may. Asked for by a page opened with page=settings.
    public void RequestServerSettingsPage()
    {
        if (_sidebarItems.FirstOrDefault(i => i.Kind == SidebarItemKind.ServerSettings) is { } row)
        {
            SelectedSidebarItem = row;
            return;
        }

        _serverSettingsPageRequested = true;
    }

    // Whether this head's sidebar has a Server Settings page at all - the
    // browser's, and only the browser's. A property rather than the platform
    // check inline so a test can stand in for a browser.
    internal bool HostsServerSettingsPage { get; set; } = OperatingSystem.IsBrowser();

    private void UpdateServerSettingsSidebarRows()
    {
        if (!HostsServerSettingsPage)
            return;

        var existing = _sidebarItems.FirstOrDefault(i => i.Kind == SidebarItemKind.ServerSettings);
        if (IsBrowserAdmin && existing == null)
        {
            AddServerSettingsSidebarRows();
            if (_serverSettingsPageRequested)
            {
                _serverSettingsPageRequested = false;
                RequestServerSettingsPage();
            }
        }
        else if (!IsBrowserAdmin && existing != null)
        {
            var index = _sidebarItems.IndexOf(existing);
            if (SelectedSidebarItem == existing)
                SelectedSidebarItem = _sidebarItems.FirstOrDefault(i => i.Kind == SidebarItemKind.Songs);
            _sidebarItems.RemoveAt(index);
            if (index > 0 && _sidebarItems[index - 1] is { Kind: SidebarItemKind.Header, Name: "Server" })
                _sidebarItems.RemoveAt(index - 1);
        }
    }

    private void AddServerSettingsSidebarRows()
    {
        _sidebarItems.Add(new SidebarItem(SidebarItemKind.Header, "Server"));
        _sidebarItems.Add(new SidebarItem(SidebarItemKind.ServerSettings, "Server Settings", MaterialIconKind.CogOutline));
    }

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
            OnPropertyChanged(nameof(IsOpenSelectedServerSettingsEnabled));
        }
    }

    // Whether the selected server can be talked to right now. Read off the
    // sidebar row's own glyph (SidebarItem.ShowUnreachableIcon) rather than
    // worked out again here, so the device-detail header and the row beside it
    // cannot disagree about it - they once did, a warning in the sidebar and a
    // green check in the header for the same unreachable server. A row with no
    // live endpoint at all (the pinned paired-server placeholder, see
    // DeviceSidebarSection.RemoveItem) has no address to reach, whatever it
    // says.
    public bool IsSelectedServerReachable =>
        SelectedDevice != null && !(_selectedSidebarItem?.ShowUnreachableIcon ?? false);

    // Open in Browser stays visible for a paired server that has dropped off
    // the network, so it is clear the action exists, but greyed out: it has to
    // sign a request against that server, and there is nothing to send it to.
    public bool IsOpenSelectedServerSettingsEnabled => IsSelectedServerReachable && !IsOpeningServerSettings;

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
            using var http = PeerHttpClient.Create(TimeSpan.FromSeconds(15));
            var client = CreateServerAdminClient(http, device);

            var pairing = await client.IssuePairingCodeAsync(grantsAdmin: true);
            if (!PlatformShell.OpenUrl(pairing.BrowserUrl))
                ServerSettingsError = "Could not open a browser.";
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


    // ── Handing out pairing codes ─────────────────────────────────────────────

    // "Generate Pairing Code" on mobile's Settings sheet, and what came of
    // it - see PairingCodeIssuerViewModel.
    public PairingCodeIssuerViewModel PairingCodes { get; } = new();

    // Whether mobile's Settings sheet offers to hand out a pairing code. A phone
    // has no sidebar to select a server in, so it asks the one server it is
    // paired with - resolved through PairedServerReachability, which knows the
    // server's current address whether it was found on this link or remembered
    // from home (see docs/REMOTE-ACCESS.md).
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

    public Task InviteDeviceToPairedServerAsync()
    {
        if (_reachability?.PairedServerDevice is not { } device || _signingKey == null || _deviceIdentity == null)
            return Task.CompletedTask;

        return PairingCodes.IssueAsync(http => CreateServerAdminClient(http, device), device.BaseUri, _logger);
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

    // Every server settings screen built this session, kept rather than
    // rebuilt. Selecting a playlist and coming back used to throw the whole
    // thing away and reassemble it from nothing - a fresh load, a fresh roster,
    // the Logs tab back on the server's own log at the top - when the only
    // thing that had actually happened was the reader looking at something else
    // for a moment. Keeping it means the tab, the log being read, its scroll
    // position and any half-finished edit are simply still there, because they
    // were never taken apart.
    //
    // Keyed by fingerprint, with the address it was built against alongside: a
    // server reached at a new one (a LAN-to-tailnet handover) does need a new
    // screen, since that address is baked into the admin client behind it.
    // Bounded by how many servers the sidebar has ever shown, which under this
    // app's deployment model is one and change.
    private readonly Dictionary<string, (Uri BaseUri, SettingsViewModel Settings)> _serverSettings = new();

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

        SelectedServerSettings = ServerSettingsFor(device);
    }

    private SettingsViewModel ServerSettingsFor(DiscoveredDevice device)
    {
        if (_serverSettings.TryGetValue(device.Fingerprint, out var kept))
        {
            if (kept.BaseUri == device.BaseUri)
                return kept.Settings;

            // Reached somewhere else now, so the screen that was talking to the
            // old address is no longer talking to anything.
            _serverSettings.Remove(device.Fingerprint);
        }

        _adminHttp ??= PeerHttpClient.Create(TimeSpan.FromSeconds(15));
        var settings = new SettingsViewModel(
            new RemoteServerSettingsBackend(CreateServerAdminClient(_adminHttp, device)),
            _appSettings, _appSettingsStore);
        _serverSettings[device.Fingerprint] = (device.BaseUri, settings);
        return settings;
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
    private (bool, bool, bool, bool, bool, bool, string?, bool, string?, bool, bool, bool, bool)? _lastPairButtonState;

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
            IsSelectedServerReachable,
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
        // Appears with the pairing and goes with it - it reads off the trusted
        // store, which PairWithServer/UnpairServer are what change.
        OnPropertyChanged(nameof(SelectedDevicePinnedFingerprint));
        OnPropertyChanged(nameof(HasSelectedDevicePinnedFingerprint));
        OnPropertyChanged(nameof(IsPairedServerTrustConfirmed));
        OnPropertyChanged(nameof(IsPairedServerAwaitingApproval));
        OnPropertyChanged(nameof(IsSelectedDeviceTrustConfirmed));
        OnPropertyChanged(nameof(IsPairAwaitingApproval));
        OnPropertyChanged(nameof(IsSelectedDeviceBusy));
        OnPropertyChanged(nameof(IsPairedServerBusy));
        OnPropertyChanged(nameof(PairActionLabel));
        OnPropertyChanged(nameof(IsPairActionEnabled));
        OnPropertyChanged(nameof(PairActionHint));
        OnPropertyChanged(nameof(IsPairingCodeRequired));
        OnPropertyChanged(nameof(IsPairSubmittable));
        OnPropertyChanged(nameof(CanOpenSelectedServerSettings));
        OnPropertyChanged(nameof(IsSelectedServerReachable));
        OnPropertyChanged(nameof(IsOpenSelectedServerSettingsEnabled));
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
    // The container's logger factory, for what this view model builds itself
    // (Browser, Sync) and for the views it is handed to - TrackInfoWindow,
    // MainView, the mobile TrackInfoView - which are built by XAML or by a
    // view, have no constructor the container fills, and used to reach for
    // AppLogging's static factory instead. The designer's constructor gets a
    // factory that logs nothing.
    public ILoggerFactory LoggerFactory { get; }

    public MainViewModel()
    {
        // The collaborators this class forwards to are constructed even here:
        // most of what MainView.axaml binds (Rows, PageSubtitle, SubListItems,
        // the tile grids) now reads through Browser, so leaving it null would
        // make the previewer throw on the first binding rather than render an
        // empty window. Over an empty Library, so nothing touches disk.
        _appSettings = new AppSettings();
        LoggerFactory = NullLoggerFactory.Instance;
        Library   = new Library(new List<Track>());
        Browser   = new LibraryBrowserViewModel(Library, this, LoggerFactory.CreateLogger<LibraryBrowserViewModel>());
        Playlists = new PlaylistManagementViewModel(Library, _sidebarItems, this);
        Home      = new HomeViewModel(Library, new AlbumProgressTracker(Library), null);
        Downloads = CreateDownloadRunner();
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
        OutputDeviceControlViewModel outputDevice,
        CurrentlyPlayingControlViewModel nowPlaying,
        EqualizerViewModel equalizer,
        SidebarRenameService rename,
        ILogger<MainViewModel> logger,
        ILoggerFactory loggerFactory,
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
        LogViewModel? log = null,
        // Trailing + defaulted for the same reason as log above: nothing in a
        // MainViewModel test needs smart playlists recomputing, and the rule
        // editor is the only thing that asks for this.
        SmartPlaylistRefresher? smartPlaylists = null,
        // Trailing + defaulted for the same reason: only a cover change on a
        // server's song signs with it (ArtEditorFor). The container hands in
        // whichever this head has - a device key, or the browser's pairing.
        IPeerCredentials? peerCredentials = null,
        // Trailing + defaulted like the rest of the sync stack: only a head
        // with files of its own and a server to send them to has one.
        LibraryMirrorService? libraryMirrorService = null,
        // Trailing + defaulted like the rest: only a head with files of its
        // own has a folder of its own to put dropped songs in.
        FlowerFolder? flowerFolder = null)
    {
        Library                = library;
        _flowerFolder          = flowerFolder;
        SmartPlaylists         = smartPlaylists;
        _playlistControlViewModel = playlistControlViewModel;
        Volume                 = volume;
        OutputDevice           = outputDevice;
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
        _peerTrackResolver     = peerTrackResolver;
        _peerCredentials       = peerCredentials;
        DeviceNicknames        = deviceNicknameStore;
        NetworkDiscovery       = networkDiscovery;
        _appSettingsStore      = appSettingsStore;
        _busy                  = busy;
        ITunesImport           = iTunesImport;
        _logger                = logger;
        LoggerFactory          = loggerFactory;
        if (networkDiscovery != null && peerCredentials != null)
        {
            PairedIcon = new PairedServerIcon(appSettings, appSettingsStore, peerCredentials,
                PeerHttpClient.Create(TimeSpan.FromSeconds(15)), LoggerFactory.CreateLogger<PairedServerIcon>());
        }

        Browser = new LibraryBrowserViewModel(library, this, LoggerFactory.CreateLogger<LibraryBrowserViewModel>(), animationClock);
        Browser.RestoreSort(
            appSettings.SortColumn ?? "TrackNumber",
            appSettings.SortColumn is null || appSettings.SortAscending,
            appSettings.SortArtistAlbumsByYear);

        // The browser owns the state; these re-raise it on this ViewModel,
        // which is what every binding actually watches - along with the page
        // header's own summaries of it.
        _subscriptions.Add<PropertyChangedEventHandler>(
            (_, e) =>
            {
                OnPropertyChanged(e.PropertyName);
                switch (e.PropertyName)
                {
                    case nameof(LibraryBrowserViewModel.DisplayedTracks):
                        OnPropertyChanged(nameof(PageSubtitle));
                        RefreshPageDownload();
                        break;
                    case nameof(LibraryBrowserViewModel.AlbumGridTiles):
                        OnPropertyChanged(nameof(PageSubtitle));
                        break;
                    case nameof(LibraryBrowserViewModel.SelectedSubItems):
                        RefreshArtistPage();
                        RefreshPageDownload();
                        if (IsShowingArtistPage)
                            ScheduleRecordPlace();
                        break;
                    case nameof(LibraryBrowserViewModel.ExpandedAlbumKey):
                        RecordOpenAlbum();
                        break;
                    // A rescan or a sync replaced the library: the cards hold
                    // the tracks they were built from.
                    case nameof(LibraryBrowserViewModel.SubListItems):
                        RefreshArtistPage(force: true);
                        RefreshPageDownload();
                        OnPropertyChanged(nameof(PageSubtitle));
                        break;
                    case nameof(LibraryBrowserViewModel.VisibleSubListItems):
                        OnPropertyChanged(nameof(PageSubtitle));
                        break;
                    case nameof(LibraryBrowserViewModel.FilterText):
                        SyncHomeFilter();
                        RefreshArtistPage();
                        RefreshPageDownload();
                        break;
                }
            },
            h => Browser.PropertyChanged += h, h => Browser.PropertyChanged -= h);

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(CurrentlyPlayingTrack) or nameof(Availability))
                ArtistPage.UpdateRows(Availability, CurrentlyPlayingTrack?.Id);
            if (e.PropertyName is nameof(Availability))
                RefreshPageDownload();
        };

        Playlists = new PlaylistManagementViewModel(library, _sidebarItems, this, _appSettings.CollapsedPlaylistFolders);
        Downloads = CreateDownloadRunner();

        // Where a cover tapped on it leads is the view's to decide - the Albums
        // grid here (ShowAlbumAsync, wired by MainView), an album screen on a
        // phone (MobileMainViewModel) - so nothing here answers AlbumOpened.
        Home = new HomeViewModel(library, playlistControlViewModel.AlbumProgress, playlistControlViewModel,
            () => (StreamingServerFingerprint, IsStreamingServerReachable))
        {
            ContinuePlayingLimit = _appSettings.HomeContinuePlayingLimit,
            RecentlyPlayedLimit = _appSettings.HomeRecentlyPlayedLimit,
            RecentlyAddedMonths = _appSettings.HomeRecentlyAddedMonths,
            ShelfOrder = HomeShelfOrder.Parse(_appSettings.HomeShelfOrder),
            HiddenFromRecentlyPlayed = _appSettings.HomeRecentlyPlayedHidden,
        };
        _subscriptions.Add<EventHandler>((_, _) =>
        {
            _appSettings.HomeRecentlyPlayedHidden = new Dictionary<string, DateTimeOffset>(Home.HiddenFromRecentlyPlayed);
            SaveSettings();
        },
            h => Home.HiddenFromRecentlyPlayedChanged += h, h => Home.HiddenFromRecentlyPlayedChanged -= h);
        foreach (var shelf in Home.ShelfOrder)
            HomeShelfSettings.Add(new HomeShelfSettingRow(shelf, this));

        _subscriptions.Add<EventHandler<PlaybackFailedEventArgs>>((_, e) => ShowPlaybackNotice(e),
            h => playlistControlViewModel.PlaybackFailed += h, h => playlistControlViewModel.PlaybackFailed -= h);
        _subscriptions.Add<PropertyChangedEventHandler>((_, e) =>
        {
            if (e.PropertyName == nameof(PlaylistControlViewModel.IsPlaying)
                && _playbackNoticeLastsUntilPlaying && playlistControlViewModel.IsPlaying)
            {
                _playbackNoticeLastsUntilPlaying = false;
                PlaybackNotice = null;
            }
        },
            h => playlistControlViewModel.PropertyChanged += h, h => playlistControlViewModel.PropertyChanged -= h);

        Queue = new QueueViewModel(playlistControlViewModel,
            () => (StreamingServerFingerprint, IsStreamingServerReachable));
        _subscriptions.Add<System.ComponentModel.PropertyChangedEventHandler>((_, e) =>
        {
            if (e.PropertyName is nameof(QueueViewModel.Rows) or nameof(QueueViewModel.Caption) && IsShowingQueue)
                OnPropertyChanged(nameof(PageSubtitle));
        },
            h => Queue.PropertyChanged += h, h => Queue.PropertyChanged -= h);

        _subscriptions.Add<EventHandler<DeletePlaylistConfirmationEventArgs>>(
            (_, e) => DeletePlaylistConfirmationRequested?.Invoke(this, e),
            h => Playlists.DeleteConfirmationRequested += h, h => Playlists.DeleteConfirmationRequested -= h);

        // A recompute changes a playlist's contents without going through
        // PlaylistsChanged (deliberately - see Library.SavePlaylists), so this
        // is the only signal that the list on screen is now out of date. Only
        // the playlist actually being shown matters; the rest will be right
        // whenever they are next selected. Marshalled because a pass runs on
        // whichever thread its debounce timer completed on.
        if (smartPlaylists != null)
        {
            _subscriptions.Add<EventHandler<SmartPlaylistsRefreshedEventArgs>>(
                (_, e) =>
                {
                    if (_selectedSidebarItem?.Playlist is not { } shown || !e.Changed.Contains(shown))
                        return;

                    Dispatcher.UIThread.Post(ScheduleFilter);
                },
                h => smartPlaylists.Refreshed += h, h => smartPlaylists.Refreshed -= h);
        }

        OpenAppDataLocationCommand  = new RelayCommand(OpenAppDataLocation);
        RebuildDatabaseCommand      = new AsyncRelayCommand(RebuildDatabaseAsync);
        SortByColumnCommand         = new RelayCommand<string>(Browser.SortByColumn);
        OpenSettingsCommand         = new RelayCommand(() => SettingsRequested?.Invoke(this, EventArgs.Empty));
        OpenColumnSelectorCommand   = new RelayCommand(() => ColumnSelectorRequested?.Invoke(this, EventArgs.Empty));
        TogglePlaybackControlsPlacementCommand = new RelayCommand(() =>
            PlaybackControlsPlacement = PlaybackControlsAtBottom
                ? PlaybackControlsPlacement.Top
                : PlaybackControlsPlacement.Bottom);
        OpenLogWindowCommand        = new RelayCommand(() => LogWindowRequested?.Invoke(this, EventArgs.Empty));
        OpenEqualizerWindowCommand  = new RelayCommand(
            () => EqualizerWindowRequested?.Invoke(this, EventArgs.Empty), () => Equalizer.IsAvailable);
        NewPlaylistCommand          = new AsyncRelayCommand(() => CreatePlaylistWithTrack(null));
        NewSmartPlaylistCommand     = new RelayCommand(NewSmartPlaylist);
        PlayOrPauseCommand          = new RelayCommand(PlayOrPauseFromCurrentView);
        PlayViewCommand             = new RelayCommand(() => PlayView(shuffle: false));
        ShuffleViewCommand          = new RelayCommand(() => PlayView(shuffle: true));
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

        ToggleAlbumExpandedCommand = new RelayCommand<AlbumTileViewModel>(Browser.ToggleAlbumExpanded);

        Sync = new PeerSyncCoordinator(
            this, appSettings, appSettingsStore, deviceIdentityStore,
            LoggerFactory.CreateLogger<PeerSyncCoordinator>(),
            networkDiscovery, reachability, playlistSyncService, librarySyncService,
            libraryDownloadService, peerPairingService, peerTrackResolver, trustedPeerStore, deviceIdentity, signingKey,
            Library, libraryMirrorService);

        _deviceSidebar = new DeviceSidebarSection(_sidebarItems, this, deviceNicknameStore, reachability, networkDiscovery, PairedIcon);
        if (PairedIcon != null)
        {
            _subscriptions.Add<EventHandler>((_, _) => Dispatcher.UIThread.Post(() =>
            {
                _deviceSidebar.RefreshIcons();
                OnPropertyChanged(nameof(PairedServerIconImage));
                OnPropertyChanged(nameof(PairedServerIconEmoji));
                OnPropertyChanged(nameof(HasPairedServerIconEmoji));
                OnPropertyChanged(nameof(HasPairedServerIconImage));
            }),
                h => PairedIcon.Changed += h, h => PairedIcon.Changed -= h);
        }

        // The coordinator owns the state; these re-raise it on this ViewModel,
        // which is what every binding actually watches.
        _subscriptions.Add<PropertyChangedEventHandler>(
            (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(PeerSyncCoordinator.IsSyncing):
                    OnPropertyChanged(nameof(IsSyncing));
                    OnPropertyChanged(nameof(IsSelectedDeviceBusy));
                    OnPropertyChanged(nameof(IsPairedServerBusy));
                    _deviceSidebar.SetPairedServerSyncing(Sync.IsSyncing);
                    break;
                case nameof(PeerSyncCoordinator.LastForceSyncResult):
                    OnPropertyChanged(nameof(LastForceSyncResult));
                    break;
                case nameof(PeerSyncCoordinator.UnwrittenWarning):
                    OnUnwrittenChanged();
                    break;
                case nameof(PeerSyncCoordinator.LastSyncedAt):
                    OnPropertyChanged(nameof(LastSyncedAt));
                    OnPropertyChanged(nameof(LastSyncedDisplay));
                    break;
            }
        },
            h => Sync.PropertyChanged += h, h => Sync.PropertyChanged -= h);

        _subscriptions.Add<EventHandler>((_, _) =>
        {
            OnPropertyChanged(nameof(PairedServerFingerprint));
            OnPropertyChanged(nameof(PairedServerAlias));
            OnPropertyChanged(nameof(Availability));
            OnUnwrittenChanged();
            NotifyPairButtonPropertiesChanged();
            // Identity first, then the reachability glyph - SyncPairedServerRow
            // only ever touches a row that is already pinned, so pinning has to
            // happen here rather than inside it (see DeviceSidebarSection).
            if (Sync.PairedServerFingerprint is { Length: > 0 } fingerprint)
                _deviceSidebar.PinPairedServerRow(fingerprint);
            else
                _deviceSidebar.UnpinPairedServerRow();
            _deviceSidebar.SyncPairedServerRow();
            PairedIcon?.FollowPairing();
            _deviceSidebar.RefreshIcons();
            // Pairing, unpairing and being made an administrator each change
            // which folders this device scans - see DeviceLibraryFolders.
            FollowScanFolders();
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
        Home.IsActive = IsShowingHome;
        Queue.IsActive = IsShowingQueue;
        Browser.Repopulate();

        // Everything here runs on the UI thread, not just the rebuilds. Both
        // events can be raised off it - a play count bumped as a track ends,
        // a rescan's Task.Run - and ScheduleContentSync's continuation goes on
        // to enumerate _sidebarItems, a plain ObservableCollection the UI
        // thread mutates through AddOrUpdateDeviceSidebarItem/RemoveDeviceItem.
        _subscriptions.Add<EventHandler>((_, _) => Dispatcher.UIThread.Post(() =>
        {
            Browser.Repopulate();
            if (IsShowingSearch)
                Search.RefreshNow();
            // A merge in flight means this fired because one of our own
            // syncs just merged something (see RunTrackedSync's doc comment) -
            // not a genuine local change - so don't treat it as one.
            if (!Sync.IsMergingOwnSync)
                ScheduleContentSync();
            else
                _logger.LogDebug("LibraryChanged fired mid-sync - not scheduling a resync");
        }),
            h => library.LibraryChanged += h, h => library.LibraryChanged -= h);
        _subscriptions.Add<EventHandler<TrackChangedEventArgs>>(
            (_, e) => Dispatcher.UIThread.Post(() => OnTrackChanged(e)),
            h => library.TrackChanged += h, h => library.TrackChanged -= h);

        // Same reasoning as LibraryChanged above - PlaylistsUpdated is raised
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
            if (PairedIcon != null)
                PairedIcon.ObserveAsync(device).Forget(_logger, "The paired server's icon");
            TriggerSyncIfPeerCatalogChanged(device);
            TriggerSyncIfPeerPlaylistsChanged(device);
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
            // After the row's glyph, which IsSelectedServerReachable reads.
            NotifyPairButtonPropertiesChanged();
            Browser.ApplyTrackAvailability(StreamingServerFingerprint, IsStreamingServerReachable);
            Home.ApplyAvailability();
            Queue.ApplyAvailability();
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

        // An upload round in the status bar, where a rescan and a download
        // batch already say what they are doing. It can run for as long as an
        // album takes to cross the network, and a spinner beside the server's
        // name with no words next to it reads as a sync that has hung.
        if (libraryMirrorService != null)
        {
            IDisposable? uploadBusy = null;
            _subscriptions.Add<Action<int, int>>((done, total) => Dispatcher.UIThread.Post(() =>
            {
                // The new scope before the old one goes, so the indicator does
                // not blink off between two songs (see BusyState on nesting).
                var next = total > 0
                    ? _busy.BeginScope($"Uploading song {done + 1} of {total} to {PairedServerAlias ?? "your server"}")
                    : null;
                uploadBusy?.Dispose();
                uploadBusy = next;
            }),
                h => libraryMirrorService.UploadProgress += h, h => libraryMirrorService.UploadProgress -= h);
        }
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
    // ── Technical details ────────────────────────────────────────────────────
    //
    // Settings' debugging corner on both heads: what Flower is costing this
    // device (ResourceUsageViewModel - polled only while a settings screen
    // showing it is open, which starts and stops it), and whether the
    // frame-rate readout (FrameMeter) is drawn over the window. That one for
    // this session only: a debugging aid, gone on the next launch.
    public ResourceUsageViewModel Resources { get; } = new();

    private bool _showsFrameMeter;
    public bool ShowsFrameMeter
    {
        get => _showsFrameMeter;
        set => SetProperty(ref _showsFrameMeter, value);
    }

    public void Dispose()
    {
        _subscriptions.Dispose();
        Resources.Dispose();
        _playbackNoticeTimer?.Stop();
        Home.Dispose();
        Queue.Dispose();
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

    // Double-click on an album tile in the Albums grid (see
    // MainView.axaml.cs's AlbumGrid_PointerPressed) - queues the whole album
    // in track order and starts playing from the first track, and makes sure
    // it ends up expanded rather than toggling closed (unlike a plain click's
    // ToggleAlbumExpandedCommand).
    public void PlayAlbum(AlbumTileViewModel tile)
    {
        var tracks = LibraryBrowserViewModel.ExpandedTracksFor(tile);
        if (tracks.Count == 0)
            return;

        // Unlike a plain click's ToggleAlbumExpandedCommand, this always ends
        // expanded rather than toggling closed.
        if (Browser.ExpandedAlbumKey != tile.Key)
            Browser.ToggleAlbumExpanded(tile);

        SetPlayQueue(tracks);
        _playlistControlViewModel.Play(tracks[0], 0);
    }

    // A cover tapped on Home: the album, open, in the Albums grid - the same
    // place a click on it there leads, so there is one way an album is shown
    // on desktop rather than a second screen for albums reached from Home.
    // Matched by name, which is what the Albums grid groups by.
    public Task ShowAlbumAsync(AlbumTileViewModel album) =>
        ShowAlbumAsync(tiles => tiles.FirstOrDefault(t => t.Name == album.Name));

    private async Task ShowAlbumAsync(Func<IReadOnlyList<AlbumTileViewModel>, AlbumTileViewModel?> pick)
    {
        if (_sidebarItems.FirstOrDefault(i => i.Kind == SidebarItemKind.Albums) is not { } albums)
            return;

        if (!string.IsNullOrEmpty(FilterText))
            FilterText = null;
        SelectedSidebarItem = albums;
        if (!await RebuildRowsImmediatelyAsync())
            return;

        if (pick(AlbumGridTiles) is not { } tile)
            return;

        if (Browser.ExpandedAlbumKey != tile.Key)
            Browser.ToggleAlbumExpanded(tile);
        AlbumRevealRequested?.Invoke(this, tile.Key);
    }

    // The Albums grid should scroll the tile with this key into view - see
    // ShowAlbumAsync.
    public event EventHandler<AlbumTileKey>? AlbumRevealRequested;

    // Enter/double-click on a song in one album's own list - the Albums
    // grid's expanded album, or an album card on Artists (AlbumTrackList) -
    // as opposed to double-clicking the album tile itself (PlayAlbum above).
    // Deliberately does NOT go through PlayTrack/SyncPlayQueueToCurrentView:
    // that sources the queue from DisplayedTracks, which for the Albums grid is
    // driven by _selectedSubItems - the Ctrl/Shift multi-select used for
    // drag-to-playlist (see ExpandedAlbumName's remarks), not by which album is
    // actually expanded on screen. Left-over multi-selected tiles from that
    // gesture made the queue silently become empty, or a union of tracks across
    // every selected album - playback would run off the end of the displayed
    // album straight into a different one. Queuing the list the song was
    // played from keeps the queue matching what's actually on screen.
    public void PlayTrackInAlbum(IReadOnlyList<Track> albumTracks, Track track)
    {
        if (albumTracks.Count == 0)
            return;

        SetPlayQueue(albumTracks);
        _playlistControlViewModel.Play(track, albumTracks.ToList().IndexOf(track));
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

    // Every song on screen from the top, or shuffled from a random one - the
    // Artists page header's play and shuffle circles, which are the phone's
    // album-header ones (MobileMainViewModel.PlayAlbum). On Artists that is
    // the cards' songs in the cards' order, album after album, not the row
    // order the hidden track list keeps.
    public void PlayView(bool shuffle) =>
        PlayTracks(IsShowingArtistPage
            ? ArtistPage.AllTracks
            : DisplayedTracks, shuffle);

    // These songs as the queue, from the top or shuffled from a random one -
    // the Artists header's circles over everything on the page, the Albums
    // grid's open album's Play and Shuffle over that album, and every Play and
    // Shuffle on the phone. Through ToggleShuffle rather than the property, so
    // the choice persists like the Now Playing toggle.
    public void PlayTracks(IReadOnlyList<Track> tracks, bool shuffle)
    {
        if (tracks.Count == 0)
            return;
        if (_playlistControlViewModel.IsShuffleEnabled != shuffle)
            _playlistControlViewModel.ToggleShuffle();

        var index = shuffle ? Random.Shared.Next(tracks.Count) : 0;
        SetPlayQueue(tracks);
        _playlistControlViewModel.Play(tracks[index], index);
    }

    // Re-anchors Next/Previous/auto-advance to a specific list of tracks.
    // Public because mobile needs the same thing over a different source: its
    // search results are a separate mirror of Rows rather than Rows itself, so
    // it cannot just call SyncPlayQueueToCurrentView above - which is what left
    // it with its own copy of this until now.
    public void SetPlayQueue(IEnumerable<Track> tracks) =>
        _playlistControlViewModel.SetCurrentPlaylist(new Playlist("Now Playing Queue", new List<Track>(tracks)));

    // Into the queue that is playing, rather than in place of it - see
    // PlaylistControlViewModel.PlayNext/AddToQueue.
    public void PlayNext(IReadOnlyList<Track> tracks) => _playlistControlViewModel.PlayNext(tracks);

    public void AddToQueue(IReadOnlyList<Track> tracks) => _playlistControlViewModel.AddToQueue(tracks);

    // ── Sidebar ───────────────────────────────────────────────────────────────

    private void BuildSidebarItems()
    {
        _sidebarItems.Clear();
        _sidebarItems.Add(new SidebarItem(SidebarItemKind.Header,        "Library"));
        _sidebarItems.Add(new SidebarItem(SidebarItemKind.Home,    "Home",    MaterialIconKind.HomeOutline));
        _sidebarItems.Add(new SidebarItem(SidebarItemKind.Search,  "Search",  MaterialIconKind.Magnify));
        _sidebarItems.Add(new SidebarItem(SidebarItemKind.Queue,   "Queue",   MaterialIconKind.PlaylistMusicOutline));
        _sidebarItems.Add(new SidebarItem(SidebarItemKind.History, "History", MaterialIconKind.ClockTimeEightOutline));
        _sidebarItems.Add(new SidebarItem(SidebarItemKind.Songs,   "Songs",   MaterialIconKind.Music));
        _sidebarItems.Add(new SidebarItem(SidebarItemKind.Albums,  "Albums",  MaterialIconKind.Album));
        _sidebarItems.Add(new SidebarItem(SidebarItemKind.Artists, "Artists", MaterialIconKind.AccountOutline));

        if (Library.Playlists.Count > 0)
        {
            _sidebarItems.Add(new SidebarItem(SidebarItemKind.Header, "Playlists"));
            foreach (var row in Playlists.CreateSidebarRows())
                _sidebarItems.Add(row);
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
            _sidebarItems.Add(new SidebarItem(SidebarItemKind.Device, pairedAlias, MaterialIconKind.ServerOutline)
            {
                IsPairedServer = true,
                IsReachable = false,
                IconEmoji = PairedIcon?.Emoji ?? "",
                IconImage = PairedIcon?.Image,
            });
        }

        // The browser's way into Settings, and its only one: a single-view
        // lifetime has no app menu and no second Window, so the row that would
        // be a menu item on desktop is a place here. Last, under everything the
        // library offers, because it is not part of the library - and shown
        // only on the one host that needs it, since a desktop head reaches the
        // same screen from its menu and administers a *selected* server through
        // the device-detail pane instead. What it opens is the server this tab
        // was served from (App.CreateOriginServerSettings), which is the only
        // server a tab has.
        //
        // Only for a tab the server made an administrator (IsBrowserAdmin),
        // added later if that answer arrives after this first build.
        if (HostsServerSettingsPage && IsBrowserAdmin)
            AddServerSettingsSidebarRows();

        // Restores whichever view (see AppSettings.LastSidebarKind/
        // LastPlaylistName's own doc comment) the user was on when the app
        // last closed, falling back to Home - on a genuine first run, when the saved view no longer exists (a
        // deleted playlist), or when nothing was ever saved at all.
        var restored = ResolveLastSidebarItem();
        WasLastViewRestored = restored != null;
        _selectedSidebarItem = restored ?? _sidebarItems.FirstOrDefault(i => i.Kind == SidebarItemKind.Home);
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
        return kind is SidebarItemKind.Songs or SidebarItemKind.Albums or SidebarItemKind.Artists or SidebarItemKind.Home or SidebarItemKind.Queue or SidebarItemKind.History
            ? _sidebarItems.FirstOrDefault(i => i.Kind == kind)
            : null;
    }

    // Called from MainView.axaml.cs (MainWindow.Closing, alongside the window
    // geometry save) with whichever scroll offset is relevant to the view
    // showing at that moment (MusicListView's or one of the album grids',
    // depending on IsShowingAlbumGrid) - this
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

    // The sidebar's own view of *who* is out there, rather than
    // NetworkDiscoveryService.KnownDevices - see IPeerSyncHost.ListedPeers for
    // why the set is a sidebar question.
    //
    // *Where* each of them is dialled is not a sidebar question, and is
    // resolved here rather than read off the row (see IPeerEndpointResolver).
    // A row holds the endpoint that was current when it was last touched by
    // discovery, which can be minutes old by the time a sync or a log push
    // reads it; this is read at the moment of use, so walking back onto the
    // home WiFi moves the very next request onto the LAN address without
    // waiting for the row to be updated by something else.
    IReadOnlyList<DiscoveredDevice> IPeerSyncHost.ListedPeers =>
        _sidebarItems.Where(i => i.Kind == SidebarItemKind.Device && i.Device != null)
            .Select(i => NetworkDiscovery?.EndpointFor(i.Device!.Fingerprint) ?? i.Device!)
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

    // The UI-side driver for the above: per-row spinner state and batch
    // throttling, shared by desktop's track-list download icon and right-click
    // Download items and by mobile's own download button (see
    // TrackDownloadRunner, and MobileMainViewModel, which forwards to this
    // rather than keeping a second copy). Rows are resolved through Rows by
    // SyncKey on demand - see DownloadAllAsync for why not by instance.
    public TrackDownloadRunner Downloads { get; private set; }

    public Task DeleteDownloadedFileAsync(Track track) => Sync.DeleteDownloadedFileAsync(track);

    private TrackDownloadRunner CreateDownloadRunner() =>
        new(DownloadTrackAsync,
            syncKey => Rows.FirstOrDefault(r => r.Track.SyncKey == syncKey),
            BeginBusyScope);


    // internal for MainViewModelSyncTriggerTests, which drive the discovery
    // handlers directly - reaching them through the real
    // NetworkDiscoveryService would mean standing up an mDNS backend AND an
    // HTTP /info endpoint per case, just to choose a Fingerprint.
    internal void TriggerSyncIfPeerCatalogChanged(DiscoveredDevice device) => Sync.TriggerSyncIfPeerCatalogChanged(device);

    internal void TriggerSyncIfPeerPlaylistsChanged(DiscoveredDevice device) => Sync.TriggerSyncIfPeerPlaylistsChanged(device);

    internal void TriggerSyncIfReady(DiscoveredDevice device) => Sync.TriggerSyncIfReady(device);

    // What a change to known tracks costs this view depends on what moved.
    //
    // A tag edit, new artwork or a download can move a row to another album
    // group or sort position, or change how it draws, so the list is rebuilt
    // and the paired server offered the change - the same as a rescan.
    //
    // A play, a star or a playback option changes a cell or two on the rows
    // showing those tracks, so only those are re-raised. None of them
    // schedules a content sync either: the periodic track-state push
    // (PeerSyncCoordinator.LogPushTick) already carries play counts, stars and
    // options to the server within seconds, without a catalog pull.
    private void OnTrackChanged(TrackChangedEventArgs e)
    {
        if (e.Reshapes)
        {
            Browser.Repopulate();
            if (e.Source == ChangeSource.Local && !Sync.IsMergingOwnSync)
                ScheduleContentSync();
            return;
        }

        Browser.NotifyTracksChanged(e.Tracks);
    }

    // Playlist CRUD, membership and the sidebar's Playlists section all live
    // in PlaylistManagementViewModel; these forward because MainView's context
    // menus, the Playlist main menu and MobileMainViewModel all reach them
    // through this ViewModel.
    public PlaylistManagementViewModel Playlists { get; } = null!;

    public Task CreatePlaylistWithTrack(Track? track) => Playlists.CreateWithTrack(track);

    public Task CreatePlaylistWithTracks(IEnumerable<Track> tracks, Playlist? folder = null) => Playlists.CreateWithTracks(tracks, folder);

    public Task CreatePlaylistFolder(Playlist? parent) => Playlists.CreateFolder(parent);

    public Task MovePlaylists(IReadOnlyList<Playlist> playlists, Playlist? folder) => Playlists.Move(playlists, folder);

    // The mobile flow: the name is typed before anything is created - see
    // PlaylistManagementViewModel.CreateNamed.
    public Task CreatePlaylistNamed(string name, IEnumerable<Track> tracks) => Playlists.CreateNamed(name, tracks);

    public Task DeletePlaylistAsync(Playlist playlist) => Playlists.DeleteAsync(playlist);

    public Task DeletePlaylistsAsync(IReadOnlyList<Playlist> playlists) => Playlists.DeleteAsync(playlists);

    // Pushed by MainView on every sidebar selection change: the ListBox owns
    // the multiple selection, and SelectedSidebarItem carries only one of it.
    public IReadOnlyList<Playlist> SelectedPlaylists { get; private set; } = Array.Empty<Playlist>();

    public void SetSelectedPlaylists(IReadOnlyList<Playlist> playlists) => SelectedPlaylists = playlists;

    public Task AddTrackToPlaylist(Track track, Playlist playlist) => Playlists.AddTrack(track, playlist);

    public Task AddTracksToPlaylist(IEnumerable<Track> tracks, Playlist playlist) => Playlists.AddTracks(tracks, playlist);

    public Task ReorderPlaylistTrack(Playlist playlist, Track dragged, Track? insertBefore) =>
        Playlists.ReorderTrack(playlist, dragged, insertBefore);

    // ── Smart playlists ───────────────────────────────────────────────────

    // Null on Flower.Web/WASM and in tests that did not ask for one - see the
    // constructor. Everything below no-ops without it rather than offering a
    // rule editor that cannot recompute what it saves.
    public SmartPlaylistRefresher? SmartPlaylists { get; }

    // Creates an ordinary, empty playlist and opens the editor on it. It only
    // becomes smart when the editor saves rules onto it, and cancelling removes
    // it again - see PlaylistManagementViewModel.CreateSmart.
    public void NewSmartPlaylist()
    {
        if (SmartPlaylists is null)
            return;

        var playlist = Playlists.CreateSmart();
        SmartPlaylistEditorRequested?.Invoke(this, new SmartPlaylistEditorEventArgs(playlist, IsNew: true));
    }

    public void EditSmartPlaylist(Playlist playlist)
    {
        if (SmartPlaylists is null)
            return;

        SmartPlaylistEditorRequested?.Invoke(this, new SmartPlaylistEditorEventArgs(playlist, IsNew: false));
    }

    public Task ConvertPlaylistToOrdinary(Playlist playlist) => Playlists.ConvertToOrdinary(playlist);

    // ── IPlaylistManagementHost ───────────────────────────────────────────

    // A playlist currently on screen gained/lost/reordered tracks. One discrete
    // user action, not a rapid-fire one like typing a search, so it bypasses
    // ScheduleFilter's 250ms debounce the way a navigation does - through it, a
    // dropped row sat back in its old place for the debounce plus the rebuild,
    // well over a second on a phone, before jumping to where it was dropped.
    void IPlaylistManagementHost.PlaylistContentChanged() =>
        RebuildRowsImmediatelyAsync().Forget(_logger, "Playlist content rebuild");

    void IPlaylistManagementHost.PlaylistFoldersCollapsedChanged() => SaveSettings();

    private void OnSidebarSelectionChanged()
    {
        Browser.CollapseExpandedAlbum();

        OnPropertyChanged(nameof(IsSubListVisible));
        OnPropertyChanged(nameof(IsShowingAlbumGrid));
        OnPropertyChanged(nameof(IsShowingHome));
        SyncHomeFilter();
        Home.IsActive = IsShowingHome;
        OnPropertyChanged(nameof(IsShowingQueue));
        OnPropertyChanged(nameof(ShowsPageFilter));
        OnPropertyChanged(nameof(ShowsSortButton));
        Queue.IsActive = IsShowingQueue;
        OnPropertyChanged(nameof(IsShowingTrackList));
        OnPropertyChanged(nameof(IsShowingArtistPage));
        OnPropertyChanged(nameof(IsShowingPlaylist));
        OnPropertyChanged(nameof(IsShowingSearch));
        // What it finds is scanned again on the way in, the library having
        // perhaps changed while it was away; the query and the results stay
        // meanwhile, so coming back shows them at once.
        if (IsShowingSearch)
            Search.RefreshNow();
        else
            _search?.CancelRebuild();
        OnPropertyChanged(nameof(IsShowingDeviceDetail));
        OnPropertyChanged(nameof(IsShowingServerSettings));
        OnPropertyChanged(nameof(ShowsPageHeader));
        OnPropertyChanged(nameof(ShowsPageSubtitle));
        OnPropertyChanged(nameof(ShowsPagePlay));
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageSubtitle));
        RefreshArtistPage();
        RefreshPageDownload();
        OnPropertyChanged(nameof(SelectedDevice));
        // A code typed for one server must never be submitted against
        // another, so the box empties whenever the selection moves.
        PairingCode = "";
        PairingCodeError = null;
        // Likewise for a code handed *out* for one server: it is worthless
        // against another, and leaving it on screen under a different server's
        // name says otherwise. Cleared only here and not in DeviceRowsChanged,
        // which also runs on the 5s peer poll - a code that vanished every few
        // seconds could not be read out loud.
        PairingCodes.Clear();
        ServerSettingsError = null;
        NotifyPairButtonPropertiesChanged();
        RefreshSelectedServerSettings();
        // History carries its own independent sort state (see SortColumn), so
        // switching to/from it changes what these computed properties report.
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
        PlatformShell.OpenFolder(dir);
    }

    private async Task RebuildDatabaseAsync()
    {
        if (_importer == null || _mainPlaylist == null)
            return;
        using var _ = _busy.BeginScope("Rebuilding library…");
        var libraryPaths = DeviceLibraryFolders.ScanPaths(_appSettings);
        ScannedFolders = libraryPaths;
        Library.UsePathRules(PathRules.For(Importer.Importer.ScanRoots(libraryPaths)));
        var freshTracks = Library.WithoutExcluded(await _importer.ImportAsync(libraryPaths));
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
        FolderWatcher?.Watch(DeviceLibraryFolders.ScanPaths(_appSettings));
    }

    // The folders the last scan walked (DeviceLibraryFolders.ScanPaths) - set
    // by the startup scan and every rescan. Null until the startup scan has
    // begun, which is what tells FollowScanFolders there is nothing to follow
    // yet: that scan reads the folders as they are by then.
    public IReadOnlyList<string>? ScannedFolders { get; set; }

    // Rescans, and watches the new set, when the folders this device scans are
    // no longer the ones it last scanned - after pairing, unpairing, being made
    // an administrator, or a change to the Music.app switch. Every other time
    // it is asked, nothing has changed and it does nothing.
    public void FollowScanFolders()
    {
        if (_importer?.ScansLocalFiles != true || OperatingSystem.IsBrowser() || ScannedFolders is not { } scanned)
            return;

        var folders = DeviceLibraryFolders.ScanPaths(_appSettings);
        if (folders.SequenceEqual(scanned, StringComparer.OrdinalIgnoreCase))
            return;

        _logger.LogInformation("The folders this device scans changed to [{Folders}] - rescanning", string.Join(", ", folders));
        ScannedFolders = folders;
        FolderWatcher?.Watch(folders);
        _ = RescanLibraryAsync();
    }

    // Keeps the library in step with its folders between scans - desktop
    // only, and set by the composition root once the startup scan is done
    // (App.axaml.cs). Null until then, and on every other head.
    public LibraryFolderWatcher? FolderWatcher { get; set; }

    // Whether songs can be dropped on the window: only where the library is
    // this machine's own folders. A browser tab's is a server's catalog.
    public bool AcceptsDroppedSongs => _importer?.ScansLocalFiles == true && !OperatingSystem.IsBrowser();

    // Songs dropped on the window - files, folders, or both. Those already in
    // a library folder are added where they are; the rest are copied into the
    // first one (LibraryDrop) and added from there, at once rather than when
    // the watcher next notices.
    public async Task AddDroppedSongsAsync(IReadOnlyList<string> paths)
    {
        if (!AcceptsDroppedSongs || paths.Count == 0)
            return;

        // On a device paired to a server, a song from anywhere else goes into
        // Flower's own folder - never a library folder, and never Music.app's
        // even when that one is taken in. See docs/SYNC-PLAN.md, "A paired
        // device has two sources of files, and no folders".
        var roots = Importer.Importer.ScanRoots(DeviceLibraryFolders.ScanPaths(_appSettings));
        if (!string.IsNullOrEmpty(_appSettings.PairedServerFingerprint) && _flowerFolder != null)
        {
            if (await _flowerFolder.ClaimAsync() is not { } flower)
            {
                using var refused = _busy.BeginScope("Flower's folder isn't available, so the songs were not added");
                await Task.Delay(TimeSpan.FromSeconds(4));
                return;
            }

            roots = [flower, .. roots.Where(r => !string.Equals(r, flower, StringComparison.OrdinalIgnoreCase))];
        }

        if (roots.Count == 0)
        {
            // Said where the drop was made, for long enough to read.
            using var notice = _busy.BeginScope("Add a library folder in Settings to drop songs here");
            await Task.Delay(TimeSpan.FromSeconds(4));
            return;
        }

        using var busy = _busy.BeginScope("Adding songs…");
        var result = await new LibraryDrop(roots, _logger).PrepareAsync(paths);
        _logger.LogInformation("Dropped songs: {Count} to add, {Copied} copied into {Root}, {Failed} could not be copied",
            result.Files.Count, result.Copied, roots[0], result.Failed);
        if (result.Files.Count == 0)
            return;

        if (FolderWatcher != null)
            await FolderWatcher.AddFilesAsync(result.Files);
        else
            await RescanLibraryAsync();

        // The first drop on a paired device is what makes Flower's folder one
        // this device scans. After the songs are in, not before: a rescan that
        // read the folder ahead of the copy would write the library back
        // without them.
        FollowScanFolders();
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
        // The queue starts with the song playing, so on its page it is
        // already there - the browser's rows, which that page does not show,
        // would send this off to Songs.
        if (IsShowingQueue || DisplayedTracks.Any(t => t.Id == track.Id))
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
                when _selectedSidebarItem.Playlist is not { } shown
                     || !PlaylistTree.TracksOf(shown, Library.Playlists).Any(t => t.Id == track.Id):
                var songs = _sidebarItems.FirstOrDefault(i => i.Kind == SidebarItemKind.Songs);
                if (songs != null)
                    SelectedSidebarItem = songs;
                break;
            case SidebarItemKind.Albums:
                SelectedSubItem = track.Album;
                break;
            case SidebarItemKind.Artists:
                if (!string.IsNullOrEmpty(ArtistFilterText))
                    ArtistFilterText = null;
                SelectedSubItem = track.Artists;
                break;
        }

        if (!await RebuildRowsImmediatelyAsync())
            return; // Superseded by a newer filter/navigation change - let that one win.

        NavigateToTrackRequested?.Invoke(this, track);
    }

    // ── Go to the album or artist playing ────────────────────────────────────
    //
    // The player bar's menu and the Controls menu, as the phone's Now Playing
    // menu has them: the album open in the Albums grid, or the artist's page.
    // The album is the tile holding the very song playing, where one does -
    // two albums can share a name - and by name otherwise.

    public Task GoToCurrentlyPlayingAlbumAsync()
    {
        if (CurrentlyPlayingTrack is not { } track || string.IsNullOrEmpty(track.Album))
            return Task.CompletedTask;

        return ShowAlbumAsync(tiles =>
            tiles.FirstOrDefault(t => t.Tracks.Any(x => x.Id == track.Id))
            ?? tiles.FirstOrDefault(t => t.Name == track.Album));
    }

    public Task GoToCurrentlyPlayingArtistAsync() =>
        CurrentlyPlayingTrack?.Artists is { Length: > 0 } artist ? ShowArtistAsync(artist) : Task.CompletedTask;

    // The artist's page, their name picked and scrolled to in the column of
    // names (ArtistRevealRequested). By the same raw field the column lists.
    public async Task ShowArtistAsync(string artist)
    {
        if (_sidebarItems.FirstOrDefault(i => i.Kind == SidebarItemKind.Artists) is not { } artists)
            return;

        if (!string.IsNullOrEmpty(FilterText))
            FilterText = null;
        if (!string.IsNullOrEmpty(ArtistFilterText))
            ArtistFilterText = null;
        SelectedSidebarItem = artists;
        SelectedSubItem = artist;
        if (!await RebuildRowsImmediatelyAsync())
            return;

        ArtistRevealRequested?.Invoke(this, artist);
    }

    public event EventHandler<string>? ArtistRevealRequested;

    // ── Back and Forward (desktop) ───────────────────────────────────────────
    //
    // Through the pages visited - a sidebar row, the artist picked on Artists,
    // with the album that was open on Albums - by the sidebar's arrows, the
    // View menu, and a mouse's own back and forward buttons.

    private readonly NavigationHistory _history = new();
    private bool _recordPlacePending;

    public bool CanGoBack => _history.CanGoBack;
    public bool CanGoForward => _history.CanGoForward;

    private RelayCommand? _goBackCommand;
    public IRelayCommand GoBackCommand => _goBackCommand ??= new RelayCommand(() => GoBackAsync().Forget(_logger, "Back"), () => CanGoBack);

    private RelayCommand? _goForwardCommand;
    public IRelayCommand GoForwardCommand => _goForwardCommand ??= new RelayCommand(() => GoForwardAsync().Forget(_logger, "Forward"), () => CanGoForward);

    public Task GoBackAsync()
    {
        RecordPlaceNow();
        return GoToAsync(_history.Back(IsThere));
    }

    public Task GoForwardAsync()
    {
        RecordPlaceNow();
        return GoToAsync(_history.Forward(IsThere));
    }

    private NavigationPlace? CurrentPlace() =>
        _selectedSidebarItem is { IsSelectable: true } item
            ? NavigationPlace.Of(item, SelectedSubItem, Browser.ExpandedAlbumKey)
            : null;

    private bool IsThere(NavigationPlace place) => _sidebarItems.Any(place.IsRow);

    // Once whatever moved the window has finished moving it: a jump to an
    // artist picks Artists, then whoever it remembered, then the one asked for,
    // and only the last is a page visited.
    private void ScheduleRecordPlace()
    {
        if (_recordPlacePending)
            return;
        _recordPlacePending = true;
        Dispatcher.UIThread.Post(RecordPlaceNow);
    }

    private void RecordPlaceNow()
    {
        if (!_recordPlacePending)
            return;
        _recordPlacePending = false;
        if (CurrentPlace() is { } place)
            _history.Visit(place);
        NotifyHistoryChanged();
    }

    // An album opened or closed in the grid belongs to the page it is on. One
    // closed because the page is being left is left to RecordPlaceNow, which
    // has not yet recorded the next page.
    private void RecordOpenAlbum()
    {
        if (_recordPlacePending || !IsShowingAlbumGrid || CurrentPlace() is not { } place)
            return;
        _history.Visit(place);
    }

    private async Task GoToAsync(NavigationPlace? place)
    {
        NotifyHistoryChanged();
        if (place is null || _sidebarItems.FirstOrDefault(place.IsRow) is not { } item)
            return;

        if (place.Artist is { } artist)
            await ShowArtistAsync(artist);
        else if (place.Album is { } album)
            await ShowAlbumAsync(tiles => tiles.FirstOrDefault(t => t.Key == album));
        else
            SelectedSidebarItem = item;
    }

    private void NotifyHistoryChanged()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        _goBackCommand?.NotifyCanExecuteChanged();
        _goForwardCommand?.NotifyCanExecuteChanged();
    }
}
