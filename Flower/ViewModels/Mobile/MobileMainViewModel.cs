using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;

using CommunityToolkit.Mvvm.Input;

using Flower.Models;

namespace Flower.ViewModels.Mobile;

public enum MobileTab { Songs, Albums, Artists, Playlists }

// Full-screen overlays shown on top of the tab content, e.g. the expanded
// now-playing view opened by tapping the mini-player.
public enum MobileSheet { None, NowPlaying }

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

    public ICommand SelectTabCommand { get; }
    public ICommand SelectAlbumOrArtistCommand { get; }
    public ICommand SelectPlaylistCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand PlayTrackCommand { get; }
    public ICommand ToggleMiniPlayerCommand { get; }
    public ICommand OpenNowPlayingCommand { get; }
    public ICommand CloseSheetCommand { get; }
    public ICommand NextTrackCommand { get; }
    public ICommand PreviousTrackCommand { get; }

    private MobileTab _selectedTab = MobileTab.Songs;
    public MobileTab SelectedTab
    {
        get => _selectedTab;
        private set
        {
            if (_selectedTab == value) return;
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

    public bool IsShowingAlbumArtistPicker => (SelectedTab is MobileTab.Albums or MobileTab.Artists) && !_hasDrilledIn;
    public bool IsShowingPlaylistPicker => SelectedTab == MobileTab.Playlists && !_hasDrilledIn;
    public bool IsShowingTrackList => !IsShowingAlbumArtistPicker && !IsShowingPlaylistPicker;
    public bool CanGoBack => _hasDrilledIn;

    public string ScreenTitle => _hasDrilledIn
        ? (Main.SelectedSidebarItem?.Name ?? SelectedTab.ToString())
        : SelectedTab.ToString();

    private MobileSheet _activeSheet = MobileSheet.None;
    public MobileSheet ActiveSheet
    {
        get => _activeSheet;
        private set
        {
            if (_activeSheet == value) return;
            _activeSheet = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsShowingNowPlaying));
        }
    }

    public bool IsShowingNowPlaying => ActiveSheet == MobileSheet.NowPlaying;

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

        Main.SidebarItems.CollectionChanged += (_, _) => RebuildPlaylistPicker();
        RebuildPlaylistPicker();
        ApplyTabSelection();

        SelectTabCommand = new RelayCommand<string>(name =>
        {
            if (name != null && System.Enum.TryParse<MobileTab>(name, out var tab))
                SelectedTab = tab;
        });
        SelectAlbumOrArtistCommand = new RelayCommand<string>(SelectAlbumOrArtist);
        SelectPlaylistCommand = new RelayCommand<SidebarItem>(SelectPlaylist);
        BackCommand = new RelayCommand(GoBack);
        PlayTrackCommand = new RelayCommand<Track>(track =>
        {
            if (track != null) PlaylistControl.PlayOrPause(track);
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
    }

    private void RebuildPlaylistPicker()
    {
        PlaylistPickerItems.Clear();
        foreach (var item in Main.SidebarItems.Where(i => i.Kind == SidebarItemKind.Playlist))
            PlaylistPickerItems.Add(item);
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
        if (name == null) return;
        Main.SelectedSubItem = name;
        _hasDrilledIn = true;
        RaiseNavigationChanged();
    }

    private void SelectPlaylist(SidebarItem? item)
    {
        if (item == null) return;
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
        OnPropertyChanged(nameof(IsShowingAlbumArtistPicker));
        OnPropertyChanged(nameof(IsShowingPlaylistPicker));
        OnPropertyChanged(nameof(IsShowingTrackList));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(ScreenTitle));
    }
}
