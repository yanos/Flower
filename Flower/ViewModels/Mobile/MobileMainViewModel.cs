using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;

using Avalonia.Threading;

using CommunityToolkit.Mvvm.Input;

using Flower.Models;
using Flower.Services;

namespace Flower.ViewModels.Mobile;

// RecentlyAdded is first/default (see MobileMainViewModel's _selectedTab) - an
// album grid ordered by recency, the app's home screen. The other four mirror
// desktop's Songs/Albums/Artists/Playlists sidebar sections.
public enum MobileTab { RecentlyAdded, Songs, Albums, Artists, Playlists }

// Full-screen overlays shown on top of the tab content, e.g. the expanded
// now-playing view opened by tapping the mini-player.
public enum MobileSheet { None, NowPlaying, TrackActions, TrackInfo, AddToPlaylist, Settings, PeerApproval, ConfirmPairServer }

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

    public ICommand SelectTabCommand { get; }
    public ICommand SelectAlbumOrArtistCommand { get; }
    public ICommand SelectArtistCommand { get; }
    public ICommand SelectArtistAlbumCommand { get; }
    public ICommand SelectRecentlyAddedAlbumCommand { get; }
    public ICommand SelectPlaylistCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand PlayTrackCommand { get; }
    public ICommand ToggleMiniPlayerCommand { get; }
    public ICommand OpenNowPlayingCommand { get; }
    public ICommand CloseSheetCommand { get; }
    public ICommand NextTrackCommand { get; }
    public ICommand PreviousTrackCommand { get; }
    public ICommand OpenTrackActionsCommand { get; }
    public ICommand ViewTrackInfoCommand { get; }
    public ICommand ToggleSearchCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand OpenAddToPlaylistCommand { get; }
    public ICommand AddTrackToPlaylistCommand { get; }
    public ICommand CreatePlaylistCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand RescanCommand { get; }
    public ICommand OpenAppSettingsCommand { get; }
    public ICommand AllowPeerCommand { get; }
    public ICommand DenyPeerCommand { get; }
    public ICommand DownloadTrackCommand { get; }
    public ICommand PairWithServerCommand { get; }
    public ICommand UnpairServerCommand { get; }
    public ICommand ConfirmPairServerCommand { get; }
    public ICommand CancelPairServerCommand { get; }

    private MobileTab _selectedTab = MobileTab.RecentlyAdded;
    public MobileTab SelectedTab
    {
        get => _selectedTab;
        private set
        {
            if (_selectedTab == value)
                return;
            _selectedTab = value;
            _hasDrilledIn = false;
            _selectedArtistName = null;
            _hasDrilledIntoArtistAlbum = false;
            ApplyTabSelection();
            OnPropertyChanged();
            RaiseNavigationChanged();
        }
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
    public bool IsShowingTrackList =>
        !IsShowingAlbumGrid && !IsShowingArtistPicker && !IsShowingArtistAlbumGrid
        && !IsShowingPlaylistPicker && !IsShowingRecentlyAddedAlbums;
    public bool CanGoBack => _hasDrilledIn;

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

    // The search box only makes sense over a track list (Main.FilterText filters
    // Rows, which the Albums/Artists/Playlists picker screens do not use).
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
        private set { _actionTarget = value; OnPropertyChanged(); }
    }

    // Whichever list is currently on screen (picker or track list) has nothing in it.
    // Without this, an empty library or an empty search just renders a blank screen.
    public bool IsContentEmpty =>
        (IsShowingAlbumGrid && AlbumGridRows.Count == 0) ||
        (IsShowingArtistPicker && Main.SubListItems.Count == 0) ||
        (IsShowingArtistAlbumGrid && ArtistAlbumGridRows.Count == 0) ||
        (IsShowingPlaylistPicker && PlaylistPickerItems.Count == 0) ||
        (IsShowingRecentlyAddedAlbums && RecentlyAddedAlbumRows.Count == 0) ||
        (IsShowingTrackList && Main.Rows.Count == 0);

    public string EmptyStateTitle
    {
        get
        {
            if (IsShowingPlaylistTracks)
                return "Playlist is Empty";
            if (!string.IsNullOrEmpty(Main.FilterText))
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
            if (IsShowingPlaylistTracks)
                return "Add tracks from a track's ... menu.";
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
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateMessage));
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
        });
        Main.PropertyChanged += (_, e) =>
        {
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
        SelectArtistAlbumCommand = new RelayCommand<string>(SelectArtistAlbum);
        SelectRecentlyAddedAlbumCommand = new RelayCommand<string>(SelectRecentlyAddedAlbum);
        SelectPlaylistCommand = new RelayCommand<SidebarItem>(SelectPlaylist);
        BackCommand = new RelayCommand(GoBack);
        PlayTrackCommand = new RelayCommand<Track>(track =>
        {
            // Always starts this track, mirroring desktop's row-activation handler
            // (MainView.axaml.cs calls Play, not PlayOrPause, on Enter/double-click).
            // PlayOrPause ignores its track argument whenever something is already
            // playing, so reusing it here paused instead of switching tracks.
            // Path == null means not yet downloaded (see SYNC-PLAN.md Phase 3) -
            // tapping such a row does nothing in v1; only the row's own download
            // icon (DownloadTrackCommand) is actionable for it.
            if (track is { Path: not null })
                PlaylistControl.Play(track);
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

            row.IsDownloadUnavailable = false;
            row.IsDownloading = true;
            var result = await Main.DownloadTrackAsync(row.Track);
            row.IsDownloading = false;
            row.IsDownloadUnavailable = result is TrackDownloadResult.PeerUnavailable or TrackDownloadResult.Failed;
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
    private void SelectAlbumOrArtist(string? name)
    {
        if (name == null)
            return;
        Main.SelectedSubItem = name;
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
        Main.SelectedSidebarItem = Main.SidebarItems.FirstOrDefault(i => i.Kind == SidebarItemKind.Albums);
        Main.SelectedSubItem = albumName;
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
    private void SelectRecentlyAddedAlbum(string? albumName)
    {
        if (albumName == null)
            return;
        Main.SelectedSidebarItem = Main.SidebarItems.FirstOrDefault(i => i.Kind == SidebarItemKind.Albums);
        Main.SelectedSubItem = albumName;
        _hasDrilledIn = true;
        RaiseNavigationChanged();
    }

    private void SelectPlaylist(SidebarItem? item)
    {
        if (item == null)
            return;
        Main.SelectedSidebarItem = item;
        _hasDrilledIn = true;
        RaiseNavigationChanged();
    }

    private void GoBack()
    {
        // Artists has two levels to unwind one at a time, unlike everything
        // else's single level - see _selectedArtistName/_hasDrilledIntoArtistAlbum.
        if (SelectedTab == MobileTab.Artists && _hasDrilledIntoArtistAlbum)
        {
            // Undo SelectArtistAlbum's temporary re-point of SelectedSidebarItem
            // at Albums, back to the artist's own album grid - _selectedArtistName
            // itself is untouched, so ArtistAlbumGridRows is still exactly as left.
            _hasDrilledIntoArtistAlbum = false;
            Main.SelectedSidebarItem = Main.SidebarItems.FirstOrDefault(i => i.Kind == SidebarItemKind.Artists);
            RaiseNavigationChanged();
            return;
        }
        if (SelectedTab == MobileTab.Artists && _selectedArtistName != null)
        {
            _selectedArtistName = null;
            _hasDrilledIn = false;
            ApplyTabSelection();
            RaiseNavigationChanged();
            return;
        }

        _hasDrilledIn = false;
        ApplyTabSelection();
        RaiseNavigationChanged();
    }

    private void RaiseNavigationChanged()
    {
        if (!IsShowingTrackList)
            IsSearchVisible = false;

        OnPropertyChanged(nameof(IsShowingAlbumGrid));
        OnPropertyChanged(nameof(IsShowingArtistPicker));
        OnPropertyChanged(nameof(IsShowingArtistAlbumGrid));
        OnPropertyChanged(nameof(IsShowingPlaylistPicker));
        OnPropertyChanged(nameof(IsShowingRecentlyAddedAlbums));
        OnPropertyChanged(nameof(IsShowingTrackList));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(ScreenTitle));
        OnPropertyChanged(nameof(CurrentPlaylist));
        OnPropertyChanged(nameof(IsShowingPlaylistTracks));
        OnPropertyChanged(nameof(CanSearch));
        RaiseEmptyStateChanged();
    }

    // Driven by the track list's touch drag-to-reorder gesture (see MobileMainView's
    // code-behind); a no-op if the user isn't currently viewing a playlist's tracks.
    public async void ReorderCurrentPlaylistTrack(Track dragged, Track? insertBefore)
    {
        if (CurrentPlaylist is { } playlist)
            await Main.ReorderPlaylistTrack(playlist, dragged, insertBefore);
    }
}
