using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Flower.Controls;
using Flower.Models;
using Microsoft.Extensions.Logging;

using Flower.Services;
using Flower.ViewModels.Mobile;

namespace Flower.ViewModels;

// The slice of sidebar state the browser needs to know what it is showing.
// Everything else about the sidebar - what rows exist, how they are selected,
// which device is which - stays with MainViewModel; this is only "what scope
// am I browsing right now, and what should a row look like in it".
public interface ILibraryBrowseHost
{
    SidebarItemKind? CurrentKind { get; }
    Playlist? CurrentPlaylist { get; }
    Track? CurrentlyPlayingTrack { get; }

    // Both feed TrackListBuilder's per-row availability marking - a track whose
    // origin peer is the paired server shows as streamable only while that
    // server is reachable. See TrackAvailability.
    string? PairedServerFingerprint { get; }
    bool IsPairedServerReachable { get; }

    // Persist a sort choice into AppSettings. Only Songs' sort is persisted -
    // Recently Added's and History's are per-session, same as they always were.
    void PersistSort(string column, bool ascending);
    void PersistSortArtistAlbumsByYear(bool value);
}

// Everything about *displaying* the library: the flat row list, the search
// filter, the three independent sort states, the album/Recently Added tile
// grids, the Artists sub-list, and the status bar summarising them. Split out
// of MainViewModel, where it was one of six unrelated jobs (see
// docs/ARCHITECTURE-REVIEW.md Tier 4.2).
public sealed class LibraryBrowserViewModel : ViewModelBase
{
    private readonly Library _library;
    private readonly ILibraryBrowseHost _host;
    // Threaded down to every row this builds so the download spinner animates
    // on the container's clock. Optional because the rows are also built by
    // static builders that have no container behind them - see
    // TrackRowViewModel.Clock.
    private readonly AnimationClock? _animationClock;

    public LibraryBrowserViewModel(
        Library library,
        ILibraryBrowseHost host,
        ILogger<LibraryBrowserViewModel> logger,
        AnimationClock? animationClock = null)
    {
        _library        = library;
        _host           = host;
        _logger         = logger;
        _animationClock = animationClock;
    }

    private readonly ILogger _logger;

    // ── Rows (flat list for MusicListView) ────────────────────────────────

    private ObservableCollection<TrackRowViewModel> _rows = new();
    public ObservableCollection<TrackRowViewModel> Rows
    {
        get => _rows;
        private set { _rows = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusBarText)); }
    }

    // ── Status bar ────────────────────────────────────────────────────────

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

    // ── Sort state ────────────────────────────────────────────────────────

    private string _sortColumn    = "TrackNumber";
    private bool   _sortAscending = true;

    // Recently Added has its own independent sort state (defaulting to newest-first)
    // rather than sharing Songs/Albums/Artists' single sort column - so clicking a
    // header there doesn't change what Songs is sorted by, and vice versa.
    private string _recentlyAddedSortColumn    = "DateAdded";
    private bool   _recentlyAddedSortAscending = false;

    // History gets the same independent-sort-state treatment as Recently Added,
    // and for the same reason - defaults to newest-played-first rather than
    // sharing/clobbering Songs' sort column.
    private string _historySortColumn    = "LastPlayed";
    private bool   _historySortAscending = false;

    private bool IsViewingRecentlyAdded => _host.CurrentKind == SidebarItemKind.RecentlyAdded;
    private bool IsViewingHistory => _host.CurrentKind == SidebarItemKind.History;

    public string SortColumn => IsViewingRecentlyAdded ? _recentlyAddedSortColumn : IsViewingHistory ? _historySortColumn : _sortColumn;

    public bool SortAscending => IsViewingRecentlyAdded ? _recentlyAddedSortAscending : IsViewingHistory ? _historySortAscending : _sortAscending;

    // Restores the persisted Songs sort at startup - AppSettings is read by
    // MainViewModel, which owns the settings object.
    public void RestoreSort(string column, bool ascending, bool artistAlbumsByYear)
    {
        _sortColumn = column;
        _sortAscending = ascending;
        _sortArtistAlbumsByYear = artistAlbumsByYear;
    }

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
            _host.PersistSortArtistAlbumsByYear(value);
            if (SortColumn == "Artist")
                ScheduleFilter();
        }
    }

    public void SortByColumn(string? columnId)
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
            NotifySortChanged();
            ScheduleFilter();
            return;
        }

        if (IsViewingHistory)
        {
            if (_historySortColumn == columnId)
                _historySortAscending = !_historySortAscending;
            else
            {
                _historySortColumn    = columnId;
                _historySortAscending = true;
            }
            NotifySortChanged();
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
        NotifySortChanged();
        ScheduleFilter();
        _host.PersistSort(_sortColumn, _sortAscending);
    }

    // Recently Added and History carry their own independent sort state, so
    // switching to or from either changes what SortColumn/SortAscending report
    // without anything actually being re-sorted - MainViewModel calls this on
    // every sidebar selection change for that reason.
    public void NotifySortChanged()
    {
        OnPropertyChanged(nameof(SortColumn));
        OnPropertyChanged(nameof(SortAscending));
    }

    // ── Filter ────────────────────────────────────────────────────────────

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

    // ── Expanded album (inline within either tile grid) ───────────────────

    // The one album (if any) currently expanded inline within whichever grid
    // is showing - see AlbumGridView/AlbumGridRowControl for the actual
    // expand/collapse rendering+animation. Deliberately independent of
    // SelectedSubItems (Ctrl/Shift multi-select for drag-to-playlist, see
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
    public void ToggleAlbumExpanded(string? albumName)
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

    // Every fresh visit to Albums/Recently Added starts with nothing expanded,
    // never a remembered one - matches mobile, where switching tabs and back
    // always starts at the flat grid too.
    public void CollapseExpandedAlbum()
    {
        ExpandedAlbumName = null;
        ExpandedAlbumTracks = new ObservableCollection<Track>();
    }

    public ObservableCollection<Track> BuildExpandedAlbumTracks(string albumName) =>
        new(_allTracks.Where(t => t.Album == albumName)
            .OrderBy(t => t.DiscNumber)
            .ThenBy(t => t.TrackNumber));

    // ── Tile grids ────────────────────────────────────────────────────────

    // Rebuilt in Repopulate (every TracksUpdated) - see AlbumGridBuilder/
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
        ApplyTileAvailability();

        // An expanded album's tracks were resolved against the previous
        // _allTracks snapshot - refresh them so a library change (a rescan,
        // a download completing, a tag edit) while expanded doesn't leave it
        // showing stale Track references.
        if (_expandedAlbumName != null)
            ExpandedAlbumTracks = BuildExpandedAlbumTracks(_expandedAlbumName);
    }

    // ── Sub-list (Artists picker) ─────────────────────────────────────────

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

    private void ApplySubItemSelection(IReadOnlyList<string> items) => ApplySubItemSelection(items, immediate: false);

    // immediate=true bypasses ScheduleFilter's 250ms debounce the same way
    // RebuildRowsImmediatelyAsync's other callers do - used only by
    // MainViewModel's OnSidebarSelectionChanged, a single discrete navigation
    // (sidebar click), not the rapid-fire callers (typing a search query,
    // sub-list drag-multi-select) that still want the debounce. Without this,
    // switching sidebar views showed the previous view's stale rows for the
    // debounce's own delay before the new view's rows appeared - visible as
    // a flash of the old view on every switch, most noticeable jumping to
    // Songs from a small filtered view.
    public void ApplySubItemSelection(IReadOnlyList<string> items, bool immediate)
    {
        _selectedSubItems = new HashSet<string>(items);
        _selectedSubItem  = items.Count > 0 ? items[0] : null;
        RememberSubItemSelection(_selectedSubItem);
        OnPropertyChanged(nameof(SelectedSubItem));
        OnPropertyChanged(nameof(SelectedSubItems));
        if (immediate)
            _ = RebuildRowsImmediatelyAsync();
        else
            ScheduleFilter();
    }

    private void RememberSubItemSelection(string? value)
    {
        if (value == null)
            return;
        if (_host.CurrentKind == SidebarItemKind.Artists)
            _lastSelectedArtist = value;
    }

    // Albums no longer auto-selects an album on a fresh visit - it starts at
    // the grid instead, matching mobile's Albums tab. Only Artists keeps the
    // old auto-select-first/remembered behavior, since its plain-text picker is
    // unchanged.
    public string? InitialSubItemForCurrentView() =>
        _host.CurrentKind == SidebarItemKind.Artists
            ? (_lastSelectedArtist != null && _subListItems.Contains(_lastSelectedArtist)
                ? _lastSelectedArtist
                : _subListItems.FirstOrDefault())
            : null;

    public void RebuildSubListItems()
    {
        if (_host.CurrentKind == SidebarItemKind.Albums)
            SubListItems = new ObservableCollection<string>(
                _allTracks.Select(t => t.Album!).Where(a => !string.IsNullOrEmpty(a)).Distinct().OrderBy(a => a));
        else if (_host.CurrentKind == SidebarItemKind.Artists)
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
        return _host.CurrentKind switch
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
        return _host.CurrentKind switch
        {
            SidebarItemKind.Playlist when _host.CurrentPlaylist != null
                => new List<Track>(_host.CurrentPlaylist.Tracks),
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
            // Never-played tracks would otherwise just clutter the bottom/top of
            // a "newest played first" sort with a meaningless null - History only
            // makes sense for tracks that actually have a LastPlayedAt.
            SidebarItemKind.History
                => _allTracks.Where(t => t.LastPlayedAt != null).ToList(),
            _ => _allTracks
        };
    }

    // Identifies the currently displayed track list (Songs / a given album /
    // artist / playlist) so the view can remember a separate scroll position
    // and selection for each one.
    public string CurrentViewKey => _host.CurrentKind switch
    {
        // Keyed on the whole set (sorted, so order doesn't matter) rather than
        // just the primary item - otherwise two different multi-selections that
        // happen to share the same first-selected item would collide and
        // incorrectly share saved scroll/selection state in ApplyRows.
        SidebarItemKind.Albums        => $"album:{string.Join('\u0001', _selectedSubItems.OrderBy(s => s))}",
        SidebarItemKind.Artists       => $"artist:{string.Join('\u0001', _selectedSubItems.OrderBy(s => s))}",
        SidebarItemKind.Playlist      => $"playlist:{_host.CurrentPlaylist?.Name}",
        SidebarItemKind.RecentlyAdded => "recently-added",
        SidebarItemKind.History       => "history",
        _                             => "songs"
    };

    // ── Rebuild pipeline ──────────────────────────────────────────────────

    // Re-snapshots the library and rebuilds everything derived from it - called
    // on every Library.TracksUpdated.
    public void Repopulate()
    {
        _allTracks = new List<Track>(_library.Tracks);
        _logger.LogInformation("Library view repopulated: {Count} track(s)", _allTracks.Count);
        RebuildSubListItems();
        RebuildAlbumGrids();
        ScheduleFilter();
    }

    // Was async void - a throw here (rather than the OperationCanceledException
    // it already handles) would tear the process down, since
    // TaskScheduler.UnobservedTaskException does not observe async void. See
    // ARCHITECTURE-REVIEW's "async void on non-event-handler paths" note.
    // Forget() rather than a bare `_ =` so an unexpected fault is observed and
    // logged rather than vanishing into a Task nobody awaits.
    public void ScheduleFilter() => ScheduleFilterAsync().Forget(_logger, "Filter rebuild");

    private async Task ScheduleFilterAsync()
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

    private async Task RebuildRowsAsync(CancellationToken token, bool includeGridTiles = true)
    {
        var text       = _filterText;
        // Playlists have a user-defined (drag-reorderable) track order rather
        // than a sortable one, so ignore the column sort while viewing one.
        // Recently Added uses its own independent sort state (see SortColumn).
        var sortCol    = _host.CurrentKind == SidebarItemKind.Playlist ? "PlaylistOrder" : SortColumn;
        var sortAsc    = SortAscending;
        var playing    = _host.CurrentlyPlayingTrack;
        var baseTracks = GetBaseTracksForFilter();
        var allTracks  = _allTracks;
        var pairedServerFingerprint = _host.PairedServerFingerprint;
        var pairedServerReachable   = _host.IsPairedServerReachable;

        // Albums/Recently Added show a tile grid instead of Rows built straight
        // from _allTracks, not from GetBaseTracksForFilter's (mostly
        // Albums-view-irrelevant) result - so without this, FilterText had no
        // effect on either grid at all. Rebuilt here, alongside Rows, on every
        // filter/sort/view change rather than only on a rescan (see
        // RebuildAlbumGrids), so typing in the search box while on Albums
        // actually narrows the grid.
        //
        // includeGridTiles=false skips both builds entirely - mobile (the
        // only caller that passes false, via RebuildRowsImmediatelyAsync)
        // has its own separate AlbumGridRows/RecentlyAddedAlbumRows on
        // MobileMainViewModel (rebuilt only on library changes, not on every
        // navigation - see RebuildAlbumGrid/RebuildRecentlyAddedAlbums
        // there) and never reads AlbumGridTiles/RecentlyAddedGridTiles at
        // all, so building two full-library tile grids on every single
        // drill-in/back-navigation was pure wasted work there - confirmed on
        // a real device as a large chunk of the pause after tapping Back.
        // Desktop got the same treatment mobile already had, just derived
        // rather than passed in: the two tile grids are only ever *painted* on
        // the Albums and Recently Added views, yet every rebuild built both -
        // two full passes over the whole library, discarded unread, on every
        // keystroke and every drill-in while viewing Songs, Artists or a
        // playlist. Switching to either grid view runs MainViewModel's
        // OnSidebarSelectionChanged, which rebuilds through here again, so they
        // are always built before the view that reads them appears. See
        // ARCHITECTURE-REVIEW Tier 1.5.
        var buildGrids = includeGridTiles &&
            _host.CurrentKind is SidebarItemKind.Albums or SidebarItemKind.RecentlyAdded;

        // Only the plan - filter, sort and album grouping over plain Tracks -
        // runs off the UI thread. Turning it into rows is a UI-thread job now
        // that rows are reused rather than reallocated (see TrackRowMerge):
        // ApplyPlan writes to instances that are live and bound, and raises
        // PropertyChanged on them.
        var (plan, albumTiles, recentTiles) = await Task.Run(() =>
        {
            var builtPlan = TrackListBuilder.Plan(baseTracks, text, sortCol, sortAsc, playing, _sortArtistAlbumsByYear, pairedServerFingerprint, pairedServerReachable);
            if (!buildGrids)
                return (builtPlan, (List<AlbumTileViewModel>?)null, (List<AlbumTileViewModel>?)null);

            var filteredForGrids = TrackListBuilder.Filter(allTracks, text).ToList();
            return (
                builtPlan,
                AlbumGridBuilder.Build(filteredForGrids),
                RecentlyAddedAlbumsBuilder.Build(filteredForGrids));
        }, token);

        if (token.IsCancellationRequested)
            return;

        var rows = TrackRowMerge.Apply(_rows, plan, out var retired, _animationClock);

        _currentFilteredTracks = new List<Track>(plan.Count);
        foreach (var entry in plan)
            _currentFilteredTracks.Add(entry.Track);

        // Only the rows that did *not* survive the merge are dropped on the
        // floor here, so anything they own that isn't purely managed memory has
        // to be released explicitly - in practice the download spinner's
        // animation-clock subscription, which otherwise outlives the row it
        // belonged to (see TrackRowViewModel.Dispose). Disposing the reused
        // ones would kill a spinner that is still on screen and still
        // downloading.
        // Debug, not Information: this runs on every keystroke in the search
        // box, not just on a library change - unlike Repopulate's own line
        // above, which is the once-per-library-update counterpart to Library's
        // "Library updated" and is what tells the two apart in a log.
        _logger.LogTrace("Rows rebuilt: {Rows} row(s) from {Base} track(s) of {All} ({Kind})",
            rows.Count, baseTracks.Count, allTracks.Count, _host.CurrentKind);

        Rows = new ObservableCollection<TrackRowViewModel>(rows);
        foreach (var row in retired)
            row.Dispose();
        if (buildGrids)
        {
            AlbumGridTiles = new ObservableCollection<AlbumTileViewModel>(albumTiles!);
            RecentlyAddedGridTiles = new ObservableCollection<AlbumTileViewModel>(recentTiles!);
            ApplyTileAvailability();
        }
        OnPropertyChanged(nameof(StatusBarText));
    }

    // Bypasses ScheduleFilter's own 250ms debounce - meant for a single,
    // discrete navigation action (mobile drilling into a specific album/
    // playlist - see MobileMainViewModel's SelectAlbumOrArtistCore/
    // SelectArtistAlbum/SelectRecentlyAddedAlbum/SelectPlaylist) rather than
    // a rapid-fire one like typing a search query or desktop's own sub-list
    // multi-select drag, which still want the debounce and so still go
    // through SelectedSubItem's own setter/ScheduleFilter as before.
    // Without this, drilling into an album showed the previous scope's
    // tracks (or the whole library) for up to the debounce's own delay
    // before the newly-scoped list actually appeared - confirmed on a real
    // device as a ~500ms flash of a different album's songs on every
    // drill-in. Returns false if superseded by a newer filter/navigation
    // change before this one finished, so a caller with its own
    // follow-up work (see GoToCurrentlyPlayingTrackAsync) can skip it.
    //
    // includeGridTiles defaults to true so the one desktop caller
    // (GoToCurrentlyPlayingTrackAsync, which can run while Albums/Recently
    // Added is the active sidebar view) keeps getting fresh grid tiles
    // without having to know to ask for them - mobile's 5 call sites pass
    // false explicitly instead, since mobile never reads them at all.
    public async Task<bool> RebuildRowsImmediatelyAsync(bool includeGridTiles = true)
    {
        _filterCts?.Cancel();
        _filterCts = new CancellationTokenSource();
        var token = _filterCts.Token;
        try
        {
            await RebuildRowsAsync(token, includeGridTiles);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    // ── Row-level refreshes ───────────────────────────────────────────────

    public void UpdatePlayingIndicators()
    {
        var playing = _host.CurrentlyPlayingTrack;
        foreach (var row in _rows)
            row.IsCurrentlyPlaying = playing != null && row.Track.Id == playing.Id;
    }

    // A play-count / LastPlayedAt bump only affects two columns on one row -
    // re-raise exactly those rather than rebuilding every row.
    public void NotifyTrackStatsChanged(Track track)
    {
        foreach (var row in _rows)
        {
            if (row.Track.Id == track.Id)
            {
                row.NotifyStatsChanged();
                break;
            }
        }
    }

    public void ApplyTrackAvailability(string? pairedServerFingerprint, bool reachable)
    {
        TrackAvailability.Apply(_rows, pairedServerFingerprint, reachable);
        ApplyTileAvailability();
    }

    // The tile grids are rebuilt far less often than the rows are (only on a
    // library change, or on entering one of the two views that paint them),
    // so they need re-marking on their own whenever the server's reachability
    // moves under them - otherwise a grid built while the server was up stays
    // at full strength for as long as it is left on screen. Reads the host
    // rather than taking parameters: unlike the rows, this also runs from
    // RebuildAlbumGrids, which has no reachability arguments to hand.
    private void ApplyTileAvailability()
    {
        TrackAvailability.Apply(AlbumGridTiles, _host.PairedServerFingerprint, _host.IsPairedServerReachable);
        TrackAvailability.Apply(RecentlyAddedGridTiles, _host.PairedServerFingerprint, _host.IsPairedServerReachable);
    }
}
