using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

using Avalonia.Threading;

using CommunityToolkit.Mvvm.Input;

using Material.Icons;

using Microsoft.Extensions.Logging;

using Flower.Models;
using Flower.Services;

namespace Flower.ViewModels.Mobile;

// RecentlyAdded is first/default (see MobileMainViewModel's _selectedTab) - an
// album grid ordered by recency, the app's home screen. The middle four mirror
// desktop's Songs/Albums/Artists/Playlists sidebar sections. Search is mobile-only
// (desktop has no equivalent standalone tab - its search box works over whichever
// sidebar section is already selected) - see IsShowingSearchPrompt.
public enum MobileTab { RecentlyAdded, Songs, Albums, Artists, Playlists, Search }

// Which way a newly-navigated-to screen should arrive on screen. Read (and
// cleared) by ScreenStackPanel on every navigation - see
// MobileMainViewModel.ConsumePendingTransition. None means nothing slides in:
// either there is no outgoing screen to slide over (the very first screen), or
// the transition is a Back/Forward, which animates the outgoing screen off
// instead.
public enum MobileNavigationTransition { None, FromRight, FromLeft }

// Full-screen overlays shown on top of the tab content, e.g. the expanded
// now-playing view opened by tapping the mini-player.
public enum MobileSheet { None, NowPlaying, TrackActions, AlbumActions, TrackInfo, AddToPlaylist, Settings, SmartPlaylistEditor, ConfirmPairServer, ConfirmDeleteFile, ConfirmDeletePlaylist }

// Translates the desktop MainViewModel's sidebar+sublist (side-by-side master-detail)
// navigation model into tab+drill-down navigation for a phone screen, without changing
// MainViewModel itself. Songs is a flat list; Albums/Artists/Playlists show a picker
// (album/artist names, or playlist entries) until the user taps into one.
public class MobileMainViewModel : ViewModelBase, IDisposable
{
    private readonly ILogger _logger;

    public MainViewModel Main { get; }
    public PlaylistControlViewModel PlaylistControl { get; }
    public CurrentlyPlayingControlViewModel CurrentlyPlaying { get; }

    public ObservableCollection<SidebarItem> PlaylistPickerItems { get; } = new();

    // Rows of tiles rather than a flat tile list - see AlbumGridRow for why
    // (virtualization), and AlbumGridColumns below for how wide a row is.
    public ObservableCollection<AlbumGridRow> RecentlyAddedAlbumRows { get; } = new();
    public ObservableCollection<AlbumGridRow> AlbumGridRows { get; } = new();

    // One artist's own albums (Artists tab, one level in - see IsShowingArtistAlbumGrid),
    // rebuilt by RebuildArtistAlbumGrid whenever _selectedArtistName changes or the
    // library updates while it's set.
    public ObservableCollection<AlbumGridRow> ArtistAlbumGridRows { get; } = new();

    // How many tiles the three grids above put on a row, pushed in from the
    // views by AlbumGridColumnSizing as their measured width changes - a phone
    // rotated into landscape fits five where portrait fits two. Re-chunks
    // whatever is already built rather than waiting for the next library
    // update, so the grid reflows as the device turns.
    private int _albumGridColumns = 2;

    public int AlbumGridColumns
    {
        get => _albumGridColumns;
        set
        {
            if (_albumGridColumns == value)
                return;

            _albumGridColumns = value;
            Rechunk(RecentlyAddedAlbumRows);
            Rechunk(AlbumGridRows);
            Rechunk(ArtistAlbumGridRows);
        }
    }

    private void Rechunk(ObservableCollection<AlbumGridRow> rows)
    {
        if (rows.Count == 0)
            return;

        var tiles = AlbumTilesIn(rows).ToList();
        rows.Clear();
        foreach (var row in AlbumGridRow.Chunk(tiles, _albumGridColumns))
            rows.Add(row);
    }

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
    public ICommand OpenAddToPlaylistCommand { get; }
    public ICommand AddTrackToPlaylistCommand { get; }
    public ICommand BeginCreatePlaylistCommand { get; }
    public ICommand CommitNewPlaylistCommand { get; }
    public ICommand CancelNewPlaylistCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand OpenAppSettingsCommand { get; }
    public ICommand DownloadTrackCommand { get; }
    public ICommand DownloadActionTargetCommand { get; }
    public ICommand DeleteDownloadedFileCommand { get; }
    public ICommand ConfirmDeleteFileCommand { get; }
    public ICommand CancelDeleteFileCommand { get; }
    public ICommand DownloadAllVisibleCommand { get; }
    public ICommand PlayAlbumCommand { get; }
    public ICommand ShuffleAlbumCommand { get; }
    public ICommand OpenAlbumAddToPlaylistCommand { get; }
    public ICommand OpenAlbumActionsCommand { get; }

    // A playlist row's own "..." (PlaylistPickerScreenView) - the album menu,
    // over a playlist, with the two entries only a playlist has.
    public ICommand OpenPlaylistActionsCommand { get; }
    public ICommand RenamePlaylistActionTargetCommand { get; }
    public ICommand CommitPlaylistRenameCommand { get; }
    public ICommand DeletePlaylistActionTargetCommand { get; }
    public ICommand ConfirmDeletePlaylistCommand { get; }
    public ICommand CancelDeletePlaylistCommand { get; }
    public ICommand OpenArtistActionsCommand { get; }
    public ICommand PlayAlbumActionTargetCommand { get; }
    public ICommand ShuffleAlbumActionTargetCommand { get; }
    public ICommand AddAlbumActionTargetToPlaylistCommand { get; }
    public ICommand DownloadAlbumActionTargetCommand { get; }
    public ICommand DeleteAlbumActionTargetLocalFilesCommand { get; }
    public ICommand PairWithServerCommand { get; }
    public ICommand UnpairServerCommand { get; }

    // Hands out a one-time pairing code for somebody else's device, if this
    // phone is an administrator of the server it is paired with - desktop's
    // equivalent is the "Generate Pairing Code" button in the sidebar's device-detail
    // header. See MainViewModel.InviteDeviceToPairedServerAsync.
    public ICommand InviteDeviceCommand { get; }

    // Bootstrap-only: an address for a server this phone has never shared a
    // network with, and therefore could never discover. A server paired with at
    // home reports its own addresses and needs none of this - see
    // docs/REMOTE-ACCESS-PLAN.md.
    public ICommand AddManualServerCommand { get; }
    public ICommand RemoveManualServerCommand { get; }
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
        // wholesale-replaced Main.Rows/CurrentDetailHeader.
        IsShowingTrackList ? Main.Rows.ToList() : null,
        CurrentDetailHeader,
        // Almost always None - the one sheet that is up while a navigation is
        // pushed is Now Playing, whose album art is a drill-in. See
        // MobileNavigationFrame.Sheet.
        ActiveSheet);

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

    private void PushHistory(MobileNavigationTransition transition = MobileNavigationTransition.FromRight)
    {
        var frame = BuildLeavingFrame();
        NavigationLeaving?.Invoke(this, frame);
        RememberTab(frame);
        _navigationHistory.Push(frame);
        _forwardHistory.Clear();
        _pendingTransition = transition;
        _restoredFrame = null;
    }

    // What each tab was last showing, root first: Albums, then the album the
    // user had open in it. Tapping a tab puts the user back there - on that
    // album, with the grid under it for Back - rather than at the top of the
    // tab again. The same frame objects the history holds, so a screen comes
    // back where it was scrolled to as well (see ConsumeRestoredFrame).
    //
    // Recorded on every screen left, overwriting: whatever a tab's last
    // recording was when the user walked off it is what it was showing. The
    // screens under it are the run of the history's top that belong to the
    // same tab, which is that one visit to it and nothing from an earlier one.
    private readonly Dictionary<MobileTab, IReadOnlyList<MobileNavigationFrame>> _tabScreens = new();

    private void RememberTab(MobileNavigationFrame leaving) =>
        _tabScreens[leaving.Tab] = _navigationHistory
            .TakeWhile(f => f.Tab == leaving.Tab)
            .Reverse()
            .Append(leaving)
            .ToList();

    // The frame the last Back, Forward or tab restore landed on, for the view
    // to put that screen's scroll position back - see ScreenStackPanel, which
    // keys the positions on these exact objects. Null after a fresh navigation,
    // which starts at the top.
    private MobileNavigationFrame? _restoredFrame;

    public MobileNavigationFrame? ConsumeRestoredFrame()
    {
        var frame = _restoredFrame;
        _restoredFrame = null;
        return frame;
    }

    // How the screen this navigation lands on should arrive - the forward
    // counterpart to the swipe gesture's own live drag, which ScreenStackPanel
    // already animates from the View side. Only PushHistory sets it (a genuine
    // forward navigation: a tab tap, a picker tile, a drill-in); Back/Forward
    // clear it, because their outgoing screen is animated off by
    // ScreenStackPanel itself rather than the incoming one sliding on top.
    // Consumed rather than merely read, so a NavigationChanged raised for some
    // other reason (a library rescan, a search refresh) after the one this was
    // set for can never replay the same entrance a second time.
    private MobileNavigationTransition _pendingTransition;

    public MobileNavigationTransition ConsumePendingTransition()
    {
        var transition = _pendingTransition;
        _pendingTransition = MobileNavigationTransition.None;
        return transition;
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
        null,
        ActiveSheet);

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
            // The tab bar is a left-to-right strip the swipe gesture pages
            // through (see SwipeBack/SwipeForward), so a tap has a direction
            // too: a tab to the right of this one arrives from the right, one
            // to the left from the left - the same way the swipe that reaches
            // it would. Drill-ins below have no such spatial ordering and all
            // use the default, arriving from the right like a pushed screen.
            PushHistory(value > _selectedTab
                ? MobileNavigationTransition.FromRight
                : MobileNavigationTransition.FromLeft);
            if (RememberedScreens(value) is { Count: > 0 } screens)
                RestoreTab(screens);
            else
                SetSelectedTabCore(value);
        }
    }

    // A tab's remembered screens, cut short at the first one naming a sidebar
    // item that no longer exists - a playlist deleted since, or replaced by a
    // sync (PlaylistManagementViewModel.RefreshSidebarItems builds new items).
    // What is left under it is still somewhere the user was.
    private IReadOnlyList<MobileNavigationFrame>? RememberedScreens(MobileTab tab) =>
        _tabScreens.TryGetValue(tab, out var screens)
            ? screens.TakeWhile(f => f.SidebarItem == null || Main.SidebarItems.Contains(f.SidebarItem)).ToList()
            : null;

    // Lands on the last of a tab's screens with the rest under it in the
    // history, so Back walks down through that tab before leaving it. Arrives
    // the way the tab tap says (PushHistory just set it), and without the
    // sheet the screen was left under: a tab is tapped with no sheet up, and
    // the one sheet a navigation carries is Now Playing, which was being
    // dismissed on the way out rather than something to reopen here.
    private void RestoreTab(IReadOnlyList<MobileNavigationFrame> screens)
    {
        foreach (var screen in screens.Take(screens.Count - 1))
            _navigationHistory.Push(screen);
        ApplyFrame(screens[^1], goingBack: false, restoringTab: true).Forget(_logger, "Tab restore");
    }

    // Tapping the tab already showing, the way a phone's tab bar answers it:
    // from inside the tab (an album, an artist's grid, a playlist) it goes
    // back to the tab's first screen, at the top; on that first screen it
    // scrolls it to the top.
    //
    // The tab's screens come off the history rather than the one being left
    // going on, so Back from the root leaves the tab instead of walking back
    // into the album the tap just closed. Arrives from the left, the side a
    // screen further up the tab is on.
    private void ReselectTab()
    {
        if (!_hasDrilledIn)
        {
            ScrollToTopRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        NavigationLeaving?.Invoke(this, BuildLeavingFrame());
        while (_navigationHistory.TryPeek(out var under) && under.Tab == _selectedTab)
            _navigationHistory.Pop();
        _forwardHistory.Clear();
        _pendingTransition = MobileNavigationTransition.FromLeft;
        _restoredFrame = null;
        SetSelectedTabCore(_selectedTab);
    }

    // The current screen should scroll back to its top - see ReselectTab.
    public event EventHandler? ScrollToTopRequested;

    // The actual state mutation, split out from the public setter above so
    // compound jumps (SelectSearchAlbumCommand/SelectSearchArtistCommand)
    // can push exactly one history entry for the whole jump rather than one
    // for the tab switch and a second for the drill-in.
    //
    // raiseNavigationChanged is the same argument one level down: a compound
    // jump is one navigation, so it must also be one NavigationChanged. Two
    // meant the tab's own picker screen was synced first and consumed the
    // pending entrance (ScreenStackPanel.ConsumePendingTransition), leaving
    // the drill-in that actually landed - the album the user tapped - to cut
    // in with nothing pending. The picker's slide-in was over in the same
    // frame it started, so what the user saw was no animation at all.
    private void SetSelectedTabCore(MobileTab value, bool raiseNavigationChanged = true)
    {
        // Leaving the Playlists tab - or drilling into one of its rows - takes
        // the half-typed draft with it rather than leaving it to reappear on
        // the way back.
        EndNamingNewPlaylist();
        _selectedTab = value;
        _hasDrilledIn = false;
        _selectedArtistName = null;
        _hasDrilledIntoArtistAlbum = false;
        ApplyTabSelection();
        OnPropertyChanged(nameof(SelectedTab));
        // SearchQuery deliberately survives leaving the Search tab - the query
        // and its results should still be there whenever the user comes back
        // to Search, whether via the tab bar or Back/swipe-back, not reset to
        // a blank prompt every time.
        if (raiseNavigationChanged)
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
    // Main.FilterText. Search is a one-off lookup across the whole library,
    // surfaced in its own results view (SearchAlbumResults/SearchArtistResults/
    // SearchSongResults below) - it was never meant to act as a filter that
    // follows the user to other tabs. Sharing Main.FilterText used to do
    // exactly that: ApplyTabSelection pointed the Search tab at the same
    // "Songs" scope Main.Rows uses, so a query typed here stayed live in
    // Main.FilterText and kept narrowing the Songs tab's own list for a
    // moment (or, in the worst case, until something else happened to clear
    // it) after switching tabs.
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

    // The same header, for a playlist: mimicking the album screen rather than
    // being a screen of its own is the whole point, so a playlist gets an
    // AlbumTileViewModel too and the one header markup draws both (see
    // AlbumHeaderTemplates.axaml). What it says instead of an artist is how
    // many songs are in it, and its art is deliberately nothing at all -
    // AlbumArtView's empty cover - until a playlist has a picture of its own
    // to show.
    //
    // Keyed by the playlist and by how many tracks it holds, so adding a song
    // to the one on screen re-counts the line under its name; everything else
    // about it is fixed for as long as it is that playlist's header, and
    // rebuilding on every binding pass would restart the download indicator
    // underneath it.
    private (Playlist? Playlist, int Count) _currentPlaylistHeaderKey;
    private AlbumTileViewModel? _currentPlaylistHeader;
    public AlbumTileViewModel? CurrentPlaylistHeader
    {
        get
        {
            var playlist = CurrentPlaylist;
            var key = (playlist, playlist?.Tracks.Count ?? 0);
            if (key != _currentPlaylistHeaderKey)
            {
                _currentPlaylistHeaderKey = key;
                _currentPlaylistHeader = playlist == null ? null : BuildPlaylistHeader(playlist);
            }
            return _currentPlaylistHeader;
        }
    }

    // Whichever of the two the screen is showing - one of them at most, since
    // a track list is one album's or one playlist's or the whole library's.
    public AlbumTileViewModel? CurrentDetailHeader => CurrentAlbumHeader ?? CurrentPlaylistHeader;

    private AlbumTileViewModel? BuildPlaylistHeader(Playlist playlist)
    {
        var tracks = playlist.Tracks.ToList();
        var header = new AlbumTileViewModel
        {
            Name = playlist.Name,
            // What the playlist's own row says in the picker, from the same
            // place - how many songs, and how long they run, here in words.
            Artist = PlaylistSummaryText.ForHeader(tracks),
            RepresentativeTrack = null,
            MostRecentlyAdded = tracks.Count == 0 ? default : tracks.Max(t => t.DateAdded),
            Tracks = tracks,
        };
        TrackAvailability.Apply([header], Main.PairedServerFingerprint, Main.IsPairedServerReachable);
        return header;
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

        var header = new AlbumTileViewModel
        {
            Name = albumName,
            Artist = artist,
            RepresentativeTrack = representative,
            MostRecentlyAdded = tracks.Max(t => t.DateAdded),
            Tracks = tracks,
        };
        TrackAvailability.Apply([header], Main.PairedServerFingerprint, Main.IsPairedServerReachable);
        return header;
    }

    // The top bar's download-all button, which is the same control every
    // per-track download icon is (see TrackDownloadButton) rather than a
    // hand-copied pair of glyphs - so it spins while its batch runs and goes
    // away when there is nothing left on the screen to fetch, both of which
    // the copy it replaces did not do.
    //
    // One instance for the whole app rather than one per screen: only one
    // screen is ever the current one, and RefreshDownloadAllIndicator below
    // re-answers "is there anything here to download" for whichever that is.
    public BulkDownloadIndicatorViewModel DownloadAllIndicator { get; } = new();

    // Asked of the tracks themselves rather than of the rows' own
    // IsDownloadable, so this never depends on TrackAvailability.Apply having
    // run over Main.Rows before this handler did - the answer is the same one
    // it pushes into them (see MainViewModel.Availability).
    private void RefreshDownloadAllIndicator()
    {
        // Not while its own batch is running: a track landing mid-batch is a
        // library update, and the last row on the screen going local would
        // otherwise take the spinner away before the batch it belongs to is
        // actually over. FinishDownload owns that ending.
        if (DownloadAllIndicator.IsDownloading)
            return;

        var availability = Main.Availability;
        DownloadAllIndicator.IsDownloadable = Main.Rows.Any(r => availability.IsDownloadable(r.Track));
    }

    // Re-anchors Next/Previous/auto-advance to whatever's actually on screen
    // right now, mirroring desktop's MainViewModel.SyncPlayQueueToCurrentView -
    // called immediately before every PlayTrackCommand invocation. Search's
    // own song section (SearchSongResults) is a separate mirror of Main.Rows,
    // not Main.Rows itself (see that collection's own doc comment), so it
    // needs its own branch here; every other screen (Songs, an album/
    // artist-album/Recently-Added drill-in, or a playlist) renders straight
    // from Main.Rows, whatever MainViewModel already narrowed it to.
    private void PlayAlbum(bool shuffle)
    {
        var rows = Main.Rows;
        if (rows.Count == 0)
            return;
        if (PlaylistControl.IsShuffleEnabled != shuffle)
            PlaylistControl.ToggleShuffle();

        var index = shuffle ? Random.Shared.Next(rows.Count) : 0;
        SyncPlayQueueToCurrentView();
        PlaylistControl.Play(rows[index].Track, index);
    }

    private void SyncPlayQueueToCurrentView() =>
        Main.SetPlayQueue(CurrentTrackRows.Select(r => r.Track));

    // The rows a tapped song was tapped in: the smart playlist editor's
    // preview while it is up (it covers every screen), Search's own songs on
    // Search, and Main.Rows on everything else.
    private IList<TrackRowViewModel> CurrentTrackRows =>
        IsShowingSmartPlaylistEditor ? SmartPlaylistPreviewRows
        : IsShowingSearchResults ? SearchSongResults
        : Main.Rows;

    // Non-null only while drilled into a specific playlist's track list, which is the
    // one place mobile allows reordering (Songs/Albums/Artists have no persisted order).
    public Playlist? CurrentPlaylist =>
        SelectedTab == MobileTab.Playlists && _hasDrilledIn ? Main.SelectedSidebarItem?.Playlist : null;

    public bool IsShowingPlaylistTracks => CurrentPlaylist != null;

    private MobileSheet _activeSheet = MobileSheet.None;
    public MobileSheet ActiveSheet
    {
        get => _activeSheet;
        private set
        {
            if (_activeSheet == value)
                return;
            // Leaving the rule editor by any door but its check - the back
            // arrow, a swipe, a tab change - is a cancel, and a cancel is what
            // removes a playlist that was only created to be edited.
            if (_activeSheet == MobileSheet.SmartPlaylistEditor)
                FinishSmartPlaylistEdit(saved: false);
            _activeSheet = value;
            if (value == MobileSheet.None)
            {
                EndNamingNewPlaylist();
                ActionTarget = null;
                AlbumActionTarget = null;
                PlaylistActionTarget = null;
                _playlistTargets = null;
                _albumDeleteTargets = null;
                // A confirmation still waiting for an answer when its sheet
                // goes away has been answered: no. Nothing else resolves it,
                // and PlaylistManagementViewModel.DeleteAsync is sitting on
                // that task until something does.
                ResolvePendingPlaylistDeletion(confirmed: false);
            }
            if (value == MobileSheet.NowPlaying)
                NowPlayingExitsForward = false;
            else
                // Whatever direction the last Now Playing arrived from, it is
                // over now - the next one is an ordinary raise from the right
                // unless ApplyFrame says otherwise, which it does immediately
                // before assigning this property.
                NowPlayingEntersBackward = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsShowingNowPlaying));
            OnPropertyChanged(nameof(IsShowingTrackActions));
            OnPropertyChanged(nameof(IsShowingAlbumActions));
            OnPropertyChanged(nameof(IsShowingTrackInfo));
            OnPropertyChanged(nameof(IsShowingAddToPlaylist));
            OnPropertyChanged(nameof(IsShowingSettings));
            OnPropertyChanged(nameof(IsShowingSmartPlaylistEditor));
            OnPropertyChanged(nameof(IsShowingConfirmPairServer));
            OnPropertyChanged(nameof(IsShowingConfirmDeleteFile));
            OnPropertyChanged(nameof(IsShowingConfirmDeletePlaylist));

            // Sampling costs a timer tick a second, so it runs only while the
            // readout that consumes it is actually on screen - a diagnostics
            // panel that drained the battery would be measuring its own
            // overhead as much as anything else.
            if (value == MobileSheet.Settings)
                Resources.Start();
            else
                Resources.Stop();
        }
    }

    // Live CPU/memory for the settings screen. See ResourceUsageViewModel -
    // it is a debugging readout, not a feature for listeners.
    public ResourceUsageViewModel Resources { get; } = new();

    public bool IsShowingNowPlaying => ActiveSheet == MobileSheet.NowPlaying;

    // Which edge the Now Playing sheet leaves by - see SlidingSheet.ExitsForward,
    // which this is bound to in MobileMainView.axaml. False for every ordinary
    // dismissal (the back arrow, a swipe): the sheet retreats the way it came.
    // True for the one dismissal that is a forward navigation, tapping the album
    // art, where the sheet is pushed off to the left by the album arriving from
    // the right. Reset whenever the sheet is shown again, so one forward exit
    // never colours the next plain dismissal.
    private bool _nowPlayingExitsForward;
    public bool NowPlayingExitsForward
    {
        get => _nowPlayingExitsForward;
        private set
        {
            if (_nowPlayingExitsForward == value)
                return;
            _nowPlayingExitsForward = value;
            OnPropertyChanged();
        }
    }

    // The mirror of NowPlayingExitsForward, for the way back: a sheet that was
    // pushed off to the left by an album arriving from the right has to return
    // from that same left edge when the album is popped, or going back would
    // play as a forward push. Set only by ApplyFrame, and only when the frame
    // being restored is one Back is walking to; cleared the moment any other
    // sheet state takes over, so an ordinary reopen still arrives from the
    // right. See SlidingSheet.EntersBackward.
    private bool _nowPlayingEntersBackward;
    public bool NowPlayingEntersBackward
    {
        get => _nowPlayingEntersBackward;
        private set
        {
            if (_nowPlayingEntersBackward == value)
                return;
            _nowPlayingEntersBackward = value;
            OnPropertyChanged();
        }
    }
    public bool IsShowingTrackActions => ActiveSheet == MobileSheet.TrackActions;
    public bool IsShowingAlbumActions => ActiveSheet == MobileSheet.AlbumActions;
    public bool IsShowingTrackInfo => ActiveSheet == MobileSheet.TrackInfo;
    public bool IsShowingAddToPlaylist => ActiveSheet == MobileSheet.AddToPlaylist;
    public bool IsShowingSettings => ActiveSheet == MobileSheet.Settings;
    public bool IsShowingSmartPlaylistEditor => ActiveSheet == MobileSheet.SmartPlaylistEditor;

    // ── Smart playlists ───────────────────────────────────────────────────────

    // The rule editor the sheet is showing, the same ViewModel desktop's window
    // binds. Opened by MainViewModel.SmartPlaylistEditorRequested, which both
    // heads answer - so creating one here is MainViewModel.NewSmartPlaylist,
    // exactly as it is from the desktop sidebar. Not cleared when the sheet
    // closes: it is what the sheet is still showing while it slides away.
    private SmartPlaylistEditorViewModel? _smartPlaylistEditor;
    public SmartPlaylistEditorViewModel? SmartPlaylistEditor
    {
        get => _smartPlaylistEditor;
        private set
        {
            if (_smartPlaylistEditor != null)
                _smartPlaylistEditor.PropertyChanged -= OnSmartPlaylistEditorChanged;
            _smartPlaylistEditor = value;
            if (_smartPlaylistEditor != null)
                _smartPlaylistEditor.PropertyChanged += OnSmartPlaylistEditorChanged;
            OnPropertyChanged();
            RebuildSmartPlaylistPreviewRows();
        }
    }

    // The editor's preview (SmartPlaylistEditorViewModel.PreviewTracks) as the
    // Songs tab's own rows. Every match, not a sample: the sheet lays them out
    // in a VirtualizingStackPanel, so only the rows on screen cost anything.
    // Replaced whole rather than cleared and refilled - a list of thousands
    // added one at a time is thousands of change notifications per keystroke.
    private List<TrackRowViewModel> _smartPlaylistPreviewRows = [];
    public List<TrackRowViewModel> SmartPlaylistPreviewRows
    {
        get => _smartPlaylistPreviewRows;
        private set
        {
            _smartPlaylistPreviewRows = value;
            OnPropertyChanged();
        }
    }

    private void OnSmartPlaylistEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SmartPlaylistEditorViewModel.PreviewTracks))
            RebuildSmartPlaylistPreviewRows();
    }

    private void RebuildSmartPlaylistPreviewRows()
    {
        var tracks = _smartPlaylistEditor?.PreviewTracks ?? [];

        // In the rules' own order - a limit's "most played" picks in that
        // order, and it is the order the saved playlist will have.
        SmartPlaylistPreviewRows = TrackListBuilder.Build(tracks, null, "PlaylistOrder", true,
            PlaylistControl.CurrentlyPlayingTrack,
            pairedServerFingerprint: Main.PairedServerFingerprint,
            pairedServerReachable: Main.IsPairedServerReachable);
    }

    // Whether the editor on screen has yet to be saved or cancelled - see
    // FinishSmartPlaylistEdit, which is reached from both.
    private bool _smartPlaylistEditPending;

    // Hidden rather than inert when there is nothing to recompute a playlist
    // with - the same condition desktop leaves its menu item out on.
    public bool CanCreateSmartPlaylist => Main.SmartPlaylists != null;

    public ICommand NewSmartPlaylistCommand { get; }
    public ICommand SaveSmartPlaylistCommand { get; }

    private void OpenSmartPlaylistEditor(SmartPlaylistEditorEventArgs e)
    {
        if (Main.SmartPlaylists is not { } refresher)
            return;

        // Anything already open goes first, so a pending editor is cancelled
        // before its replacement takes the field.
        ActiveSheet = MobileSheet.None;
        SmartPlaylistEditor = new SmartPlaylistEditorViewModel(e.Playlist, Main.Library, refresher, e.IsNew);
        _smartPlaylistEditPending = true;
        ActiveSheet = MobileSheet.SmartPlaylistEditor;
    }

    private void FinishSmartPlaylistEdit(bool saved)
    {
        if (!_smartPlaylistEditPending)
            return;
        _smartPlaylistEditPending = false;

        if (!saved)
            SmartPlaylistEditor?.Cancel();

        // Both outcomes move the list: a save can rename the playlist, and a
        // cancel on a new one deletes its row outright. Desktop's window does
        // the same once it closes.
        Main.Playlists.RefreshSidebarItems();
    }
    public bool IsShowingConfirmPairServer => ActiveSheet == MobileSheet.ConfirmPairServer;
    public bool IsShowingConfirmDeleteFile => ActiveSheet == MobileSheet.ConfirmDeleteFile;
    public bool IsShowingConfirmDeletePlaylist => ActiveSheet == MobileSheet.ConfirmDeletePlaylist;

    // Set by PairWithServerCommand (Settings' server list) before switching to
    // the ConfirmPairServer sheet, cleared once ConfirmPairServerCommand/
    // CancelPairServerCommand resolves it - see those. Desktop's equivalent is
    // ServerPickerView's own ConfirmDialogWindow prompt; mobile had no
    // confirmation at all here previously (SettingsView's Sync button called
    // straight through to Main.PairWithServer).
    private DiscoveredDevice? _pendingServerToPair;
    public string PendingServerToPairAlias => _pendingServerToPair?.Alias ?? "";

    // Only one sheet is up at a time (see ActiveSheet), so raising the pairing
    // sheet takes Settings' place rather than stacking on top of it. Whichever
    // sheet it displaced is remembered here and put back when pairing resolves -
    // otherwise finishing (or cancelling) a pair drops the user all the way out
    // to the library, when what they did was tap one row inside Settings and
    // expect to still be in Settings afterwards.
    private MobileSheet _sheetBeforePairing = MobileSheet.None;

    public string ConfirmPairServerTitle => $"Pair With \"{PendingServerToPairAlias}\"?";

    public string ConfirmPairServerActionLabel => IsPairingInProgress ? "Pairing..." : "Pair";

    public string ConfirmPairServerMessage =>
        $"This device's library view will be replaced by \"{PendingServerToPairAlias}\"'s - your Songs/Albums list will show its library instead of managing its own. Your existing music files on this device will not be deleted. "
        + "The pairing code below authorizes this device immediately.";

    // What the user typed into Settings' "add a server by address" box, and
    // the result of the last attempt. See AddManualServerCommand.
    private string _manualServerAddress = "";
    public string ManualServerAddress
    {
        get => _manualServerAddress;
        set
        {
            if (_manualServerAddress == value)
                return;
            _manualServerAddress = value;
            OnPropertyChanged();
        }
    }

    private string _manualServerStatus = "";
    public string ManualServerStatus
    {
        get => _manualServerStatus;
        set
        {
            if (_manualServerStatus == value)
                return;
            _manualServerStatus = value;
            OnPropertyChanged();
        }
    }

    // What the user typed into the sheet's code box. Reset every time the
    // sheet opens, so a code left from an abandoned attempt is never
    // submitted against a different server.
    private string _pendingPairingCode = "";
    public string PendingPairingCode
    {
        get => _pendingPairingCode;
        set
        {
            if (_pendingPairingCode == value)
                return;
            _pendingPairingCode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsConfirmPairServerEnabled));
            // Editing the code is the user retrying, and the message was about
            // the previous attempt - same rule as desktop's PairingCode setter.
            Main.PairingCodeError = null;
        }
    }

    // Set while a code is being redeemed, which is the whole time the sheet
    // holds itself open waiting for an answer - see ConfirmPairServerCommand.
    private bool _isPairingInProgress;
    public bool IsPairingInProgress
    {
        get => _isPairingInProgress;
        private set
        {
            if (_isPairingInProgress == value)
                return;
            _isPairingInProgress = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsConfirmPairServerEnabled));
            OnPropertyChanged(nameof(ConfirmPairServerActionLabel));
        }
    }

    // "Pair" on an empty box would only round-trip to be rejected, and a second
    // tap while the first is still in flight would redeem the same one-use code
    // twice.
    public bool IsConfirmPairServerEnabled =>
        !IsPairingInProgress && !string.IsNullOrWhiteSpace(PendingPairingCode);

    // Android's media-access permission can be permanently denied, in which case the
    // only way back in is the system app-settings screen; desktop/iOS have nothing
    // equivalent to check (PlatformPermissions.Current is left null there).
    public bool HasMediaPermissionPrompt => PlatformPermissions.Current != null;
    public bool HasMediaPermission => PlatformPermissions.Current?.IsGranted() ?? true;

    // The row the action menu was opened from, kept alongside ActionTarget for
    // the menu's Download entry: a download goes through its row so the row's
    // own icon spins (TrackDownloadRunner.DownloadRowAsync).
    private TrackRowViewModel? _actionRow;

    // Read by the menu's header for the row's art; ActionTarget's setter
    // raises its change, since the two are always set together.
    public TrackRowViewModel? ActionRow => _actionRow;

    // The album tile a long press on a grid's cover opened the album menu
    // from (AlbumActionsView). The tile rather than its tracks, because the
    // menu's header shows its art and name and its Download spins the tile's
    // own indicator, the one under that cover in the grid.
    private AlbumTileViewModel? _albumActionTarget;
    public AlbumTileViewModel? AlbumActionTarget
    {
        get => _albumActionTarget;
        private set
        {
            _albumActionTarget = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanDownloadAlbumActionTarget));
            OnPropertyChanged(nameof(CanDeleteAlbumActionTargetLocalFiles));
        }
    }

    // The playlist row whose "..." raised that same menu, and null whenever
    // it was raised over an album. The menu is one sheet for both because
    // most of what it offers - play, shuffle, add these songs somewhere,
    // download them - is the same question asked of a different pile of
    // songs; what this carries is the row itself, because the two entries it
    // does add (renaming it, deleting it) are about the playlist rather than
    // about its tracks.
    private SidebarItem? _playlistActionTarget;
    public SidebarItem? PlaylistActionTarget
    {
        get => _playlistActionTarget;
        private set
        {
            _playlistActionTarget = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsActingOnAPlaylist));
        }
    }

    public bool IsActingOnAPlaylist => PlaylistActionTarget?.Playlist != null;

    // Shown when the album's own download button would be: something in it
    // to fetch, from a server that is there to fetch it from.
    public bool CanDownloadAlbumActionTarget =>
        AlbumActionTarget?.IsDownloadable == true && Main.CanForceSync;

    // Shown once any song in the album has a file on this device - the
    // album's counterpart to a song's own Delete (CanDeleteDownloadedFile).
    public bool CanDeleteAlbumActionTargetLocalFiles =>
        AlbumActionTarget?.Tracks.Any(t => t.Path != null) == true;

    // A tile's tracks come in library order, so they are put in the album's
    // own before anything plays them - the order its track list shows. An
    // artist's stand-in tile holds several albums, which play one after
    // another in the order their grid shows them.
    private static List<Track> InAlbumOrder(AlbumTileViewModel tile) =>
        tile.Tracks.OrderBy(t => t.Album).ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber).ToList();

    // The album menu for an artist's name: a tile standing for everything of
    // theirs, built on the spot since the name list has none. Its art is their
    // most recently added album's, and where an album's artist would go it
    // says how much there is.
    private AlbumTileViewModel? BuildArtistTile(string artist)
    {
        var tracks = Main.Library.Tracks.Where(t => t.Artists == artist).ToList();
        if (tracks.Count == 0)
            return null;

        var albums = tracks.Select(t => t.Album).Distinct().Count();
        var tile = new AlbumTileViewModel
        {
            Name = artist,
            Artist = $"{albums} {(albums == 1 ? "album" : "albums")} · {tracks.Count} {(tracks.Count == 1 ? "song" : "songs")}",
            RepresentativeTrack = tracks.MaxBy(t => t.DateAdded)!,
            MostRecentlyAdded = tracks.Max(t => t.DateAdded),
            Tracks = tracks,
        };
        TrackAvailability.Apply([tile], Main.PairedServerFingerprint, Main.IsPairedServerReachable);
        return tile;
    }

    // PlayAlbum's counterpart for an album that is not the screen showing:
    // the queue is the album rather than the rows on screen.
    private void PlayAlbumActionTarget(bool shuffle)
    {
        var tile = AlbumActionTarget;
        ActiveSheet = MobileSheet.None;
        if (tile == null)
            return;
        var tracks = InAlbumOrder(tile);
        if (tracks.Count == 0)
            return;
        if (PlaylistControl.IsShuffleEnabled != shuffle)
            PlaylistControl.ToggleShuffle();

        var index = shuffle ? Random.Shared.Next(tracks.Count) : 0;
        Main.SetPlayQueue(tracks);
        PlaylistControl.Play(tracks[index], index);
    }

    // What the Add to Playlist sheet adds when it was opened from an album's
    // own actions rather than from one track's menu: the whole album. Null
    // otherwise, and cleared with the sheet, so the sheet falls back to
    // ActionTarget.
    private IReadOnlyList<Track>? _playlistTargets;

    // What the playlist being named is to be created with, taken at the moment
    // the box opened - see BeginCreatePlaylistCommand.
    private IReadOnlyList<Track>? _newPlaylistTracks;

    // The draft row: shown at the top of the playlist list (and at the top of
    // the add-to-playlist sheet's own list) with an empty name box in it, in
    // place of the New Playlist button that opened it. Both places show the
    // same control - Controls/NewPlaylistEntry - so the flow is one flow.
    private bool _isNamingNewPlaylist;
    public bool IsNamingNewPlaylist
    {
        get => _isNamingNewPlaylist;
        private set
        {
            if (_isNamingNewPlaylist == value)
                return;
            _isNamingNewPlaylist = value;
            OnPropertyChanged();
            // The draft row is something on an otherwise empty Playlists tab,
            // so "Nothing Here" must not be over it.
            RaiseEmptyStateChanged();
        }
    }

    private string? _newPlaylistName;
    public string? NewPlaylistName
    {
        get => _newPlaylistName;
        set
        {
            if (_newPlaylistName == value)
                return;
            _newPlaylistName = value;
            OnPropertyChanged();
        }
    }

    private void EndNamingNewPlaylist()
    {
        IsNamingNewPlaylist = false;
        NewPlaylistName = "";
        _newPlaylistTracks = null;
    }

    // The track a row's "..." action menu (and, in turn, the Track Info sheet) applies to.
    private Track? _actionTarget;
    public Track? ActionTarget
    {
        get => _actionTarget;
        private set
        {
            _actionTarget = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActionRow));
            OnPropertyChanged(nameof(CanDeleteDownloadedFile));
            OnPropertyChanged(nameof(CanDownloadActionTarget));
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

    // The menu's Download entry shows exactly when the row's own download icon
    // does: a placeholder the paired server can serve right now.
    public bool CanDownloadActionTarget => _actionRow?.IsDownloadable == true;

    // Whether ActionTarget's file, once deleted, would actually come back on
    // its own - true only if the currently paired Server is the same device
    // that last reported having a copy (OriginDeviceFingerprint). Null (no
    // peer has ever reported holding this exact track - e.g. purely a local
    // import) or a fingerprint that isn't the current pairing (a track from
    // before a Server switch, or synced via ad-hoc peer browsing rather than
    // bulk sync) both mean the same thing here: nothing will resync it back.
    // For an album, only if every one of its files would come back.
    public bool IsRecoverableDownload => DeleteTargets.Count > 0 && DeleteTargets.All(t =>
        t.OriginDeviceFingerprint != null && t.OriginDeviceFingerprint == Main.PairedServerFingerprint);

    // The album menu's Delete Local Files confirms through the same sheet as
    // a song's Delete, over every file of the album's on this device. Null
    // while the sheet is confirming ActionTarget alone.
    private IReadOnlyList<Track>? _albumDeleteTargets;

    private IReadOnlyList<Track> DeleteTargets =>
        _albumDeleteTargets ?? (ActionTarget is { } track ? [track] : []);

    public string ConfirmDeleteFileTitle => _albumDeleteTargets is { } files
        ? $"Delete {files.Count} local {(files.Count == 1 ? "file" : "files")} of \"{AlbumActionTarget?.Name}\"?"
        : $"Delete \"{ActionTarget?.Title}\"?";

    public string ConfirmDeleteFileMessage => (_albumDeleteTargets != null, IsRecoverableDownload) switch
    {
        (false, true) => "This removes the downloaded copy from this device. Your paired server still has it, so you can download it again later.",
        (false, false) => "Your currently paired server doesn't have this file, so it won't be synced back automatically. Deleting it now will remove your only copy.",
        (true, true) => "This removes the downloaded copies from this device. Your paired server still has them, so you can download them again later.",
        (true, false) => "Your currently paired server doesn't have some of these files, so they won't be synced back automatically. Deleting them now will remove your only copy.",
    };

    private void ConfirmDeleting(IReadOnlyList<Track>? albumFiles)
    {
        _albumDeleteTargets = albumFiles;
        OnPropertyChanged(nameof(IsRecoverableDownload));
        OnPropertyChanged(nameof(ConfirmDeleteFileTitle));
        OnPropertyChanged(nameof(ConfirmDeleteFileMessage));
        ActiveSheet = MobileSheet.ConfirmDeleteFile;
    }

    // Deleting a playlist is confirmed here rather than taken on trust, the
    // same way deleting a downloaded file is. The prompt is not this view
    // model's idea: PlaylistManagementViewModel.DeleteAsync asks whoever owns
    // the screen (desktop's is a dialog from MainView) and waits on the
    // answer, so a phone that never answered would leave that delete hanging
    // forever.
    private DeletePlaylistConfirmationEventArgs? _pendingPlaylistDelete;

    public string ConfirmDeletePlaylistTitle => _pendingPlaylistDelete?.Playlists is { Count: 1 } one
        ? $"Delete \"{one[0].Name}\"?"
        : $"Delete {_pendingPlaylistDelete?.Playlists.Count ?? 0} playlists?";

    public string ConfirmDeletePlaylistMessage =>
        "The playlist goes away. The songs in it stay in your library.";

    private void AskToDeletePlaylist(DeletePlaylistConfirmationEventArgs e)
    {
        _pendingPlaylistDelete = e;
        OnPropertyChanged(nameof(ConfirmDeletePlaylistTitle));
        OnPropertyChanged(nameof(ConfirmDeletePlaylistMessage));
        ActiveSheet = MobileSheet.ConfirmDeletePlaylist;
    }

    // Answers at most once: the sheet's own two buttons both close it, and
    // closing it comes back through here (see ActiveSheet) with the answer
    // already given.
    private void ResolvePendingPlaylistDeletion(bool confirmed)
    {
        var pending = _pendingPlaylistDelete;
        _pendingPlaylistDelete = null;
        pending?.Confirmed.TrySetResult(confirmed);
    }

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
        (IsShowingPlaylistPicker && PlaylistPickerItems.Count == 0 && !IsNamingNewPlaylist) ||
        (IsShowingRecentlyAddedAlbums && RecentlyAddedAlbumRows.Count == 0) ||
        IsShowingSearchPrompt ||
        (IsShowingSearchResults && !HasSearchAlbumResults && !HasSearchArtistResults && !HasSearchSongResults) ||
        // Not while the rows for this scope are still being built: until they
        // land, Main.Rows belongs to the screen being left - see
        // LibraryBrowserViewModel.IsRowsRebuildPending.
        // A playlist is the one track list that says what it is even when it
        // holds nothing: its header (name, cover, actions) is the screen, and
        // an overlay centred over the whole thing sat on top of that header
        // to announce what the empty space under it already showed.
        (IsShowingTrackList && !IsShowingPlaylistTracks
            && Main.Rows.Count == 0 && !Main.Browser.IsRowsRebuildPending);

    // What mobile derives from the library as a whole rather than from the rows
    // Main keeps: the album grids, search results, the album header and the
    // Download All indicator.
    private void RebuildLibraryDerivedState()
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
        _currentPlaylistHeaderKey = default;
        RaiseDetailHeaderChanged();
        RefreshDownloadAllIndicator();
    }

    public MaterialIconKind EmptyStateIcon => IsShowingSearchPrompt ? MaterialIconKind.Magnify : MaterialIconKind.MusicNoteOff;

    public string EmptyStateTitle
    {
        get
        {
            if (IsShowingSearchPrompt)
                return "Search Your Library";
            if (IsShowingSearchResults)
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
            if (IsShowingSearchResults)
                return $"No matches for \"{SearchQuery}\".";
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

    // Every event this class attaches to in its constructor, paired with its
    // teardown - see SubscriptionBag, and docs/ARCHITECTURE-REVIEW.md Tier 2.3.
    // It outlives nothing here in the app (it is a container singleton, like
    // the MainViewModel it wraps), but a test that builds one no longer leaves
    // six handlers attached to the shared Library/PlaylistControlViewModel.
    private readonly SubscriptionBag _subscriptions = new();

    public void Dispose()
    {
        Resources.Dispose();
        _subscriptions.Dispose();
    }

    public MobileMainViewModel(
        MainViewModel main,
        PlaylistControlViewModel playlistControl,
        CurrentlyPlayingControlViewModel currentlyPlaying,
        ILogger<MobileMainViewModel> logger)
    {
        Main = main;
        PlaylistControl = playlistControl;
        CurrentlyPlaying = currentlyPlaying;
        _logger = logger;

        _subscriptions.Add<PropertyChangedEventHandler>((_, e) =>
        {
            if (e.PropertyName == nameof(PlaylistControlViewModel.CurrentlyPlayingTrack) &&
                PlaylistControl.CurrentlyPlayingTrack == null)
                ActiveSheet = MobileSheet.None;
        },
            h => PlaylistControl.PropertyChanged += h, h => PlaylistControl.PropertyChanged -= h);

        _subscriptions.Add<NotifyCollectionChangedEventHandler>((_, _) => RebuildPlaylistPicker(),
            h => Main.SidebarItems.CollectionChanged += h, h => Main.SidebarItems.CollectionChanged -= h);
        // A song added to (or dragged within, or removed from) a playlist
        // changes what its row says on the right without changing which rows
        // there are, so the collection above never hears about it.
        _subscriptions.Add<EventHandler>((_, _) => Dispatcher.UIThread.Post(RefreshPlaylistSummaries),
            h => Main.Library.PlaylistsChanged += h, h => Main.Library.PlaylistsChanged -= h);
        // Mobile's answer to "really delete this playlist?" - desktop's is a
        // dialog raised from MainView. Something has to answer, or the delete
        // waits forever; see AskToDeletePlaylist.
        _subscriptions.Add<EventHandler<SmartPlaylistEditorEventArgs>>((_, e) => OpenSmartPlaylistEditor(e),
            h => Main.SmartPlaylistEditorRequested += h, h => Main.SmartPlaylistEditorRequested -= h);
        _subscriptions.Add<EventHandler<DeletePlaylistConfirmationEventArgs>>((_, e) => AskToDeletePlaylist(e),
            h => Main.DeletePlaylistConfirmationRequested += h, h => Main.DeletePlaylistConfirmationRequested -= h);
        _subscriptions.Add<EventHandler>((_, _) => Dispatcher.UIThread.Post(RebuildLibraryDerivedState),
            h => Main.Library.LibraryChanged += h, h => Main.Library.LibraryChanged -= h);
        // Only a change that can move a track between albums, or change its
        // art or whether it is on the device - a play or a star changes none
        // of what this rebuilds.
        _subscriptions.Add<EventHandler<TrackChangedEventArgs>>((_, e) =>
        {
            if (e.Reshapes)
                Dispatcher.UIThread.Post(RebuildLibraryDerivedState);
        },
            h => Main.Library.TrackChanged += h, h => Main.Library.TrackChanged -= h);
        _subscriptions.Add<PropertyChangedEventHandler>((_, e) =>
        {
            // Songs/Albums/Artists picker empty-states only - Search has its
            // own SearchQuery-driven path (see that property's setter) and no
            // longer touches Main.Rows at all.
            if (e.PropertyName is nameof(MainViewModel.Rows) or nameof(MainViewModel.SubListItems)
                or nameof(LibraryBrowserViewModel.IsRowsRebuildPending))
            {
                RaiseEmptyStateChanged();
                RefreshDownloadAllIndicator();
            }
            if (e.PropertyName is nameof(MainViewModel.PairingCodeError)
                or nameof(MainViewModel.IsPairedServerTrustConfirmed))
                SettleCodePairing();
        },
            h => Main.PropertyChanged += h, h => Main.PropertyChanged -= h);
        // SearchSongResults is a separate TrackRowViewModel list from
        // Main.Rows (see RebuildSearchResultsAsync's own doc comment), so it
        // needs its own live-update subscription to stay correct after the
        // paired server's reachability changes - MainViewModel.Rows gets this
        // for free from its own subscription to the same signal.
        // The album grids need the same treatment for the same reason, and
        // even more so: they are only rebuilt on a library change, so one
        // built while the server was up would otherwise stay at full strength
        // indefinitely after it went away. Re-marking is in place (see
        // AlbumTileViewModel.Tracks), so already-loaded art survives it.
        _subscriptions.Add<EventHandler>((_, _) =>
        {
            TrackAvailability.Apply(SearchSongResults, Main.PairedServerFingerprint, Main.IsPairedServerReachable);
            TrackAvailability.Apply(SmartPlaylistPreviewRows, Main.PairedServerFingerprint, Main.IsPairedServerReachable);
            ApplyAlbumTileAvailability();
            RefreshDownloadAllIndicator();
        },
            h => Main.ReachabilityChanged += h, h => Main.ReachabilityChanged -= h);
        RebuildPlaylistPicker();
        RebuildRecentlyAddedAlbums();
        RebuildAlbumGrid();
        RebuildArtistAlbumGrid();
        ApplyTabSelection();

        SelectTabCommand = new RelayCommand<string>(name =>
        {
            if (name == null || !System.Enum.TryParse<MobileTab>(name, out var tab))
                return;
            if (tab == SelectedTab)
                ReselectTab();
            else
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
            SetSelectedTabCore(MobileTab.Albums, raiseNavigationChanged: false);
            await SelectAlbumOrArtistCore(name);
        });
        SelectSearchArtistCommand = new RelayCommand<string>(name =>
        {
            if (name == null)
                return;
            PushHistory();
            SetSelectedTabCore(MobileTab.Artists, raiseNavigationChanged: false);
            SelectArtistCore(name);
        });
        SelectArtistAlbumCommand = new RelayCommand<string>(SelectArtistAlbum);
        SelectRecentlyAddedAlbumCommand = new RelayCommand<string>(SelectRecentlyAddedAlbum);
        SelectPlaylistCommand = new RelayCommand<SidebarItem>(SelectPlaylist);
        BackCommand = new RelayCommand(async () => await GoBack());
        // Takes the row rather than its Track: the row's position in the list
        // on screen is the queue position, and two rows can hold the same
        // Track (a playlist with the same song added twice), which a Track
        // alone cannot tell apart - see MainViewModel.PlayTrack's queueIndex
        // overload and docs/ARCHITECTURE-REVIEW.md 0.2.
        PlayTrackCommand = new RelayCommand<TrackRowViewModel>(row =>
        {
            var track = row?.Track;
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
            var queueIndex = CurrentTrackRows.IndexOf(row!);

            SyncPlayQueueToCurrentView();
            // The not-yet-downloaded case (Path == null) is handled inside
            // Play itself now - see IStreamUrlResolver. This used to have to
            // route through MainViewModel to get that.
            PlaylistControl.Play(track, queueIndex);
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
        // Tapping the Now Playing sheet's album art - drills into that track's
        // album and then closes the sheet, same tab-switch-plus-drill-in-as-
        // one-history-entry shape as SelectSearchAlbumCommand.
        //
        // This is a push, so the album arrives from the right like every other
        // one, and the sheet is pushed off to the left ahead of it
        // (NowPlayingExitsForward - see SlidingSheet.ExitsForward). The two
        // slides are the same length and start together, so the sheet's
        // trailing edge and the album's leading edge stay flush and the pair
        // travels as one screen. Dismissing the sheet the ordinary way instead
        // uncovers the album from the left, which is what going back looks
        // like - and doing it before the drill-in had landed hid the album's
        // own entrance behind the sheet for its whole length.
        GoToCurrentlyPlayingAlbumCommand = new RelayCommand(async () =>
        {
            var album = PlaylistControl.CurrentlyPlayingTrack?.Album;
            if (album == null)
                return;
            if (IsShowingAlbumTrackList && Main.SelectedSubItem == album)
            {
                // Already there: the sheet was raised over that very album, so
                // there is nothing to push and nothing to slide in. Pushing
                // anyway put a history entry for the screen being landed on
                // into the stack, which is a back step that goes nowhere - and
                // ScreenStackPanel would be handed the same screen twice, one
                // control for two slots (see its own note on the collision).
                // A plain dismissal instead, retreating the way it arrived.
                ActiveSheet = MobileSheet.None;
                return;
            }
            PushHistory();
            SetSelectedTabCore(MobileTab.Albums, raiseNavigationChanged: false);
            await SelectAlbumOrArtistCore(album);
            // Only now, with the album actually built and its own entrance
            // already running: the sheet and that entrance are one push, so
            // they have to start together.
            NowPlayingExitsForward = true;
            ActiveSheet = MobileSheet.None;
        });
        ToggleRepeatCommand = new RelayCommand(CurrentlyPlaying.ToggleRepeat);
        ToggleShuffleCommand = new RelayCommand(CurrentlyPlaying.ToggleShuffle);
        CloseSheetCommand = new RelayCommand(() => ActiveSheet = MobileSheet.None);
        NextTrackCommand = new RelayCommand(PlaylistControl.Next);
        PreviousTrackCommand = new RelayCommand(PlaylistControl.Previous);
        OpenTrackActionsCommand = new RelayCommand<TrackRowViewModel>(row =>
        {
            if (row == null)
                return;
            _actionRow = row;
            _playlistTargets = null;
            ActionTarget = row.Track;
            ActiveSheet = MobileSheet.TrackActions;
        });
        OpenAlbumActionsCommand = new RelayCommand<AlbumTileViewModel>(tile =>
        {
            if (tile == null)
                return;
            _playlistTargets = null;
            ActionTarget = null;
            AlbumActionTarget = tile;
            ActiveSheet = MobileSheet.AlbumActions;
        });
        // The album menu over a playlist: its own tile for the header and the
        // actions that act on songs, plus the two this target adds (see
        // IsActingOnAPlaylist).
        OpenPlaylistActionsCommand = new RelayCommand<SidebarItem>(item =>
        {
            if (item?.Playlist is not { } playlist)
                return;
            _playlistTargets = null;
            ActionTarget = null;
            AlbumActionTarget = BuildPlaylistHeader(playlist);
            PlaylistActionTarget = item;
            ActiveSheet = MobileSheet.AlbumActions;
        });
        // Renaming happens on the row itself rather than in a sheet of its
        // own: the menu closes, the row's name turns into a box with the
        // current name in it, and the phone's keyboard comes up under it -
        // the same shape as naming a new one (NewPlaylistEntry), and the same
        // commit rules as the desktop sidebar's in-place rename, which is
        // literally the same service underneath.
        RenamePlaylistActionTargetCommand = new RelayCommand(() =>
        {
            var item = PlaylistActionTarget;
            ActiveSheet = MobileSheet.None;
            if (item?.Playlist == null)
                return;
            foreach (var other in PlaylistPickerItems)
                other.IsEditing = false;
            item.IsEditing = true;
        });
        CommitPlaylistRenameCommand = new RelayCommand<SidebarItem>(async item =>
        {
            if (item is { IsEditing: true })
                await Main.Rename.CommitAsync(item, Main);
        });
        // Closes the menu and asks. What asks is the delete itself - see
        // AskToDeletePlaylist - so this cannot forget to.
        DeletePlaylistActionTargetCommand = new RelayCommand(async () =>
        {
            var playlist = PlaylistActionTarget?.Playlist;
            ActiveSheet = MobileSheet.None;
            if (playlist != null)
                await Main.DeletePlaylistAsync(playlist);
        });
        ConfirmDeletePlaylistCommand = new RelayCommand(() =>
        {
            ResolvePendingPlaylistDeletion(confirmed: true);
            ActiveSheet = MobileSheet.None;
        });
        CancelDeletePlaylistCommand = new RelayCommand(() => ActiveSheet = MobileSheet.None);
        OpenArtistActionsCommand = new RelayCommand<string>(artist =>
        {
            if (artist == null || BuildArtistTile(artist) is not { } tile)
                return;
            OpenAlbumActionsCommand.Execute(tile);
        });
        PlayAlbumActionTargetCommand = new RelayCommand(() => PlayAlbumActionTarget(shuffle: false));
        ShuffleAlbumActionTargetCommand = new RelayCommand(() => PlayAlbumActionTarget(shuffle: true));
        AddAlbumActionTargetToPlaylistCommand = new RelayCommand(() =>
        {
            if (AlbumActionTarget is not { } tile)
                return;
            _playlistTargets = InAlbumOrder(tile);
            ActiveSheet = MobileSheet.AddToPlaylist;
        });
        // Closes the menu first, like the track menu's Download: the batch
        // runs on in the tile's own indicator.
        DownloadAlbumActionTargetCommand = new RelayCommand(async () =>
        {
            var tile = AlbumActionTarget;
            ActiveSheet = MobileSheet.None;
            if (tile != null)
                await Main.Downloads.DownloadAlbumAsync(tile, tile.Tracks);
        });
        // Confirmed first, like a song's Delete. The confirm sheet replaces the
        // menu without passing through None, so AlbumActionTarget is still set
        // for its title.
        DeleteAlbumActionTargetLocalFilesCommand = new RelayCommand(() =>
        {
            if (AlbumActionTarget is not { } tile)
                return;
            var files = tile.Tracks.Where(t => t.Path != null).ToList();
            if (files.Count > 0)
                ConfirmDeleting(files);
        });
        ViewTrackInfoCommand = new RelayCommand(() =>
        {
            if (ActionTarget != null)
                ActiveSheet = MobileSheet.TrackInfo;
        });
        OpenAddToPlaylistCommand = new RelayCommand(() =>
        {
            if (ActionTarget != null)
                ActiveSheet = MobileSheet.AddToPlaylist;
        });
        AddTrackToPlaylistCommand = new RelayCommand<SidebarItem>(async item =>
        {
            if (item?.Playlist is { } playlist)
            {
                if (_playlistTargets != null)
                    await Main.AddTracksToPlaylist(_playlistTargets, playlist);
                else if (ActionTarget != null)
                    await Main.AddTrackToPlaylist(ActionTarget, playlist);
            }
            ActiveSheet = MobileSheet.None;
        });
        // Nothing is created here: the draft row appears with an empty, focused
        // box (NewPlaylistEntry), and only a name the user actually typed
        // becomes a playlist. Whatever the new playlist is meant to hold is
        // captured now rather than at commit time - the sheet's own targets are
        // cleared the moment it closes (see ActiveSheet's setter), and the
        // commit closes it before it creates anything.
        BeginCreatePlaylistCommand = new RelayCommand(() =>
        {
            _newPlaylistTracks = _playlistTargets
                ?? (ActionTarget is { } track ? [track] : []);
            NewPlaylistName = "";
            IsNamingNewPlaylist = true;
        });
        CommitNewPlaylistCommand = new RelayCommand(async () =>
        {
            // One name, one playlist: committing is reachable from both Enter
            // and the focus the box loses on its way out, and the second of
            // those arrives after the first has already finished.
            if (!IsNamingNewPlaylist)
                return;

            var name = NewPlaylistName?.Trim();
            var tracks = _newPlaylistTracks ?? [];
            EndNamingNewPlaylist();

            // An empty name is how the user says no - the draft row simply goes
            // away, and the sheet it may have been in stays up.
            if (string.IsNullOrEmpty(name))
                return;

            ActiveSheet = MobileSheet.None;
            await Main.CreatePlaylistNamed(name, tracks);
        });
        CancelNewPlaylistCommand = new RelayCommand(EndNamingNewPlaylist);
        NewSmartPlaylistCommand = new RelayCommand(Main.NewSmartPlaylist);
        // Stays open on a rejected save, the reason under the rules - the same
        // as desktop's OK.
        SaveSmartPlaylistCommand = new RelayCommand(() =>
        {
            if (SmartPlaylistEditor?.Save() != true)
                return;
            FinishSmartPlaylistEdit(saved: true);
            ActiveSheet = MobileSheet.None;
        });
        OpenSettingsCommand = new RelayCommand(() =>
        {
            OnPropertyChanged(nameof(HasMediaPermission));
            ActiveSheet = MobileSheet.Settings;
        });
        OpenAppSettingsCommand = new RelayCommand(() => PlatformPermissions.Current?.OpenAppSettings());
        DownloadTrackCommand = new RelayCommand<TrackRowViewModel>(async row =>
        {
            if (row != null)
                await Main.Downloads.DownloadRowAsync(row);
        });
        // Closes the menu first: the download runs on in the row's own icon,
        // not in a sheet waiting on it.
        DownloadActionTargetCommand = new RelayCommand(async () =>
        {
            ActiveSheet = MobileSheet.None;
            if (_actionRow is { } row)
                await Main.Downloads.DownloadRowAsync(row);
        });
        // Opens the confirm sheet (ConfirmDeleteFileCommand/CancelDeleteFileCommand
        // below actually do the deleting) rather than deleting immediately -
        // this is a destructive, sometimes-unrecoverable action (see
        // IsRecoverableDownload/ConfirmDeleteFileMessage), so it always gets a
        // confirmation with an explicit warning first.
        DeleteDownloadedFileCommand = new RelayCommand(() =>
        {
            if (ActionTarget != null)
                ConfirmDeleting(albumFiles: null);
        });
        ConfirmDeleteFileCommand = new RelayCommand(async () =>
        {
            foreach (var track in DeleteTargets.ToList())
                await Main.DeleteDownloadedFileAsync(track);
            ActiveSheet = MobileSheet.None;
        });
        CancelDeleteFileCommand = new RelayCommand(() => ActiveSheet = MobileSheet.None);
        // Downloads every not-yet-downloaded track currently in Main.Rows -
        // only ever invoked while viewing one album's or one playlist's tracks
        // (see IsShowingAlbumTrackList/IsShowingPlaylistTracks in
        // MobileMainView.axaml), so that's the scope this ends up covering;
        // Main.Rows is whatever MainViewModel already narrowed it to. The
        // batching itself (throttle, per-row icon state, re-resolving rows a
        // completed download replaced) is the shared runner's - see
        // TrackDownloadRunner.DownloadAllAsync.
        // Through the indicator rather than straight to DownloadAllAsync, so
        // the button spins while the batch runs and leaves the way a track's
        // own icon does - see DownloadIndicatorViewModel.FinishDownload.
        DownloadAllVisibleCommand = new RelayCommand(async () =>
            await Main.Downloads.DownloadAlbumAsync(
                DownloadAllIndicator, Main.Rows.Select(r => r.Track).ToList()));

        // The album header's play and shuffle (TrackListScreenView). Both start
        // the album the way tapping one of its rows does - the queue re-anchored
        // to the rows on screen first - and both leave shuffle the way the
        // button says: play is the album in order from its first track, shuffle
        // is shuffle turned on from a random one. Through ToggleShuffle rather
        // than the property, so the choice persists like the Now Playing toggle.
        PlayAlbumCommand = new RelayCommand(() => PlayAlbum(shuffle: false));
        ShuffleAlbumCommand = new RelayCommand(() => PlayAlbum(shuffle: true));
        OpenAlbumAddToPlaylistCommand = new RelayCommand(() =>
        {
            if (Main.Rows.Count == 0)
                return;
            ActionTarget = null;
            // The sheet's header shows what it adds, the same way it does
            // when it is reached from the album menu.
            AlbumActionTarget = CurrentDetailHeader;
            _playlistTargets = Main.Rows.Select(r => r.Track).ToList();
            ActiveSheet = MobileSheet.AddToPlaylist;
        });

        // Confirm-before-pairing (see ConfirmPairServerMessage) rather than
        // pairing immediately - matches desktop's ServerPickerView dialog.
        PairWithServerCommand = new RelayCommand<DiscoveredDevice>(device =>
        {
            if (device == null)
                return;
            _pendingServerToPair = device;
            _sheetBeforePairing = ActiveSheet;
            PendingPairingCode = "";
            OnPropertyChanged(nameof(PendingServerToPairAlias));
            OnPropertyChanged(nameof(ConfirmPairServerTitle));
            OnPropertyChanged(nameof(ConfirmPairServerActionLabel));
            OnPropertyChanged(nameof(ConfirmPairServerMessage));
            OnPropertyChanged(nameof(IsConfirmPairServerEnabled));
            ActiveSheet = MobileSheet.ConfirmPairServer;
        });
        UnpairServerCommand = new RelayCommand(Main.UnpairServer);
        InviteDeviceCommand = new RelayCommand(async () => await Main.InviteDeviceToPairedServerAsync());
        AddManualServerCommand = new RelayCommand(async () =>
        {
            var address = ManualServerAddress.Trim();
            if (address.Length == 0)
                return;

            ManualServerStatus = "Looking for a server...";
            var found = await Main.AddManualServerAsync(address) != null;

            // A typo is by far the likeliest reason this fails, and finding
            // that out here beats finding it out from a coffee shop. The entry
            // is kept either way - a server that is merely switched off right
            // now is still the server the user meant.
            ManualServerStatus = found
                ? ""
                : $"Nothing answered at {address}. It is saved anyway - check the address, and that Tailscale is on at both ends.";
            if (found)
                ManualServerAddress = "";

            OnPropertyChanged(nameof(Main.ManualServerAddresses));
        });
        RemoveManualServerCommand = new RelayCommand<string>(address =>
        {
            if (address != null)
                Main.RemoveManualServer(address);
        });
        ForceSyncCommand = new RelayCommand(Main.ForceSyncNow);
        ConfirmPairServerCommand = new RelayCommand(() =>
        {
            if (_pendingServerToPair is not { } device)
            {
                ActiveSheet = _sheetBeforePairing;
                return;
            }

            Main.PairingCodeError = null;

            // A code is accepted or refused within one round trip, and a
            // refusal has to land where the user is looking - next to the box
            // that produced it, with the typed code still there to correct. So
            // the sheet holds itself open until SettleCodePairing sees one or
            // the other.
            IsPairingInProgress = true;
            Main.PairWithServer(device, PendingPairingCode.Trim());
        });
        CancelPairServerCommand = new RelayCommand(() =>
        {
            _pendingServerToPair = null;
            PendingPairingCode = "";
            IsPairingInProgress = false;
            Main.PairingCodeError = null;
            ActiveSheet = _sheetBeforePairing;
        });
    }

    // The other half of ConfirmPairServerCommand's code path: the sheet stays up
    // through the redeem, so something has to take it back down. Trust confirmed
    // means the server accepted the code and the sheet is done; an error means it
    // stays, showing the reason, ready for another try.
    private void SettleCodePairing()
    {
        if (!IsShowingConfirmPairServer || !IsPairingInProgress)
            return;

        if (Main.IsPairedServerTrustConfirmed)
        {
            IsPairingInProgress = false;
            _pendingServerToPair = null;
            PendingPairingCode = "";
            ActiveSheet = _sheetBeforePairing;
        }
        else if (!string.IsNullOrEmpty(Main.PairingCodeError))
        {
            IsPairingInProgress = false;
        }
    }

    private void RefreshPlaylistSummaries()
    {
        foreach (var item in PlaylistPickerItems)
            item.NotifyPlaylistSummaryChanged();
        // The screen's own header counts its songs too, and a playlist the
        // user is looking at is the one most likely to have just gained one.
        _currentPlaylistHeaderKey = default;
        RaiseDetailHeaderChanged();
    }

    private void RebuildPlaylistPicker()
    {
        PlaylistPickerItems.Clear();
        foreach (var item in Main.SidebarItems.Where(i => i.Kind == SidebarItemKind.Playlist))
            PlaylistPickerItems.Add(item);
        RaiseEmptyStateChanged();
    }

    // Every album tile this view model owns, re-marked in place. Called on
    // every reachability change (see the constructor's subscription) - the
    // header included, since an album drilled into while the server was up
    // stays on screen after it goes away.
    private void RaiseDetailHeaderChanged()
    {
        OnPropertyChanged(nameof(CurrentAlbumHeader));
        OnPropertyChanged(nameof(CurrentPlaylistHeader));
        OnPropertyChanged(nameof(CurrentDetailHeader));
    }

    private void ApplyAlbumTileAvailability()
    {
        var fingerprint = Main.PairedServerFingerprint;
        var reachable = Main.IsPairedServerReachable;
        TrackAvailability.Apply(AlbumTilesIn(RecentlyAddedAlbumRows), fingerprint, reachable);
        TrackAvailability.Apply(AlbumTilesIn(AlbumGridRows), fingerprint, reachable);
        TrackAvailability.Apply(AlbumTilesIn(ArtistAlbumGridRows), fingerprint, reachable);
        TrackAvailability.Apply(SearchAlbumResults, fingerprint, reachable);
        if (CurrentDetailHeader is { } header)
            TrackAvailability.Apply([header], fingerprint, reachable);
    }

    private static IEnumerable<AlbumTileViewModel> AlbumTilesIn(IEnumerable<AlbumGridRow> rows) =>
        rows.SelectMany(row => row.Tiles);

    private void RebuildRecentlyAddedAlbums() =>
        RefillAlbumRows(RecentlyAddedAlbumRows, RecentlyAddedAlbumsBuilder.Build(Main.Library.Tracks));

    private void RebuildAlbumGrid() =>
        RefillAlbumRows(AlbumGridRows, AlbumGridBuilder.Build(Main.Library.Tracks));

    // Re-chunks a grid from freshly built tiles, keeping the tile instances
    // that are still on screen - these grids are rebuilt on every library
    // change, and a completing download is a library change, so replacing the
    // tiles wholesale abandoned the very spinner the album's own download
    // button had just started. See AlbumTileMerge; desktop's own grids go
    // through the same merge from LibraryBrowserViewModel.
    private void RefillAlbumRows(ObservableCollection<AlbumGridRow> rows, List<AlbumTileViewModel> built)
    {
        var tiles = AlbumTileMerge.Apply(AlbumTilesIn(rows).ToList(), built, out var retired);
        TrackAvailability.Apply(tiles, Main.PairedServerFingerprint, Main.IsPairedServerReachable);

        rows.Clear();
        foreach (var row in AlbumGridRow.Chunk(tiles, AlbumGridColumns))
            rows.Add(row);

        foreach (var tile in retired)
            tile.Dispose();

        RaiseEmptyStateChanged();
    }

    // Every track by _selectedArtistName specifically - AlbumGridBuilder's own
    // Album-name-alone grouping (see its doc comment) is safe here despite not
    // also keying on Artist, since everything passed in already shares this one
    // artist. No-ops (clears down to empty) when nothing is selected, so a stale
    // grid never lingers if this fires while the picker itself is showing.
    private void RebuildArtistAlbumGrid()
    {
        if (_selectedArtistName == null)
        {
            foreach (var tile in AlbumTilesIn(ArtistAlbumGridRows))
                tile.Dispose();
            ArtistAlbumGridRows.Clear();
            RaiseEmptyStateChanged();
            return;
        }

        RefillAlbumRows(
            ArtistAlbumGridRows,
            AlbumGridBuilder.Build(Main.Library.Tracks.Where(t => t.Artists == _selectedArtistName)));
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
        var pairedServerFingerprint = Main.PairedServerFingerprint;
        var pairedServerReachable   = Main.IsPairedServerReachable;
        var (albums, albumsTotal, artists, artistsTotal, songs, songsTotal) = await Task.Run(() =>
        {
            // SearchText, not a plain Contains, so these two agree with the
            // song rows below - those go through TrackListBuilder, which folds
            // accents. Searching "bjork" must not return the songs and then an
            // empty artist strip.
            var matchingAlbumTracks = tracks.Where(t => SearchText.Contains(t.Album, text));
            var allAlbums = AlbumGridBuilder.Build(matchingAlbumTracks);
            TrackAvailability.Apply(allAlbums, pairedServerFingerprint, pairedServerReachable);

            // Same raw per-track field the Artists tab's own picker groups by
            // (see MainViewModel.RebuildSubListItems) - not EffectiveAlbumArtist,
            // which AlbumGridBuilder uses for a different purpose (labeling a
            // same-named album spanning several artists as "Various Artists").
            var allArtists = tracks
                .Select(t => t.Artists)
                .Where(a => SearchText.Contains(a, text))
                .Distinct()
                .OrderBy(a => a)
                .ToList();

            // See TrackAvailability - passed straight in so these rows are
            // correct from construction, same as Main.Rows itself
            // (MainViewModel.RebuildRowsAsync); SearchSongResults below is a
            // separate row list from Main.Rows (see this method's own doc
            // comment), kept correct afterward by the Main.ReachabilityChanged
            // subscription in the constructor rather than by a one-off patch
            // here.
            var allSongRows = TrackListBuilder.Build(tracks, text, "Title", true, playing,
                pairedServerFingerprint: pairedServerFingerprint, pairedServerReachable: pairedServerReachable);

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
        UseTheSortThisScreenWants(flatSongs: SelectedTab == MobileTab.Songs);
        Main.SelectedSidebarItem = kind != null
            ? Main.SidebarItems.FirstOrDefault(i => i.Kind == kind)
            : null;
    }

    // A phone has no column headers, so the order a list comes in is the app's
    // to choose rather than the user's to set - and the two screens want
    // opposite things. The flat Songs list is the whole library at once, where
    // track number means nothing and a name is what anyone is looking for; an
    // album is the album, in its own order. Applied before the scope changes,
    // so the rebuild that the scope change causes is already the right sort
    // and nothing is sorted twice. Set rather than persisted (see
    // LibraryBrowserViewModel.UseSort): it is not a choice anyone made.
    private void UseTheSortThisScreenWants(bool flatSongs) =>
        Main.Browser.UseSort(flatSongs ? "Title" : "TrackNumber", ascending: true);

    // Albums tab grid tiles only now - Artists' own name picker uses
    // SelectArtist below instead, so tapping an artist lands on that artist's
    // album grid rather than straight into every one of their songs as one
    // flat list.
    private void SelectAlbumOrArtist(string? name)
    {
        if (name == null)
            return;
        PushHistory();
        SelectAlbumOrArtistCore(name).Forget(_logger, "Album/artist drill-in");
    }

    private Task SelectAlbumOrArtistCore(string name) => DrillIntoAsync(subItem: name);

    // Which drill-in level a navigation lands on - the one thing that differs
    // between the four track-list drill-ins below.
    private enum DrillLevel { TrackList, ArtistAlbum }

    // The scope-then-show sequence every drill-in shares, written once.
    //
    // Rows are rebuilt *immediately* (see MainViewModel.RebuildRowsImmediatelyAsync)
    // rather than trusting SelectedSidebarItem/SelectedSubItem's own setters to
    // get there eventually via their normal 250ms-debounced ScheduleFilter -
    // without that, RaiseNavigationChanged would already have made the track
    // list visible showing the PREVIOUS scope's tracks for up to the debounce's
    // delay before the correct, newly-scoped list appeared. And
    // includeGridTiles: false because mobile never reads Main.AlbumGridTiles/
    // RecentlyAddedGridTiles at all - it has its own AlbumGridRows/
    // RecentlyAddedAlbumRows - so building two full-library tile grids on every
    // drill-in was pure wasted work, confirmed on a real device as a large
    // chunk of the pause after tapping Back.
    //
    // This was five hand-rolled copies of the same five lines
    // (docs/ARCHITECTURE-REVIEW.md Tier 4.2's parked mobile work); the comments
    // above were duplicated with them, three times each.
    private async Task DrillIntoAsync(SidebarItem? sidebarItem = null, string? subItem = null, DrillLevel level = DrillLevel.TrackList)
    {
        // Drilling in is always into one thing - an album, an artist's album,
        // a playlist - and none of those is the flat Songs list.
        UseTheSortThisScreenWants(flatSongs: false);
        if (sidebarItem != null)
            Main.SelectedSidebarItem = sidebarItem;
        if (subItem != null)
            Main.SelectedSubItem = subItem;

        await Main.RebuildRowsImmediatelyAsync(includeGridTiles: false);

        if (level == DrillLevel.ArtistAlbum)
            _hasDrilledIntoArtistAlbum = true;
        else
            _hasDrilledIn = true;
        RaiseNavigationChanged();
    }

    // The Albums sidebar scope, which three of the drill-ins below borrow:
    // they render an album's tracks by reusing the Albums tab's own filtering
    // rather than each maintaining a separately-scoped track list.
    private SidebarItem? AlbumsScope =>
        Main.SidebarItems.FirstOrDefault(i => i.Kind == SidebarItemKind.Albums);

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
    private void SelectArtistAlbum(string? albumName)
    {
        if (albumName == null)
            return;
        PushHistory();
        DrillIntoAsync(AlbumsScope, albumName, DrillLevel.ArtistAlbum)
            .Forget(_logger, "Artist album drill-in");
    }

    // Tapping a tile in the Recently Added grid drills into that album's
    // tracks by reusing the Albums tab's own filtering (Main.SelectedSidebarItem
    // set to the Albums sidebar item, then SelectedSubItem to the album name) -
    // ApplyTabSelection does not do this for MobileTab.RecentlyAdded itself
    // (the un-drilled-in grid renders its own RecentlyAddedAlbumRows collection,
    // not Main.Rows), so it is set explicitly here instead. SelectedTab stays
    // RecentlyAdded so Back returns to this grid, not to the Albums picker.
    private void SelectRecentlyAddedAlbum(string? albumName)
    {
        if (albumName == null)
            return;
        PushHistory();
        DrillIntoAsync(AlbumsScope, albumName).Forget(_logger, "Recently Added drill-in");
    }

    private void SelectPlaylist(SidebarItem? item)
    {
        if (item == null)
            return;
        PushHistory();
        DrillIntoAsync(item).Forget(_logger, "Playlist drill-in");
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
        if (frame.Tab != leaving.Tab)
            RememberTab(leaving);
        _forwardHistory.Push(leaving);

        await ApplyFrame(frame, goingBack: true);
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
        // Before the push, so the run of this tab's screens under it is read
        // off the history as it was. Back remembers only when it crosses to
        // another tab too, and there the popped destination is on top and is
        // not this tab's, so nothing under the screen being left is taken.
        if (frame.Tab != leaving.Tab)
            RememberTab(leaving);
        _navigationHistory.Push(leaving);

        await ApplyFrame(frame, goingBack: false);
    }

    // Restores a popped frame's state and rebuilds whatever needs it -
    // shared by GoBack and GoForward, which differ only in which stack they
    // pop from/push the outgoing screen onto (see both above).
    //
    // restoringTab is a tab tap landing on what that tab was showing (see
    // RestoreTab): a forward navigation, so it keeps the entrance the tap set
    // and leaves the sheet alone.
    private async Task ApplyFrame(MobileNavigationFrame frame, bool goingBack, bool restoringTab = false)
    {
        // Back/Forward animate the OUTGOING screen off (ScreenStackPanel's own
        // easing, whether from a swipe or the back button), revealing the one
        // already sitting underneath - nothing slides in on top, so no
        // entrance transition is pending for the frame being restored.
        if (!restoringTab)
            _pendingTransition = MobileNavigationTransition.None;
        _selectedTab = frame.Tab;
        _hasDrilledIn = frame.HasDrilledIn;
        _selectedArtistName = frame.SelectedArtistName;
        _hasDrilledIntoArtistAlbum = frame.HasDrilledIntoArtistAlbum;
        // The restored screen's own order, the same as if it had been
        // navigated to forwards - back out of an album and the library is
        // alphabetical again.
        UseTheSortThisScreenWants(flatSongs: frame.Tab == MobileTab.Songs && !frame.HasDrilledIn);
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
        // After the await, so a NavigationChanged raised for something else in
        // the meantime cannot take it for the screen still showing.
        _restoredFrame = frame;
        RaiseNavigationChanged();
        if (restoringTab)
            return;
        // Last, over a destination that is fully built: a sheet is raised on
        // top of a screen, so opening it before that screen is ready would
        // animate it over the outgoing one.
        ApplySheet(frame.Sheet, goingBack);
    }

    // Restores the sheet half of a frame, moving in whichever direction the
    // navigation itself is going. Back returns the sheet by the edge it was
    // pushed out of and, when it is instead the sheet being left behind,
    // retreats it the ordinary way; Forward is the exact mirror, which is what
    // makes a redo look like the push it is replaying rather than another back
    // step. See SlidingSheet.EntersBackward/ExitsForward.
    private void ApplySheet(MobileSheet sheet, bool goingBack)
    {
        if (sheet == ActiveSheet)
            return;
        if (sheet == MobileSheet.NowPlaying)
            NowPlayingEntersBackward = goingBack;
        else if (ActiveSheet == MobileSheet.NowPlaying)
            NowPlayingExitsForward = !goingBack;
        ActiveSheet = sheet;
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
    public void SwipeBack() => SwipeBackAsync().Forget(_logger, "Swipe back");

    private async Task SwipeBackAsync()
    {
        if (CanGoBack)
        {
            await GoBack();
            return;
        }
        if (SelectedTab > FirstTab)
            SelectedTab = SelectedTab - 1;
    }

    public void SwipeForward() => SwipeForwardAsync().Forget(_logger, "Swipe forward");

    private async Task SwipeForwardAsync()
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
        OnPropertyChanged(nameof(IsShowingAlbumGrid));
        OnPropertyChanged(nameof(IsShowingArtistPicker));
        OnPropertyChanged(nameof(IsShowingArtistAlbumGrid));
        OnPropertyChanged(nameof(IsShowingPlaylistPicker));
        OnPropertyChanged(nameof(IsShowingRecentlyAddedAlbums));
        OnPropertyChanged(nameof(IsShowingSearchPrompt));
        OnPropertyChanged(nameof(IsShowingSearchResults));
        OnPropertyChanged(nameof(IsShowingTrackList));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CurrentPlaylist));
        OnPropertyChanged(nameof(IsShowingPlaylistTracks));
        OnPropertyChanged(nameof(IsShowingAlbumTrackList));
        OnPropertyChanged(nameof(AlbumDetailRows));
        RaiseDetailHeaderChanged();
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
    public void ReorderCurrentPlaylistTrack(Track dragged, Track? insertBefore)
    {
        if (CurrentPlaylist is not { } playlist)
            return;
        Main.ReorderPlaylistTrack(playlist, dragged, insertBefore)
            .Forget(_logger, "Playlist track reorder");
    }
}
