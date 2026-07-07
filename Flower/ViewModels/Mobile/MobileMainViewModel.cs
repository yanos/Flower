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
public enum MobileSheet { None, NowPlaying, TrackActions, TrackInfo, AddToPlaylist, Settings, PeerApproval }

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
    public ObservableCollection<AlbumTileViewModel> RecentlyAddedAlbums { get; } = new();
    public ObservableCollection<AlbumTileViewModel> AlbumGridItems { get; } = new();

    public ICommand SelectTabCommand { get; }
    public ICommand SelectAlbumOrArtistCommand { get; }
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

    // Albums gets its own art-tile grid (same presentation as Recently Added,
    // see AlbumGridBuilder); Artists stays a plain name list - there is no
    // single representative image for an artist the way there is for an album.
    public bool IsShowingAlbumGrid => SelectedTab == MobileTab.Albums && !_hasDrilledIn;
    public bool IsShowingArtistPicker => SelectedTab == MobileTab.Artists && !_hasDrilledIn;
    public bool IsShowingPlaylistPicker => SelectedTab == MobileTab.Playlists && !_hasDrilledIn;
    public bool IsShowingRecentlyAddedAlbums => SelectedTab == MobileTab.RecentlyAdded && !_hasDrilledIn;
    public bool IsShowingTrackList =>
        !IsShowingAlbumGrid && !IsShowingArtistPicker && !IsShowingPlaylistPicker && !IsShowingRecentlyAddedAlbums;
    public bool CanGoBack => _hasDrilledIn;

    public string ScreenTitle => _hasDrilledIn
        ? (Main.SelectedSidebarItem?.Name ?? SelectedTab.ToString())
        : SelectedTab == MobileTab.RecentlyAdded ? "Recently Added" : SelectedTab.ToString();

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
        }
    }

    public bool IsShowingNowPlaying => ActiveSheet == MobileSheet.NowPlaying;
    public bool IsShowingTrackActions => ActiveSheet == MobileSheet.TrackActions;
    public bool IsShowingTrackInfo => ActiveSheet == MobileSheet.TrackInfo;
    public bool IsShowingAddToPlaylist => ActiveSheet == MobileSheet.AddToPlaylist;
    public bool IsShowingSettings => ActiveSheet == MobileSheet.Settings;
    public bool IsShowingPeerApproval => ActiveSheet == MobileSheet.PeerApproval;

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
        (IsShowingAlbumGrid && AlbumGridItems.Count == 0) ||
        (IsShowingArtistPicker && Main.SubListItems.Count == 0) ||
        (IsShowingPlaylistPicker && PlaylistPickerItems.Count == 0) ||
        (IsShowingRecentlyAddedAlbums && RecentlyAddedAlbums.Count == 0) ||
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

        Main.SidebarItems.CollectionChanged += (_, _) => RebuildPlaylistPicker();
        Main.Library.TracksUpdated += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            RebuildRecentlyAddedAlbums();
            RebuildAlbumGrid();
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
        ApplyTabSelection();

        SelectTabCommand = new RelayCommand<string>(name =>
        {
            if (name != null && System.Enum.TryParse<MobileTab>(name, out var tab))
                SelectedTab = tab;
        });
        SelectAlbumOrArtistCommand = new RelayCommand<string>(SelectAlbumOrArtist);
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
        RecentlyAddedAlbums.Clear();
        foreach (var album in RecentlyAddedAlbumsBuilder.Build(Main.Library.Tracks))
            RecentlyAddedAlbums.Add(album);

        RaiseEmptyStateChanged();
    }

    private void RebuildAlbumGrid()
    {
        AlbumGridItems.Clear();
        foreach (var album in AlbumGridBuilder.Build(Main.Library.Tracks))
            AlbumGridItems.Add(album);

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

    private void SelectAlbumOrArtist(string? name)
    {
        if (name == null)
            return;
        Main.SelectedSubItem = name;
        _hasDrilledIn = true;
        RaiseNavigationChanged();
    }

    // Tapping a tile in the Recently Added grid drills into that album's
    // tracks by reusing the Albums tab's own filtering (Main.SelectedSidebarItem
    // set to the Albums sidebar item, then SelectedSubItem to the album name) -
    // ApplyTabSelection does not do this for MobileTab.RecentlyAdded itself
    // (the un-drilled-in grid renders its own RecentlyAddedAlbums collection,
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
