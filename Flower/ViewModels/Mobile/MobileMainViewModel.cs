using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

using Avalonia.Threading;

using CommunityToolkit.Mvvm.Input;

using Material.Icons;

using Flower.Models;
using Flower.Services;

namespace Flower.ViewModels.Mobile;

// RecentlyAdded is first/default (see MobileMainViewModel's _selectedTab) - an
// album grid ordered by recency, the app's home screen. The middle four mirror
// desktop's Songs/Albums/Artists/Playlists sidebar sections. Search is mobile-only
// (desktop has no equivalent standalone tab - its search box works over whichever
// sidebar section is already selected) - see IsShowingSearchPrompt/CanSearch for
// how it differs from the Songs tab's own toggleable search box.
public enum MobileTab { RecentlyAdded, Songs, Albums, Artists, Playlists, Search }

// Full-screen overlays shown on top of the tab content, e.g. the expanded
// now-playing view opened by tapping the mini-player.
public enum MobileSheet { None, NowPlaying, TrackActions, TrackInfo, AddToPlaylist, Settings, PeerApproval, ConfirmPairServer, ConfirmDeleteFile }

// Translates the desktop MainViewModel's sidebar+sublist (side-by-side master-detail)
// navigation model into tab+drill-down navigation for a phone screen, without changing
// MainViewModel itself. Songs is a flat list; Albums/Artists/Playlists show a picker
// (album/artist names, or playlist entries) until the user taps into one.
public class MobileMainViewModel : ViewModelBase
{
    public MainViewModel Main { get; }
    public PlaylistControlViewModel PlaylistControl { get; }
    public CurrentlyPlayingControlViewModel CurrentlyPlaying { get; }

    public ObservableCollection<SidebarItem> PlaylistPickerItems { get; } = new();

    // Rows of two tiles rather than a flat tile list - see AlbumGridRow for why
    // (virtualization).
    public ObservableCollection<AlbumGridRow> RecentlyAddedAlbumRows { get; } = new();
    public ObservableCollection<AlbumGridRow> AlbumGridRows { get; } = new();

    // One artist's own albums (Artists tab, one level in - see IsShowingArtistAlbumGrid),
    // rebuilt by RebuildArtistAlbumGrid whenever _selectedArtistName changes or the
    // library updates while it's set.
    public ObservableCollection<AlbumGridRow> ArtistAlbumGridRows { get; } = new();

    // Search tab results - see RebuildSearchResultsAsync. Albums matching by
    // Album name, Artists matching by Artists (the same raw per-track field
    // the Artists tab's own picker groups by - see
    // MainViewModel.RebuildSubListItems), Songs mirroring (a capped slice of)
    // Main.Rows. All three capped at MaxSearchResultsPerSection: unlike the
    // Songs tab's own TrackListBox (a real virtualized ListBox), this whole
    // results view is a plain ScrollViewer+ItemsControl per section (mixing
    // three different item shapes under one scroll, which a single
    // virtualizing list can't do) - realizing thousands of un-virtualized
    // track rows for a broad one-letter query froze the UI in practice.
    private const int MaxSearchResultsPerSection = 40;
    public ObservableCollection<AlbumTileViewModel> SearchAlbumResults { get; } = new();
    public ObservableCollection<string> SearchArtistResults { get; } = new();
    public ObservableCollection<TrackRowViewModel> SearchSongResults { get; } = new();

    // True if any section actually had more matches than the cap - drives a
    // single "refine your search" caption rather than a separate one per
    // section, since usually either none or all of them are far over the cap
    // together (a broad query matches lots of everything at once).
    private bool _hasMoreSearchResults;
    public bool HasMoreSearchResults
    {
        get => _hasMoreSearchResults;
        private set { if (_hasMoreSearchResults != value) { _hasMoreSearchResults = value; OnPropertyChanged(); } }
    }

    public ICommand SelectTabCommand { get; }
    public ICommand SelectAlbumOrArtistCommand { get; }
    public ICommand SelectArtistCommand { get; }
    public ICommand SelectSearchAlbumCommand { get; }
    public ICommand SelectSearchArtistCommand { get; }
    public ICommand SelectArtistAlbumCommand { get; }
    public ICommand SelectRecentlyAddedAlbumCommand { get; }
    public ICommand SelectPlaylistCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand PlayTrackCommand { get; }
    public ICommand ToggleMiniPlayerCommand { get; }
    public ICommand OpenNowPlayingCommand { get; }
    public ICommand GoToCurrentlyPlayingAlbumCommand { get; }
    public ICommand ToggleRepeatCommand { get; }
    public ICommand ToggleShuffleCommand { get; }
    public ICommand CloseSheetCommand { get; }
    public ICommand NextTrackCommand { get; }
    public ICommand PreviousTrackCommand { get; }
    public ICommand OpenTrackActionsCommand { get; }
    public ICommand ViewTrackInfoCommand { get; }
    public ICommand ToggleSearchCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand ClearSearchQueryCommand { get; }
    public ICommand ClearSongsFilterTextCommand { get; }
    public ICommand OpenAddToPlaylistCommand { get; }
    public ICommand AddTrackToPlaylistCommand { get; }
    public ICommand CreatePlaylistCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand RescanCommand { get; }
    public ICommand OpenAppSettingsCommand { get; }
    public ICommand AllowPeerCommand { get; }
    public ICommand DenyPeerCommand { get; }
    public ICommand DownloadTrackCommand { get; }
    public ICommand DeleteDownloadedFileCommand { get; }
    public ICommand ConfirmDeleteFileCommand { get; }
    public ICommand CancelDeleteFileCommand { get; }
    public ICommand DownloadAllVisibleCommand { get; }
    public ICommand PairWithServerCommand { get; }
    public ICommand UnpairServerCommand { get; }
    public ICommand ConfirmPairServerCommand { get; }
    public ICommand CancelPairServerCommand { get; }
    public ICommand ForceSyncCommand { get; }

    // Real back-history: every "navigate forward" action (a tab-bar tap, a
    // picker tile tap, a drill-in) pushes a frame here capturing exactly the
    // state it was called from, before applying its own change. GoBack/
    // SwipeBack just pop and restore the most recent one - unlike the old
    // algorithmic unwind (SelectedTab-- / clear _hasDrilledIn), this
    // correctly retraces compound jumps too, e.g. tapping an artist search
    // result switches tab AND drills in as one step, and back undoes both at
    // once, landing back on Search - the old approach had no way to know
    // "before this jump, I was on a completely different tab."
    //
    // Plain data (MobileNavigationFrame) rather than the closures this used
    // to be - ScreenStackPanel needs to inspect what screen the entry one
    // back in history represents (PeekOneBack) so it can keep that screen's
    // control alive and ready to reveal, which a closure can't expose.
    private readonly Stack<MobileNavigationFrame> _navigationHistory = new();

    // Browser-style redo stack: GoBack pushes the screen it just left here
    // (see GoBack/GoForward, which share ApplyFrame) so a swipe/tap the
    // other way can restore exactly what Back just undid, the same way a
    // real back/forward pair works. Cleared by PushHistory - once the user
    // takes any genuinely NEW navigation action (rather than a Back/Forward
    // replay), whatever used to be "ahead" of them is no longer reachable by
    // going forward, matching ordinary browser history semantics.
    private readonly Stack<MobileNavigationFrame> _forwardHistory = new();

    // Full snapshot of the screen being left, including FrozenRows/
    // FrozenHeader - see PushHistory/GoBack, the only two callers.
    private MobileNavigationFrame BuildLeavingFrame() => new(
        _selectedTab,
        _hasDrilledIn,
        _selectedArtistName,
        _hasDrilledIntoArtistAlbum,
        Main.SelectedSidebarItem,
        Main.SelectedSubItem,
        _searchQuery,
        // Only captured leaving a track-list screen - see
        // MobileNavigationFrame's own doc comment for why a kept-alive
        // one-back TrackListScreenView needs these instead of the live,
        // wholesale-replaced Main.Rows/CurrentAlbumHeader.
        IsShowingTrackList ? Main.Rows.ToList() : null,
        IsShowingAlbumTrackList ? CurrentAlbumHeader : null);

    // Called BEFORE any state field mutates, both here and in GoBack - not
    // just before RaiseNavigationChanged's much-later resync. The gap
    // between "leaving this screen" and "the destination is fully ready"
    // can be a real await (Main.RebuildRowsImmediatelyAsync), during which
    // Main.SelectedSidebarItem/SelectedSubItem/etc. already reflect the
    // DESTINATION while Main.Rows still holds the OUTGOING screen's tracks -
    // a still-observing TrackListScreenView (see ObserveLive) would recompute
    // against that inconsistent half-updated state and flash the wrong
    // thing (confirmed on device: the album header disappearing to show a
    // stale flat list for a couple of seconds after Back). Firing this
    // immediately, before any mutation, lets ScreenStackPanel freeze the
    // outgoing screen's control in place (its subscription detached) so it
    // keeps showing exactly what the user last saw, unchanged, until the
    // destination is actually ready to replace it - see
    // ScreenStackPanel's own subscription to this event.
    public event EventHandler<MobileNavigationFrame>? NavigationLeaving;

    private void PushHistory()
    {
        var frame = BuildLeavingFrame();
        NavigationLeaving?.Invoke(this, frame);
        _navigationHistory.Push(frame);
        _forwardHistory.Clear();
    }

    // The live screen, expressed as the same MobileNavigationFrame shape
    // history entries use - same Classify/ScopeKey logic drives both, so
    // ScreenStackPanel has exactly one notion of "what screen is this",
    // never two that could drift apart. Deliberately leaves FrozenRows/
    // FrozenHeader null (unlike PushHistory's snapshot) - this is read on
    // every navigation-change resync just to classify/scope the CURRENT
    // control, which tracks the live VM directly (see
    // TrackListScreenView.ObserveLive), so paying for a full Main.Rows copy
    // here would just be discarded work every single time.
    public MobileNavigationFrame CurrentFrame => new(
        _selectedTab,
        _hasDrilledIn,
        _selectedArtistName,
        _hasDrilledIntoArtistAlbum,
        Main.SelectedSidebarItem,
        Main.SelectedSubItem,
        _searchQuery,
        null,
        null);

    // What ScreenStackPanel should keep materialized underneath the current
    // screen, ready to reveal - null if there's nowhere to go back to.
    public MobileNavigationFrame? PeekOneBack => _navigationHistory.Count > 0 ? _navigationHistory.Peek() : null;

    // Symmetric to PeekOneBack, for a leftward (forward/redo) swipe - null
    // if there's nothing to redo (either nothing was ever undone, or a
    // newer navigation since then already cleared it - see PushHistory).
    public MobileNavigationFrame? PeekOneForward => _forwardHistory.Count > 0 ? _forwardHistory.Peek() : null;

    // Fired at the end of RaiseNavigationChanged - the single sync point
    // ScreenStackPanel hooks to know when to re-materialize/re-order its
    // current/one-back children. A plain event (not a Control reference)
    // keeps this ViewModel from needing to know Views/Controls exist.
    public event EventHandler? NavigationChanged;

    private MobileTab _selectedTab = MobileTab.RecentlyAdded;
    public MobileTab SelectedTab
    {
        get => _selectedTab;
        private set
        {
            if (_selectedTab == value)
                return;
            PushHistory();
            SetSelectedTabCore(value);
        }
    }

    // The actual state mutation, split out from the public setter above so
    // compound jumps (SelectSearchAlbumCommand/SelectSearchArtistCommand)
    // can push exactly one history entry for the whole jump rather than one
    // for the tab switch and a second for the drill-in.
    private void SetSelectedTabCore(MobileTab value)
    {
        _selectedTab = value;
        _hasDrilledIn = false;
        _selectedArtistName = null;
        _hasDrilledIntoArtistAlbum = false;
        ApplyTabSelection();
        OnPropertyChanged(nameof(SelectedTab));
        // SearchQuery deliberately survives leaving the Search tab (unlike
        // Songs' own toggleable box, which does clear on hide - see
        // IsSearchVisible) - the query and its results should still be there
        // whenever the user comes back to Search, whether via the tab bar or
        // Back/swipe-back, not reset to a blank prompt every time.
        RaiseNavigationChanged();
    }

    // Whether the user has tapped into a specific album/artist/playlist from the
    // picker. MainViewModel auto-selects a sub-item (last-used or first-alphabetical)
    // as soon as the Albums/Artists sidebar item is selected, so this can't be derived
    // from Main.SelectedSubItem being non-null — it's tracked here instead.
    private bool _hasDrilledIn;

    // Artists gets an extra level Albums/Playlists/RecentlyAdded don't: name
    // picker -> that artist's own album grid -> one album's tracks, rather than
    // straight from the name picker into every song by that artist as one flat
    // list. _selectedArtistName is non-null for both of the latter two screens;
    // _hasDrilledIntoArtistAlbum distinguishes which of them.
    private string? _selectedArtistName;
    private bool _hasDrilledIntoArtistAlbum;

    // Albums gets its own art-tile grid (same presentation as Recently Added,
    // see AlbumGridBuilder); Artists stays a plain name list - there is no
    // single representative image for an artist the way there is for an album.
    public bool IsShowingAlbumGrid => SelectedTab == MobileTab.Albums && !_hasDrilledIn;
    public bool IsShowingArtistPicker => SelectedTab == MobileTab.Artists && !_hasDrilledIn;
    public bool IsShowingArtistAlbumGrid => SelectedTab == MobileTab.Artists && _hasDrilledIn && !_hasDrilledIntoArtistAlbum;
    public bool IsShowingPlaylistPicker => SelectedTab == MobileTab.Playlists && !_hasDrilledIn;
    public bool IsShowingRecentlyAddedAlbums => SelectedTab == MobileTab.RecentlyAdded && !_hasDrilledIn;

    // The Search tab's own query - deliberately its own field, not
    // Main.FilterText (which the Songs tab's own toggleable search box uses
    // to filter Main.Rows in place). Search is a one-off lookup across the
    // whole library, surfaced in its own results view (SearchAlbumResults/
    // SearchArtistResults/SearchSongResults below) - it was never meant to
    // act as a filter that follows the user to other tabs. Sharing
    // Main.FilterText used to do exactly that: ApplyTabSelection pointed the
    // Search tab at the same "Songs" scope Main.Rows uses, so a query typed
    // here stayed live in Main.FilterText and kept narrowing the Songs tab's
    // own list for a moment (or, in the worst case, until something else
    // happened to clear it) after switching tabs.
    private string? _searchQuery;
    public string? SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (_searchQuery == value)
                return;
            _searchQuery = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsShowingSearchPrompt));
            OnPropertyChanged(nameof(IsShowingSearchResults));
            ScheduleSearchResultsRebuild();
        }
    }

    // Shown instead of the track list on the Search tab until something is
    // actually typed - unlike the Songs tab (which lists the whole library by
    // default and search only narrows it), a dedicated Search tab should not
    // dump every track in the library the moment it opens.
    public bool IsShowingSearchPrompt => SelectedTab == MobileTab.Search && string.IsNullOrEmpty(SearchQuery);

    // The Search tab's own results view (Songs/Albums/Artists sections - see
    // RebuildSearchResults) once something is typed - a separate layout from
    // IsShowingTrackList's plain flat list, so Search is fully excluded from
    // that one rather than just gated by IsShowingSearchPrompt.
    public bool IsShowingSearchResults => SelectedTab == MobileTab.Search && !string.IsNullOrEmpty(SearchQuery);

    public bool IsShowingTrackList =>
        !IsShowingAlbumGrid && !IsShowingArtistPicker && !IsShowingArtistAlbumGrid
        && !IsShowingPlaylistPicker && !IsShowingRecentlyAddedAlbums && SelectedTab != MobileTab.Search;
    public bool CanGoBack => _navigationHistory.Count > 0;
    public bool CanGoForward => _forwardHistory.Count > 0;

    // The track list is showing one specific album's songs - true whether
    // reached via the Albums tab's own grid, an artist's album grid
    // (SelectArtistAlbum), or a Recently Added tile (SelectRecentlyAddedAlbum) -
    // all three re-point Main.SelectedSidebarItem at the Albums sidebar item
    // and Main.SelectedSubItem at the album name, the same way Albums' own
    // in-place drill-in does. Drives the album header (CurrentAlbumHeader)
    // above the list and hides each row's own small art (see TrackRowTemplate) -
    // redundant once a big header already shows it once.
    public bool IsShowingAlbumTrackList =>
        IsShowingTrackList && Main.SelectedSidebarItem?.Kind == SidebarItemKind.Albums && Main.SelectedSubItem != null;

    // Bound by the album-drilled-into view's own ItemsControl instead of
    // Main.Rows directly (see MobileMainView.axaml). That ItemsControl stays
    // in the visual tree - just IsVisible="False" - while Songs mode is
    // active too, and an ItemsControl's item-container generation is not
    // gated on IsVisible the way layout/rendering is, so binding it straight
    // to Main.Rows meant it kept trying to realize the WHOLE library's rows
    // in the background every time Main.Rows changed, even while completely
    // hidden - confirmed as the cause of the app feeling slow to navigate
    // generally, not just when viewing an album. Empty whenever this is not
    // the active view, so there is nothing for it to do.
    public IReadOnlyList<TrackRowViewModel> AlbumDetailRows =>
        IsShowingAlbumTrackList ? Main.Rows : Array.Empty<TrackRowViewModel>();

    // Art/name/artist/year for IsShowingAlbumTrackList's header - reuses
    // AlbumTileViewModel (the same shape the Albums/Recently Added grids'
    // tiles already are) rather than a bespoke type, so its lazy AlbumArt
    // loading (see AlbumTileViewModel.LoadArtAsync) works identically. Cached
    // by album name rather than rebuilt on every access - a fresh
    // AlbumTileViewModel instance on every binding pass would restart art
    // loading each time and could never settle.
    private string? _currentAlbumHeaderName;
    private AlbumTileViewModel? _currentAlbumHeader;
    public AlbumTileViewModel? CurrentAlbumHeader
    {
        get
        {
            var albumName = IsShowingAlbumTrackList ? Main.SelectedSubItem : null;
            if (albumName != _currentAlbumHeaderName)
            {
                _currentAlbumHeaderName = albumName;
                _currentAlbumHeader = albumName != null ? BuildAlbumHeader(albumName) : null;
            }
            return _currentAlbumHeader;
        }
    }

    private AlbumTileViewModel? BuildAlbumHeader(string albumName)
    {
        var tracks = Main.Library.Tracks.Where(t => t.Album == albumName).ToList();
        if (tracks.Count == 0)
            return null;

        var representative = tracks.OrderByDescending(t => t.DateAdded).First();
        // Same "Various Artists" fallback as AlbumGridBuilder - a header
        // naming one arbitrary artist out of several would be misleading.
        var artists = tracks.Select(t => t.EffectiveAlbumArtist).Distinct().ToList();
        var artist = artists.Count == 1 ? artists[0] : "Various Artists";

        return new AlbumTileViewModel
        {
            Name = albumName,
            Artist = artist,
            RepresentativeTrack = representative,
            MostRecentlyAdded = tracks.Max(t => t.DateAdded),
        };
    }

    // True while DownloadAllVisibleCommand is working through a batch - drives
    // the top bar's download-all icon swapping to a spinner and disabling
    // itself against a second overlapping run (see MobileMainView.axaml).
    private bool _isBulkDownloading;
    public bool IsBulkDownloading
    {
        get => _isBulkDownloading;
        private set { if (_isBulkDownloading != value) { _isBulkDownloading = value; OnPropertyChanged(); } }
    }

    // A download over the local LAN can finish in well under a UI frame, in
    // which case IsDownloading would flip true then straight back to false
    // before anything ever actually painted it - observed in practice as the
    // spinner "sometimes not even appearing". Holding it visible for at
    // least this long guarantees it's actually seen, at the cost of a barely
    // perceptible artificial delay on an already-fast download.
    private static readonly TimeSpan MinDownloadSpinnerDuration = TimeSpan.FromMilliseconds(400);

    // How many of DownloadAllVisibleCommand's downloads run at once.
    private const int MaxConcurrentDownloads = 3;

    // Re-anchors Next/Previous/auto-advance to whatever's actually on screen
    // right now, mirroring desktop's MainViewModel.SyncPlayQueueToCurrentView -
    // called immediately before every PlayTrackCommand invocation. Search's
    // own song section (SearchSongResults) is a separate mirror of Main.Rows,
    // not Main.Rows itself (see that collection's own doc comment), so it
    // needs its own branch here; every other screen (Songs, an album/
    // artist-album/Recently-Added drill-in, or a playlist) renders straight
    // from Main.Rows, whatever MainViewModel already narrowed it to.
    private void SyncPlayQueueToCurrentView()
    {
        var tracks = IsShowingSearchResults
            ? SearchSongResults.Select(r => r.Track).ToList()
            : Main.Rows.Select(r => r.Track).ToList();
        PlaylistControl.SetCurrentPlaylist(new Playlist("Now Playing Queue", tracks));
    }

    // Shared by DownloadTrackCommand (one row) and DownloadAllVisibleCommand
    // (every not-yet-downloaded row in view) - same per-row idle/in-flight/
    // unavailable state either way, so a row started via the bulk action looks
    // identical to one started by tapping its own download icon directly.
    private async Task DownloadRowAsync(TrackRowViewModel row)
    {
        row.IsDownloadUnavailable = false;
        row.IsDownloading = true;
        var started = DateTime.UtcNow;
        var result = await Main.DownloadTrackAsync(row.Track);
        var remaining = MinDownloadSpinnerDuration - (DateTime.UtcNow - started);
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining);
        row.IsDownloading = false;
        row.IsDownloadUnavailable = result is TrackDownloadResult.PeerUnavailable or TrackDownloadResult.Failed;
    }

    // The Search tab's box is always visible (no toggle needed, unlike the
    // Songs tab's - see CanSearch), so it and the screen title are mutually
    // exclusive on that basis alone, independent of IsSearchVisible. Two
    // separate boxes exist in MobileMainView.axaml (this is just "is either
    // one showing") - IsShowingSongsSearchBox/IsShowingSearchTabBox below
    // pick which physical control is visible and which field it's bound to.
    public bool IsShowingSearchBox => IsSearchVisible || SelectedTab == MobileTab.Search;
    public bool IsShowingScreenTitle => !IsShowingSearchBox;

    // The Songs tab's own toggleable box, bound to Main.FilterText.
    public bool IsShowingSongsSearchBox => IsSearchVisible;

    // The Search tab's always-visible box, bound to SearchQuery - kept as a
    // physically separate TextBox from the one above specifically so the two
    // fields backing them can never be confused for one another again.
    public bool IsShowingSearchTabBox => SelectedTab == MobileTab.Search;

    public string ScreenTitle
    {
        get
        {
            // Both artist sub-screens (that artist's own album grid, and one of
            // its albums' tracks) show the artist's name, not Main.SelectedSidebarItem's
            // ("Artists" while browsing the grid, "Albums" once SelectArtistAlbum
            // has re-pointed it there - see that method) - _selectedArtistName is
            // the one thing that's actually stable and meaningful across both.
            if (SelectedTab == MobileTab.Artists && _selectedArtistName != null)
                return _selectedArtistName;

            return _hasDrilledIn
                ? (Main.SelectedSidebarItem?.Name ?? SelectedTab.ToString())
                : SelectedTab == MobileTab.RecentlyAdded ? "Recently Added" : SelectedTab.ToString();
        }
    }

    // Non-null only while drilled into a specific playlist's track list, which is the
    // one place mobile allows reordering (Songs/Albums/Artists have no persisted order).
    public Playlist? CurrentPlaylist =>
        SelectedTab == MobileTab.Playlists && _hasDrilledIn ? Main.SelectedSidebarItem?.Playlist : null;

    public bool IsShowingPlaylistTracks => CurrentPlaylist != null;

    // The toggleable search box only makes sense over a track list (Main.FilterText
    // filters Rows, which the Albums/Artists/Playlists picker screens do not use) -
    // IsShowingTrackList already excludes the Search tab itself, whose box is
    // always visible already (see IsShowingSearchBox), with nothing to toggle.
    public bool CanSearch => IsShowingTrackList;

    private bool _isSearchVisible;
    public bool IsSearchVisible
    {
        get => _isSearchVisible;
        private set
        {
            if (_isSearchVisible == value)
                return;
            _isSearchVisible = value;
            if (!value)
                Main.FilterText = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsShowingSearchBox));
            OnPropertyChanged(nameof(IsShowingSongsSearchBox));
            OnPropertyChanged(nameof(IsShowingScreenTitle));
        }
    }

    private MobileSheet _activeSheet = MobileSheet.None;
    public MobileSheet ActiveSheet
    {
        get => _activeSheet;
        private set
        {
            if (_activeSheet == value)
                return;
            _activeSheet = value;
            if (value == MobileSheet.None)
                ActionTarget = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsShowingNowPlaying));
            OnPropertyChanged(nameof(IsShowingTrackActions));
            OnPropertyChanged(nameof(IsShowingTrackInfo));
            OnPropertyChanged(nameof(IsShowingAddToPlaylist));
            OnPropertyChanged(nameof(IsShowingSettings));
            OnPropertyChanged(nameof(IsShowingPeerApproval));
            OnPropertyChanged(nameof(IsShowingConfirmPairServer));
            OnPropertyChanged(nameof(IsShowingConfirmDeleteFile));
            // Retry a server-pairing offer that arrived while some other
            // sheet was up (see Main.ServerDiscoveredForPairing's
            // subscription below) rather than dropping it - reentrant, but
            // safe: _activeSheet is already committed to None above by the
            // time this runs, so the nested ActiveSheet = ConfirmPairServer
            // call below sees a real change and proceeds normally.
            if (value == MobileSheet.None)
                TryShowPendingServerOffer();
        }
    }

    private void TryShowPendingServerOffer()
    {
        if (_pendingServerOffer is not { } device)
            return;
        _pendingServerOffer = null;
        _pendingServerToPair = device;
        OnPropertyChanged(nameof(PendingServerToPairAlias));
        OnPropertyChanged(nameof(ConfirmPairServerTitle));
        OnPropertyChanged(nameof(ConfirmPairServerMessage));
        ActiveSheet = MobileSheet.ConfirmPairServer;
    }

    public bool IsShowingNowPlaying => ActiveSheet == MobileSheet.NowPlaying;
    public bool IsShowingTrackActions => ActiveSheet == MobileSheet.TrackActions;
    public bool IsShowingTrackInfo => ActiveSheet == MobileSheet.TrackInfo;
    public bool IsShowingAddToPlaylist => ActiveSheet == MobileSheet.AddToPlaylist;
    public bool IsShowingSettings => ActiveSheet == MobileSheet.Settings;
    public bool IsShowingPeerApproval => ActiveSheet == MobileSheet.PeerApproval;
    public bool IsShowingConfirmPairServer => ActiveSheet == MobileSheet.ConfirmPairServer;
    public bool IsShowingConfirmDeleteFile => ActiveSheet == MobileSheet.ConfirmDeleteFile;

    // Set when Main.PeerApprovalRequested fires (see SyncHttpServer's trust gate,
    // SYNC-PLAN.md Phase 3) and cleared once Allow/Deny resolves it - see
    // ResolvePeerApproval. Without a subscriber here, MainViewModel's own fallback
    // denies every peer outright (fails closed, matching desktop's behavior when no
    // MainView is attached), which - since mobile never had a MainView to begin
    // with - would otherwise silently block every sync request.
    private PeerApprovalRequestedEventArgs? _pendingPeerApproval;
    public string PeerApprovalAlias => _pendingPeerApproval?.Alias ?? "";
    public string PeerApprovalMessage =>
        $"\"{PeerApprovalAlias}\" wants to sync playlists and library data with this device. Only allow devices you recognize - it will not be asked again.";

    // Set by PairWithServerCommand (Settings' server list) before switching to
    // the ConfirmPairServer sheet, cleared once ConfirmPairServerCommand/
    // CancelPairServerCommand resolves it - see those. Desktop's equivalent is
    // ServerPickerView's own ConfirmDialogWindow prompt; mobile had no
    // confirmation at all here previously (SettingsView's Sync button called
    // straight through to Main.PairWithServer).
    private DiscoveredDevice? _pendingServerToPair;
    public string PendingServerToPairAlias => _pendingServerToPair?.Alias ?? "";
    public string ConfirmPairServerTitle => $"Pair With \"{PendingServerToPairAlias}\"?";
    public string ConfirmPairServerMessage =>
        $"This device's library view will be replaced by \"{PendingServerToPairAlias}\"'s - your Songs/Albums list will show its library instead of managing its own. Your existing music files on this device will not be deleted.";

    // Set when Main.ServerDiscoveredForPairing fires while some other sheet
    // is already up - see the subscription below and TryShowPendingServerOffer,
    // which shows it as soon as the user goes idle (ActiveSheet returns to
    // None) instead of the offer being silently dropped for the rest of the
    // session. At most one offer is remembered at a time - a second Server
    // discovered while still busy simply replaces it.
    private DiscoveredDevice? _pendingServerOffer;

    // Android's media-access permission can be permanently denied, in which case the
    // only way back in is the system app-settings screen; desktop/iOS have nothing
    // equivalent to check (PlatformPermissions.Current is left null there).
    public bool HasMediaPermissionPrompt => PlatformPermissions.Current != null;
    public bool HasMediaPermission => PlatformPermissions.Current?.IsGranted() ?? true;

    // The track a row's "..." action menu (and, in turn, the Track Info sheet) applies to.
    private Track? _actionTarget;
    public Track? ActionTarget
    {
        get => _actionTarget;
        private set
        {
            _actionTarget = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanDeleteDownloadedFile));
            OnPropertyChanged(nameof(IsRecoverableDownload));
            OnPropertyChanged(nameof(ConfirmDeleteFileTitle));
            OnPropertyChanged(nameof(ConfirmDeleteFileMessage));
        }
    }

    // Any real local file can be deleted to free up space - a still-
    // undownloaded placeholder has no file yet to delete. Whether it's safe
    // to get back later (see IsRecoverableDownload) is surfaced as a warning
    // in the confirm sheet (ConfirmDeleteFileMessage) rather than gating this
    // outright - a track this device only has a copy of because the user
    // imported it directly (or a peer that once had it is no longer the
    // paired Server) should still be deletable, just with eyes open.
    public bool CanDeleteDownloadedFile => ActionTarget?.Path != null;

    // Whether ActionTarget's file, once deleted, would actually come back on
    // its own - true only if the currently paired Server is the same device
    // that last reported having a copy (OriginDeviceFingerprint). Null (no
    // peer has ever reported holding this exact track - e.g. purely a local
    // import) or a fingerprint that isn't the current pairing (a track from
    // before a Server switch, or synced via ad-hoc peer browsing rather than
    // bulk sync) both mean the same thing here: nothing will resync it back.
    public bool IsRecoverableDownload =>
        ActionTarget?.OriginDeviceFingerprint != null &&
        ActionTarget.OriginDeviceFingerprint == Main.PairedServerFingerprint;

    public string ConfirmDeleteFileTitle => $"Delete \"{ActionTarget?.Title}\"?";

    public string ConfirmDeleteFileMessage => IsRecoverableDownload
        ? "This removes the downloaded copy from this device. Your paired server still has it, so you can download it again later."
        : "Your currently paired server doesn't have this file, so it won't be synced back automatically. Deleting it now will remove your only copy.";

    // Whichever list is currently on screen (picker or track list) has nothing in it.
    // Without this, an empty library or an empty search just renders a blank screen.
    // IsShowingSearchPrompt counts as "empty" too - the Search tab before anything is
    // typed has an empty Main.Rows (see ApplyTabSelection/IsShowingSearchPrompt), but
    // wants its own prompt rather than falling through to "No Results".
    public bool HasSearchAlbumResults => SearchAlbumResults.Count > 0;
    public bool HasSearchArtistResults => SearchArtistResults.Count > 0;
    public bool HasSearchSongResults => SearchSongResults.Count > 0;

    public bool IsContentEmpty =>
        (IsShowingAlbumGrid && AlbumGridRows.Count == 0) ||
        (IsShowingArtistPicker && Main.SubListItems.Count == 0) ||
        (IsShowingArtistAlbumGrid && ArtistAlbumGridRows.Count == 0) ||
        (IsShowingPlaylistPicker && PlaylistPickerItems.Count == 0) ||
        (IsShowingRecentlyAddedAlbums && RecentlyAddedAlbumRows.Count == 0) ||
        IsShowingSearchPrompt ||
        (IsShowingSearchResults && !HasSearchAlbumResults && !HasSearchArtistResults && !HasSearchSongResults) ||
        (IsShowingTrackList && Main.Rows.Count == 0);

    public MaterialIconKind EmptyStateIcon => IsShowingSearchPrompt ? MaterialIconKind.Magnify : MaterialIconKind.MusicNoteOff;

    public string EmptyStateTitle
    {
        get
        {
            if (IsShowingSearchPrompt)
                return "Search Your Library";
            if (IsShowingPlaylistTracks)
                return "Playlist is Empty";
            if (IsShowingSearchResults || !string.IsNullOrEmpty(Main.FilterText))
                return "No Results";
            if (Main.Library.Tracks.Count == 0)
                return "No Music Yet";
            return "Nothing Here";
        }
    }

    public string EmptyStateMessage
    {
        get
        {
            if (IsShowingSearchPrompt)
                return "Find songs by title, artist, album, or genre.";
            if (IsShowingPlaylistTracks)
                return "Add tracks from a track's ... menu.";
            if (IsShowingSearchResults)
                return $"No matches for \"{SearchQuery}\".";
            if (!string.IsNullOrEmpty(Main.FilterText))
                return $"No matches for \"{Main.FilterText}\".";
            if (Main.Library.Tracks.Count > 0)
                return "Nothing to show here yet.";
            if (System.OperatingSystem.IsAndroid())
                return "Grant music access in Settings, then add songs to your device library.";
            if (System.OperatingSystem.IsIOS())
                return "Connect to a computer and drag music files into the Flower app in Finder.";
            return "Add a library folder in Settings to get started.";
        }
    }

    private void RaiseEmptyStateChanged()
    {
        OnPropertyChanged(nameof(IsContentEmpty));
        OnPropertyChanged(nameof(EmptyStateIcon));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateMessage));
    }

    private void RaiseSearchResultsChanged()
    {
        OnPropertyChanged(nameof(HasSearchAlbumResults));
        OnPropertyChanged(nameof(HasSearchArtistResults));
        OnPropertyChanged(nameof(HasSearchSongResults));
        RaiseEmptyStateChanged();
    }

    public MobileMainViewModel(
        MainViewModel main,
        PlaylistControlViewModel playlistControl,
        CurrentlyPlayingControlViewModel currentlyPlaying)
    {
        Main = main;
        PlaylistControl = playlistControl;
        CurrentlyPlaying = currentlyPlaying;

        PlaylistControl.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaylistControlViewModel.CurrentlyPlayingTrack) &&
                PlaylistControl.CurrentlyPlayingTrack == null)
                ActiveSheet = MobileSheet.None;
        };

        Main.PeerApprovalRequested += (_, e) =>
        {
            _pendingPeerApproval = e;
            OnPropertyChanged(nameof(PeerApprovalAlias));
            OnPropertyChanged(nameof(PeerApprovalMessage));
            ActiveSheet = MobileSheet.PeerApproval;
        };

        // Proactively offers pairing the moment a Server is found, instead of
        // only when the user happens to open Settings and look - see
        // MainViewModel.ServerDiscoveredForPairing's own doc comment. If some
        // other sheet is already up, this isn't shown immediately (unlike
        // PeerApproval above, this isn't a fail-closed security prompt with a
        // deny-by-default timeout, just a convenience offer - not worth
        // interrupting whatever the user is doing) but is remembered and
        // retried the moment the user goes idle - see TryShowPendingServerOffer.
        Main.ServerDiscoveredForPairing += (_, device) =>
        {
            if (ActiveSheet != MobileSheet.None)
            {
                _pendingServerOffer = device;
                return;
            }
            _pendingServerToPair = device;
            OnPropertyChanged(nameof(PendingServerToPairAlias));
            OnPropertyChanged(nameof(ConfirmPairServerTitle));
            OnPropertyChanged(nameof(ConfirmPairServerMessage));
            ActiveSheet = MobileSheet.ConfirmPairServer;
        };

        Main.SidebarItems.CollectionChanged += (_, _) => RebuildPlaylistPicker();
        Main.Library.TracksUpdated += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            RebuildRecentlyAddedAlbums();
            RebuildAlbumGrid();
            RebuildArtistAlbumGrid();
            if (SelectedTab == MobileTab.Search)
                RefreshSearchResultsNow();
            // Forces CurrentAlbumHeader to rebuild even though the album name
            // itself hasn't changed - a download/rescan can still change which
            // track is "most recently added" (representative art) or the
            // computed artist/year underneath it.
            _currentAlbumHeaderName = null;
            OnPropertyChanged(nameof(CurrentAlbumHeader));
        });
        Main.PropertyChanged += (_, e) =>
        {
            // Songs/Albums/Artists picker empty-states only - Search has its
            // own SearchQuery-driven path (see that property's setter) and no
            // longer touches Main.Rows/Main.FilterText at all.
            if (e.PropertyName is nameof(MainViewModel.Rows) or nameof(MainViewModel.SubListItems)
                or nameof(MainViewModel.FilterText))
                RaiseEmptyStateChanged();
        };
        RebuildPlaylistPicker();
        RebuildRecentlyAddedAlbums();
        RebuildAlbumGrid();
        RebuildArtistAlbumGrid();
        ApplyTabSelection();

        SelectTabCommand = new RelayCommand<string>(name =>
        {
            if (name != null && System.Enum.TryParse<MobileTab>(name, out var tab))
                SelectedTab = tab;
        });
        SelectAlbumOrArtistCommand = new RelayCommand<string>(SelectAlbumOrArtist);
        SelectArtistCommand = new RelayCommand<string>(SelectArtist);
        // Switch tab first (moves Main.SelectedSidebarItem to the right scope -
        // see ApplyTabSelection) then drill in exactly like that tab's own
        // picker would - same SelectAlbumOrArtist/SelectArtist a tap on the
        // Albums/Artists tab itself uses, just reached from Search instead.
        // One history entry for the whole "search -> album/artist" jump (see
        // PushHistory's own doc comment) - SetSelectedTabCore/*Core below are
        // the raw mutations SelectedTab's setter/SelectAlbumOrArtist/SelectArtist
        // themselves use, deliberately skipped here so this doesn't also push
        // a second entry just for the tab switch.
        SelectSearchAlbumCommand = new RelayCommand<string>(async name =>
        {
            if (name == null)
                return;
            PushHistory();
            SetSelectedTabCore(MobileTab.Albums);
            await SelectAlbumOrArtistCore(name);
        });
        SelectSearchArtistCommand = new RelayCommand<string>(name =>
        {
            if (name == null)
                return;
            PushHistory();
            SetSelectedTabCore(MobileTab.Artists);
            SelectArtistCore(name);
        });
        SelectArtistAlbumCommand = new RelayCommand<string>(SelectArtistAlbum);
        SelectRecentlyAddedAlbumCommand = new RelayCommand<string>(SelectRecentlyAddedAlbum);
        SelectPlaylistCommand = new RelayCommand<SidebarItem>(SelectPlaylist);
        BackCommand = new RelayCommand(async () => await GoBack());
        PlayTrackCommand = new RelayCommand<Track>(track =>
        {
            // Always starts this track, mirroring desktop's row-activation handler
            // (MainView.axaml.cs calls Play, not PlayOrPause, on Enter/double-click).
            // PlayOrPause ignores its track argument whenever something is already
            // playing, so reusing it here paused instead of switching tracks.
            if (track == null)
                return;
            // Desktop's own row-activation path (MainViewModel.PlayTrack) always
            // re-anchors the Next/Previous queue to whatever's currently on
            // screen before playing - this was missing here entirely, so
            // mobile's queue stayed pinned to Importer's raw filesystem-scan
            // order (whatever MainPlaylist last held) regardless of which
            // list (Songs/album/playlist/search) the tapped track actually
            // came from - confirmed on a real device as Next/Previous
            // advancing through what looked like an arbitrary/random order.
            SyncPlayQueueToCurrentView();
            if (track.Path != null)
            {
                PlaylistControl.Play(track);
                return;
            }

            // Path == null means not yet downloaded (see SYNC-PLAN.md Phase 3) -
            // stream it on demand from whichever peer currently holds it rather
            // than requiring an explicit download first (still available via the
            // row's own download icon/DownloadTrackCommand, for offline listening
            // later). A transient copy, not the placeholder itself - Path here is
            // a stream URL, not a real local file, and must never be persisted
            // back into Library.Tracks (see VlcAudioManager.Play's "://" check,
            // which already knows how to play any URL-shaped Path).
            var streamUrl = Main.GetStreamUrl(track);
            if (streamUrl != null)
                PlaylistControl.Play(track with { Path = streamUrl });
        });
        ToggleMiniPlayerCommand = new RelayCommand(() =>
        {
            if (PlaylistControl.CurrentlyPlayingTrack is { } track)
                PlaylistControl.PlayOrPause(track);
        });
        OpenNowPlayingCommand = new RelayCommand(() =>
        {
            if (PlaylistControl.CurrentlyPlayingTrack != null)
                ActiveSheet = MobileSheet.NowPlaying;
        });
        // Tapping the Now Playing sheet's album art - closes the sheet and
        // drills into that track's album, same tab-switch-plus-drill-in-as-
        // one-history-entry shape as SelectSearchAlbumCommand.
        GoToCurrentlyPlayingAlbumCommand = new RelayCommand(async () =>
        {
            var album = PlaylistControl.CurrentlyPlayingTrack?.Album;
            if (album == null)
                return;
            ActiveSheet = MobileSheet.None;
            PushHistory();
            SetSelectedTabCore(MobileTab.Albums);
            await SelectAlbumOrArtistCore(album);
        });
        ToggleRepeatCommand = new RelayCommand(CurrentlyPlaying.ToggleRepeat);
        ToggleShuffleCommand = new RelayCommand(CurrentlyPlaying.ToggleShuffle);
        CloseSheetCommand = new RelayCommand(() => ActiveSheet = MobileSheet.None);
        NextTrackCommand = new RelayCommand(PlaylistControl.Next);
        PreviousTrackCommand = new RelayCommand(PlaylistControl.Previous);
        OpenTrackActionsCommand = new RelayCommand<Track>(track =>
        {
            if (track == null)
                return;
            ActionTarget = track;
            ActiveSheet = MobileSheet.TrackActions;
        });
        ViewTrackInfoCommand = new RelayCommand(() =>
        {
            if (ActionTarget != null)
                ActiveSheet = MobileSheet.TrackInfo;
        });
        ToggleSearchCommand = new RelayCommand(() => IsSearchVisible = !IsSearchVisible);
        ClearSearchCommand = new RelayCommand(() => IsSearchVisible = false);
        // Search tab's own inline clear (the embedded "x" in SearchTabBox,
        // see MobileMainView.axaml) - unlike ClearSearchCommand above, there's
        // nothing to close here (the Search tab's box is always visible), so
        // this just empties the query and leaves the box focused.
        ClearSearchQueryCommand = new RelayCommand(() => SearchQuery = null);
        // Songs tab's own inline clear (the embedded "x" in SearchBox) - just
        // empties the text and keeps the box open/focused, unlike
        // ClearSearchCommand (the header's Close-icon button), which also
        // collapses the box back to icon-only.
        ClearSongsFilterTextCommand = new RelayCommand(() => Main.FilterText = null);
        OpenAddToPlaylistCommand = new RelayCommand(() =>
        {
            if (ActionTarget != null)
                ActiveSheet = MobileSheet.AddToPlaylist;
        });
        AddTrackToPlaylistCommand = new RelayCommand<SidebarItem>(async item =>
        {
            if (ActionTarget != null && item?.Playlist != null)
                await Main.AddTrackToPlaylist(ActionTarget, item.Playlist);
            ActiveSheet = MobileSheet.None;
        });
        CreatePlaylistCommand = new RelayCommand(async () =>
        {
            await Main.CreatePlaylistWithTrack(ActionTarget);
            ActiveSheet = MobileSheet.None;
        });
        OpenSettingsCommand = new RelayCommand(() =>
        {
            OnPropertyChanged(nameof(HasMediaPermission));
            ActiveSheet = MobileSheet.Settings;
        });
        RescanCommand = new RelayCommand(async () => await Main.RescanLibraryAsync());
        OpenAppSettingsCommand = new RelayCommand(() => PlatformPermissions.Current?.OpenAppSettings());
        AllowPeerCommand = new RelayCommand(() => ResolvePeerApproval(true));
        DenyPeerCommand = new RelayCommand(() => ResolvePeerApproval(false));
        DownloadTrackCommand = new RelayCommand<TrackRowViewModel>(async row =>
        {
            if (row == null || row.IsDownloading)
                return;
            await DownloadRowAsync(row);
        });
        // Opens the confirm sheet (ConfirmDeleteFileCommand/CancelDeleteFileCommand
        // below actually do the deleting) rather than deleting immediately -
        // this is a destructive, sometimes-unrecoverable action (see
        // IsRecoverableDownload/ConfirmDeleteFileMessage), so it always gets a
        // confirmation with an explicit warning first.
        DeleteDownloadedFileCommand = new RelayCommand(() =>
        {
            if (ActionTarget != null)
                ActiveSheet = MobileSheet.ConfirmDeleteFile;
        });
        ConfirmDeleteFileCommand = new RelayCommand(async () =>
        {
            if (ActionTarget is { } track)
                await Main.DeleteDownloadedFileAsync(track);
            ActiveSheet = MobileSheet.None;
        });
        CancelDeleteFileCommand = new RelayCommand(() => ActiveSheet = MobileSheet.None);
        // Downloads every not-yet-downloaded track currently in Main.Rows -
        // only ever invoked while viewing one album's or one playlist's tracks
        // (see IsShowingAlbumTrackList/IsShowingPlaylistTracks in
        // MobileMainView.axaml), so that's the scope this ends up covering;
        // Main.Rows is whatever MainViewModel already narrowed it to. Reuses
        // DownloadRowAsync so each row's own download icon still shows its
        // individual progress exactly like a manual single-track download.
        // Up to MaxConcurrentDownloads run at once (not all at once, and not
        // strictly one at a time) - a middle ground between "fast" and
        // "gentle on the peer being downloaded from".
        DownloadAllVisibleCommand = new RelayCommand(async () =>
        {
            if (IsBulkDownloading)
                return;
            IsBulkDownloading = true;
            try
            {
                // Captures SyncKeys, not TrackRowViewModel instances - a
                // download completing mid-batch fires Library.TracksUpdated,
                // which (via MainViewModel's own debounced ScheduleFilter)
                // eventually replaces Main.Rows wholesale with a fresh set of
                // TrackRowViewModel objects. Holding onto row objects from a
                // snapshot taken before that swap meant later iterations kept
                // mutating IsDownloading on orphaned rows nothing on screen
                // was bound to anymore - confirmed on a real device as the
                // spinner working for the first couple of songs in a batch,
                // then never appearing again. SyncKey (not the row/Track
                // object itself) is what survives the swap intact, so each
                // task re-resolves against whatever Main.Rows currently is
                // right before it actually starts downloading (after
                // acquiring a throttle slot, not when the task was created) -
                // safe to do without locking despite running "concurrently"
                // here, since every one of these tasks' own code (everything
                // except the actual HTTP I/O in flight) still only ever runs
                // on the UI thread, the same as the rest of this ViewModel.
                var syncKeysToDownload = Main.Rows
                    .Where(r => r.Track.Path == null)
                    .Select(r => r.Track.SyncKey)
                    .ToList();
                using var throttle = new SemaphoreSlim(MaxConcurrentDownloads);
                var tasks = syncKeysToDownload.Select(async syncKey =>
                {
                    await throttle.WaitAsync();
                    try
                    {
                        var row = Main.Rows.FirstOrDefault(r => r.Track.SyncKey == syncKey);
                        if (row == null || row.Track.Path != null || row.IsDownloading)
                            return;
                        await DownloadRowAsync(row);
                    }
                    finally
                    {
                        throttle.Release();
                    }
                });
                await Task.WhenAll(tasks);
            }
            finally
            {
                IsBulkDownloading = false;
            }
        });

        // Confirm-before-pairing (see ConfirmPairServerMessage) rather than
        // pairing immediately - matches desktop's ServerPickerView dialog.
        PairWithServerCommand = new RelayCommand<DiscoveredDevice>(device =>
        {
            if (device == null)
                return;
            _pendingServerToPair = device;
            OnPropertyChanged(nameof(PendingServerToPairAlias));
            OnPropertyChanged(nameof(ConfirmPairServerTitle));
            OnPropertyChanged(nameof(ConfirmPairServerMessage));
            ActiveSheet = MobileSheet.ConfirmPairServer;
        });
        UnpairServerCommand = new RelayCommand(Main.UnpairServer);
        ForceSyncCommand = new RelayCommand(Main.ForceSyncNow);
        ConfirmPairServerCommand = new RelayCommand(() =>
        {
            if (_pendingServerToPair is { } device)
                Main.PairWithServer(device);
            _pendingServerToPair = null;
            ActiveSheet = MobileSheet.None;
        });
        CancelPairServerCommand = new RelayCommand(() =>
        {
            _pendingServerToPair = null;
            ActiveSheet = MobileSheet.None;
        });
    }

    private void ResolvePeerApproval(bool allowed)
    {
        _pendingPeerApproval?.Resolution.TrySetResult(allowed);
        _pendingPeerApproval = null;
        ActiveSheet = MobileSheet.None;
    }

    private void RebuildPlaylistPicker()
    {
        PlaylistPickerItems.Clear();
        foreach (var item in Main.SidebarItems.Where(i => i.Kind == SidebarItemKind.Playlist))
            PlaylistPickerItems.Add(item);
        RaiseEmptyStateChanged();
    }

    private void RebuildRecentlyAddedAlbums()
    {
        RecentlyAddedAlbumRows.Clear();
        foreach (var row in AlbumGridRow.Chunk(RecentlyAddedAlbumsBuilder.Build(Main.Library.Tracks)))
            RecentlyAddedAlbumRows.Add(row);

        RaiseEmptyStateChanged();
    }

    private void RebuildAlbumGrid()
    {
        AlbumGridRows.Clear();
        foreach (var row in AlbumGridRow.Chunk(AlbumGridBuilder.Build(Main.Library.Tracks)))
            AlbumGridRows.Add(row);

        RaiseEmptyStateChanged();
    }

    // Every track by _selectedArtistName specifically - AlbumGridBuilder's own
    // Album-name-alone grouping (see its doc comment) is safe here despite not
    // also keying on Artist, since everything passed in already shares this one
    // artist. No-ops (clears down to empty) when nothing is selected, so a stale
    // grid never lingers if this fires while the picker itself is showing.
    private void RebuildArtistAlbumGrid()
    {
        ArtistAlbumGridRows.Clear();
        if (_selectedArtistName != null)
        {
            var tracks = Main.Library.Tracks.Where(t => t.Artists == _selectedArtistName);
            foreach (var row in AlbumGridRow.Chunk(AlbumGridBuilder.Build(tracks)))
                ArtistAlbumGridRows.Add(row);
        }

        RaiseEmptyStateChanged();
    }

    private CancellationTokenSource? _searchResultsCts;

    // Immediate (no debounce) - used when the Search tab is entered or the
    // library changes, neither of which happens once per keystroke, unlike
    // ScheduleSearchResultsRebuild below.
    private void RefreshSearchResultsNow()
    {
        _searchResultsCts?.Cancel();
        _searchResultsCts = new CancellationTokenSource();
        _ = RebuildSearchResultsAsync(_searchResultsCts.Token);
    }

    // Debounced, same 250ms/cancel-and-restart shape as MainViewModel.ScheduleFilter
    // for Main.Rows - calling RebuildSearchResultsAsync directly on every keystroke
    // discarded and rebuilt every matching album's AlbumTileViewModel from scratch
    // each time (including any art already mid-load - see AlbumTileViewModel.AlbumArt),
    // observed in practice as the app freezing while typing.
    private void ScheduleSearchResultsRebuild()
    {
        _searchResultsCts?.Cancel();
        _searchResultsCts = new CancellationTokenSource();
        _ = DebouncedRebuildSearchResultsAsync(_searchResultsCts.Token);
    }

    private async Task DebouncedRebuildSearchResultsAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(250, token);
        }
        catch (OperationCanceledException)
        {
            return; // A newer keystroke restarted the cooldown - that call's own delay will fire instead.
        }
        await RebuildSearchResultsAsync(token);
    }

    // Pre-cap match counts for Albums/Artists/Songs, so HasMoreSearchResults
    // can tell "over the cap" apart from "genuinely fewer than the cap" after
    // the collections themselves have already been truncated.
    private int _totalMatchingAlbums;
    private int _totalMatchingArtists;
    private int _totalMatchingSongs;

    // Search tab's Albums/Artists/Songs sections, all matched directly
    // against Main.Library.Tracks by SearchQuery - a self-contained scan, not
    // a mirror of MainViewModel's own Rows/FilterText pipeline (see
    // SearchQuery's own doc comment for why that coupling was removed).
    // Songs reuses TrackListBuilder.Build, the same filter+sort+row-building
    // Main.Rows itself is built from, just pointed at the whole library
    // instead of whatever MainViewModel.GetBaseTracksForFilter's sidebar
    // scope happens to be. Clears down to empty once the query is blank,
    // since IsShowingSearchResults hides this whole view at that point
    // anyway - no reason to keep stale matches from the last query around.
    // The actual scan runs off the UI thread (Task.Run), same reasoning as
    // MainViewModel.RebuildRowsAsync - a broad single-character query can
    // match a large fraction of the library.
    private async Task RebuildSearchResultsAsync(CancellationToken token)
    {
        var text = SearchQuery;
        if (string.IsNullOrWhiteSpace(text))
        {
            SearchAlbumResults.Clear();
            SearchArtistResults.Clear();
            SearchSongResults.Clear();
            _totalMatchingAlbums = 0;
            _totalMatchingArtists = 0;
            _totalMatchingSongs = 0;
            RecomputeHasMoreSearchResults();
            RaiseSearchResultsChanged();
            return;
        }

        var tracks = Main.Library.Tracks;
        var playing = Main.CurrentlyPlayingTrack;
        var (albums, albumsTotal, artists, artistsTotal, songs, songsTotal) = await Task.Run(() =>
        {
            var matchingAlbumTracks = tracks.Where(t =>
                t.Album?.Contains(text, StringComparison.OrdinalIgnoreCase) == true);
            var allAlbums = AlbumGridBuilder.Build(matchingAlbumTracks);

            // Same raw per-track field the Artists tab's own picker groups by
            // (see MainViewModel.RebuildSubListItems) - not EffectiveAlbumArtist,
            // which AlbumGridBuilder uses for a different purpose (labeling a
            // same-named album spanning several artists as "Various Artists").
            var allArtists = tracks
                .Select(t => t.Artists)
                .Where(a => !string.IsNullOrEmpty(a) && a.Contains(text, StringComparison.OrdinalIgnoreCase))
                .Distinct()
                .OrderBy(a => a)
                .ToList();

            var allSongRows = TrackListBuilder.Build(tracks, text, "Title", true, playing);

            return (allAlbums.Take(MaxSearchResultsPerSection).ToList(), allAlbums.Count,
                    allArtists.Take(MaxSearchResultsPerSection).ToList(), allArtists.Count,
                    allSongRows.Take(MaxSearchResultsPerSection).ToList(), allSongRows.Count);
        }, token);

        if (token.IsCancellationRequested)
            return;

        SearchAlbumResults.Clear();
        foreach (var album in albums)
            SearchAlbumResults.Add(album);

        SearchArtistResults.Clear();
        foreach (var artist in artists)
            SearchArtistResults.Add(artist!);

        SearchSongResults.Clear();
        foreach (var row in songs)
            SearchSongResults.Add(row);

        _totalMatchingAlbums = albumsTotal;
        _totalMatchingArtists = artistsTotal;
        _totalMatchingSongs = songsTotal;
        RecomputeHasMoreSearchResults();
        RaiseSearchResultsChanged();
    }

    private void RecomputeHasMoreSearchResults() =>
        HasMoreSearchResults =
            _totalMatchingSongs > MaxSearchResultsPerSection ||
            _totalMatchingAlbums > MaxSearchResultsPerSection ||
            _totalMatchingArtists > MaxSearchResultsPerSection;

    // Search deliberately maps to no sidebar item (same as RecentlyAdded/
    // Playlists) - it renders entirely from its own SearchQuery-driven
    // SearchAlbumResults/SearchArtistResults/SearchSongResults, scanning
    // Main.Library.Tracks directly (see RebuildSearchResultsAsync) rather
    // than through Main.SelectedSidebarItem/Main.Rows the way it used to.
    private void ApplyTabSelection()
    {
        var kind = SelectedTab switch
        {
            MobileTab.Songs => SidebarItemKind.Songs,
            MobileTab.Albums => SidebarItemKind.Albums,
            MobileTab.Artists => SidebarItemKind.Artists,
            _ => (SidebarItemKind?)null
        };
        Main.SelectedSidebarItem = kind != null
            ? Main.SidebarItems.FirstOrDefault(i => i.Kind == kind)
            : null;
    }

    // Albums tab grid tiles only now - Artists' own name picker uses
    // SelectArtist below instead, so tapping an artist lands on that artist's
    // album grid rather than straight into every one of their songs as one
    // flat list.
    private async void SelectAlbumOrArtist(string? name)
    {
        if (name == null)
            return;
        PushHistory();
        await SelectAlbumOrArtistCore(name);
    }

    // Sets Main.SelectedSubItem then rebuilds Main.Rows immediately (see
    // MainViewModel.RebuildRowsImmediatelyAsync) rather than trusting
    // SelectedSubItem's own setter to get there eventually via its normal
    // 250ms-debounced ScheduleFilter - without this, RaiseNavigationChanged
    // below would already have made the track list visible showing the
    // PREVIOUS scope's tracks for up to that debounce's own delay before the
    // correct, newly-scoped list actually appeared.
    private async Task SelectAlbumOrArtistCore(string name)
    {
        Main.SelectedSubItem = name;
        // includeGridTiles: false - see SelectAlbumOrArtistCore's own comment on why.
        await Main.RebuildRowsImmediatelyAsync(includeGridTiles: false);
        _hasDrilledIn = true;
        RaiseNavigationChanged();
    }

    // Artists tab name picker -> that artist's own album grid (IsShowingArtistAlbumGrid).
    // Deliberately does not touch Main.SelectedSidebarItem/SelectedSubItem - this
    // level renders from ArtistAlbumGridRows, not Main.Rows, so there is nothing
    // for MainViewModel's own filtering to do until a specific album is tapped
    // (see SelectArtistAlbum).
    private void SelectArtist(string? name)
    {
        if (name == null)
            return;
        PushHistory();
        SelectArtistCore(name);
    }

    private void SelectArtistCore(string name)
    {
        _selectedArtistName = name;
        _hasDrilledIntoArtistAlbum = false;
        _hasDrilledIn = true;
        RebuildArtistAlbumGrid();
        RaiseNavigationChanged();
    }

    // A tile in that artist's album grid -> that album's tracks, reusing the
    // Albums tab's own filtering the same way SelectRecentlyAddedAlbum does
    // (see its comment) rather than a separate artist+album-scoped track list.
    private async void SelectArtistAlbum(string? albumName)
    {
        if (albumName == null)
            return;
        PushHistory();
        Main.SelectedSidebarItem = Main.SidebarItems.FirstOrDefault(i => i.Kind == SidebarItemKind.Albums);
        Main.SelectedSubItem = albumName;
        // includeGridTiles: false - mobile never reads Main.AlbumGridTiles/
        // RecentlyAddedGridTiles (it has its own AlbumGridRows/
        // RecentlyAddedAlbumRows - see those), so building them here on
        // every drill-in was pure wasted work.
        await Main.RebuildRowsImmediatelyAsync(includeGridTiles: false); // see SelectAlbumOrArtistCore's own comment on why.
        _hasDrilledIntoArtistAlbum = true;
        RaiseNavigationChanged();
    }

    // Tapping a tile in the Recently Added grid drills into that album's
    // tracks by reusing the Albums tab's own filtering (Main.SelectedSidebarItem
    // set to the Albums sidebar item, then SelectedSubItem to the album name) -
    // ApplyTabSelection does not do this for MobileTab.RecentlyAdded itself
    // (the un-drilled-in grid renders its own RecentlyAddedAlbumRows collection,
    // not Main.Rows), so it is set explicitly here instead. SelectedTab stays
    // RecentlyAdded so Back returns to this grid, not to the Albums picker.
    private async void SelectRecentlyAddedAlbum(string? albumName)
    {
        if (albumName == null)
            return;
        PushHistory();
        Main.SelectedSidebarItem = Main.SidebarItems.FirstOrDefault(i => i.Kind == SidebarItemKind.Albums);
        Main.SelectedSubItem = albumName;
        // includeGridTiles: false - mobile never reads Main.AlbumGridTiles/
        // RecentlyAddedGridTiles (it has its own AlbumGridRows/
        // RecentlyAddedAlbumRows - see those), so building them here on
        // every drill-in was pure wasted work.
        await Main.RebuildRowsImmediatelyAsync(includeGridTiles: false); // see SelectAlbumOrArtistCore's own comment on why.
        _hasDrilledIn = true;
        RaiseNavigationChanged();
    }

    private async void SelectPlaylist(SidebarItem? item)
    {
        if (item == null)
            return;
        PushHistory();
        Main.SelectedSidebarItem = item;
        // includeGridTiles: false - mobile never reads Main.AlbumGridTiles/
        // RecentlyAddedGridTiles (it has its own AlbumGridRows/
        // RecentlyAddedAlbumRows - see those), so building them here on
        // every drill-in was pure wasted work.
        await Main.RebuildRowsImmediatelyAsync(includeGridTiles: false); // see SelectAlbumOrArtistCore's own comment on why.
        _hasDrilledIn = true;
        RaiseNavigationChanged();
    }

    private async Task GoBack()
    {
        if (_navigationHistory.Count == 0)
            return;
        var frame = _navigationHistory.Pop();
        // The screen being left goes onto the redo stack, not just
        // discarded - see _forwardHistory's own doc comment. Captured here
        // (rather than inside ApplyFrame) since GoForward needs the exact
        // opposite push (onto _navigationHistory instead).
        var leaving = BuildLeavingFrame();
        NavigationLeaving?.Invoke(this, leaving);
        _forwardHistory.Push(leaving);

        await ApplyFrame(frame);
    }

    // Symmetric to GoBack - pops the redo stack and restores it, pushing the
    // screen being left back onto the back stack so Back still works
    // afterward. Same ApplyFrame body either direction; the two methods
    // only differ in which stack they pop from and which one they push the
    // outgoing screen onto.
    private async Task GoForward()
    {
        if (_forwardHistory.Count == 0)
            return;
        var frame = _forwardHistory.Pop();
        var leaving = BuildLeavingFrame();
        NavigationLeaving?.Invoke(this, leaving);
        _navigationHistory.Push(leaving);

        await ApplyFrame(frame);
    }

    // Restores a popped frame's state and rebuilds whatever needs it -
    // shared by GoBack and GoForward, which differ only in which stack they
    // pop from/push the outgoing screen onto (see both above).
    private async Task ApplyFrame(MobileNavigationFrame frame)
    {
        _selectedTab = frame.Tab;
        _hasDrilledIn = frame.HasDrilledIn;
        _selectedArtistName = frame.SelectedArtistName;
        _hasDrilledIntoArtistAlbum = frame.HasDrilledIntoArtistAlbum;
        Main.SelectedSidebarItem = frame.SidebarItem;
        Main.SelectedSubItem = frame.SubItem;
        if (frame.SelectedArtistName != null)
            RebuildArtistAlbumGrid();
        OnPropertyChanged(nameof(SelectedTab));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        // Restoring the exact query the user had typed, not just the tab, is
        // what makes landing back on Search actually useful rather than an
        // empty prompt. Set before RaiseNavigationChanged so its own
        // Search-tab branch (RefreshSearchResultsNow) rebuilds against the
        // restored query, not whatever SetSelectedTabCore's own "fresh
        // start" clear already left there.
        if (frame.Tab == MobileTab.Search)
        {
            _searchQuery = frame.SearchQuery;
            OnPropertyChanged(nameof(SearchQuery));
        }
        // Unlike the 4 forward-drill-in call sites (which always land on a
        // TrackList screen by definition), Back/Forward can land on ANY
        // screen kind - and only TrackList actually reads Main.Rows (every
        // other kind - AlbumGrid/ArtistPicker/ArtistAlbumGrid/
        // PlaylistPicker/RecentlyAdded/SearchResults - renders from its own
        // separate collection instead, see MobileNavigationFrame.Classify).
        // Rebuilding Main.Rows on the way to one of those was pure wasted
        // work (a full filter+sort+row-construction+reachability-scan pass
        // over the whole library that nothing would even display) -
        // confirmed on a real device as a large chunk of the pause after Back
        // when the destination isn't a track list at all.
        //
        // Same reasoning as SelectAlbumOrArtistCore's own comment when it IS
        // one - without bypassing the debounce, restoring an album/playlist
        // view flashed the wrong scope's tracks for a moment before the
        // restored one actually appeared. includeGridTiles: false - see
        // that same comment.
        if (frame.ScreenKind == MobileScreenKind.TrackList)
            await Main.RebuildRowsImmediatelyAsync(includeGridTiles: false);
        RaiseNavigationChanged();
    }

    // First and last MobileTab in bottom-bar order (see the enum's own
    // declaration) - the clamp bounds for swipe paging below. Named constants
    // rather than Enum.GetValues<MobileTab>() reflection, which can be trimmed
    // away under iOS AOT and silently mis-size the range.
    private const MobileTab FirstTab = MobileTab.RecentlyAdded;
    private const MobileTab LastTab = MobileTab.Search;

    // Horizontal swipe-to-navigate (see ScreenStackPanel's own pointer
    // gesture detection) - a swipe right means "go back" in whichever sense
    // is locally relevant: undo the last Back/Forward-tracked navigation if
    // there is one (same as the chevron button), else page to the previous
    // tab in the bottom bar's left-to-right order (MobileTab's own
    // declaration order). A swipe left is symmetrically "forward": redo
    // whatever the most recent Back undid if there is one, else page to the
    // next tab. Tab-paging is clamped, not wrapping, at either end of the
    // bar - a swipe past Recently Added or past Search is just a no-op
    // rather than an unexpected jump to the other end.
    public async void SwipeBack()
    {
        if (CanGoBack)
        {
            await GoBack();
            return;
        }
        if (SelectedTab > FirstTab)
            SelectedTab = SelectedTab - 1;
    }

    public async void SwipeForward()
    {
        if (CanGoForward)
        {
            await GoForward();
            return;
        }
        if (SelectedTab < LastTab)
            SelectedTab = SelectedTab + 1;
    }

    // Called by ScreenStackPanel's interactive live-drag gesture once its
    // release-triggered easing finishes sliding the outgoing screen fully
    // off-screen - identical to GoBack()/GoForward() (the same ones
    // BackCommand and the discrete SwipeBack()/SwipeForward() above already
    // use), just under names that read correctly from the View side, and
    // only ever invoked when PeekOneBack/PeekOneForward was already
    // non-null at gesture start (see ScreenStackPanel's own gating on
    // CanGoBack/CanGoForward before going interactive).
    public Task CommitSwipeBack() => GoBack();
    public Task CommitSwipeForward() => GoForward();

    private void RaiseNavigationChanged()
    {
        if (!IsShowingTrackList)
            IsSearchVisible = false;

        OnPropertyChanged(nameof(IsShowingAlbumGrid));
        OnPropertyChanged(nameof(IsShowingArtistPicker));
        OnPropertyChanged(nameof(IsShowingArtistAlbumGrid));
        OnPropertyChanged(nameof(IsShowingPlaylistPicker));
        OnPropertyChanged(nameof(IsShowingRecentlyAddedAlbums));
        OnPropertyChanged(nameof(IsShowingSearchPrompt));
        OnPropertyChanged(nameof(IsShowingSearchResults));
        OnPropertyChanged(nameof(IsShowingTrackList));
        OnPropertyChanged(nameof(IsShowingSearchBox));
        OnPropertyChanged(nameof(IsShowingSongsSearchBox));
        OnPropertyChanged(nameof(IsShowingSearchTabBox));
        OnPropertyChanged(nameof(IsShowingScreenTitle));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(ScreenTitle));
        OnPropertyChanged(nameof(CurrentPlaylist));
        OnPropertyChanged(nameof(IsShowingPlaylistTracks));
        OnPropertyChanged(nameof(CanSearch));
        OnPropertyChanged(nameof(IsShowingAlbumTrackList));
        OnPropertyChanged(nameof(AlbumDetailRows));
        OnPropertyChanged(nameof(CurrentAlbumHeader));
        // SearchQuery and its matched results survive leaving the Search tab
        // (see SearchQuery's own doc comment) - deliberately NOT cleared here
        // on the way out, so they're still there the instant the user comes
        // back, with no rescan flash of "no results" in between. Re-running
        // the scan on the way back in (rather than trusting the stale
        // collections outright) only matters if the library itself changed
        // while the user was away - since we never cleared first, this just
        // replaces old matches with new ones in place if anything's actually
        // different, imperceptible if nothing is.
        if (SelectedTab == MobileTab.Search)
            RefreshSearchResultsNow();
        else
            _searchResultsCts?.Cancel();
        RaiseEmptyStateChanged();
        NavigationChanged?.Invoke(this, EventArgs.Empty);
    }

    // Driven by the track list's touch drag-to-reorder gesture (see MobileMainView's
    // code-behind); a no-op if the user isn't currently viewing a playlist's tracks.
    public async void ReorderCurrentPlaylistTrack(Track dragged, Track? insertBefore)
    {
        if (CurrentPlaylist is { } playlist)
            await Main.ReorderPlaylistTrack(playlist, dragged, insertBefore);
    }
}
