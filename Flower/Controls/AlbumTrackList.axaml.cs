using System.Collections.Generic;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

using Flower.Models;
using Flower.Services;
using Flower.ViewModels;
using Flower.Views;

namespace Flower.Controls;

// One album's songs as a short list of their own - see the markup. Moved out of
// AlbumGridRowControl, which drew the Albums grid's expanded album this way,
// once the Artists page's album cards needed the same list.
public partial class AlbumTrackList : UserControl
{
    public static readonly StyledProperty<bool> TwoColumnsProperty =
        AvaloniaProperty.Register<AlbumTrackList, bool>(nameof(TwoColumns));

    public static readonly StyledProperty<bool> ShowsNumbersProperty =
        AvaloniaProperty.Register<AlbumTrackList, bool>(nameof(ShowsNumbers));

    // Read top to bottom then on into a second column - the Albums grid's
    // expanded album, which spans the width of the grid.
    public bool TwoColumns
    {
        get => GetValue(TwoColumnsProperty);
        set => SetValue(TwoColumnsProperty, value);
    }

    // Each song's track number ahead of it - the Artists page's cards.
    public bool ShowsNumbers
    {
        get => GetValue(ShowsNumbersProperty);
        set => SetValue(ShowsNumbersProperty, value);
    }

    public AlbumTrackList()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    private AlbumTracksViewModel? Album => DataContext as AlbumTracksViewModel;

    private void TrackRow_PointerEntered(object? sender, PointerEventArgs e)
    {
        if ((sender as Border)?.DataContext is ExpandedTrackRowViewModel row)
            row.IsHovered = true;
    }

    private void TrackRow_PointerExited(object? sender, PointerEventArgs e)
    {
        if ((sender as Border)?.DataContext is ExpandedTrackRowViewModel row)
            row.IsHovered = false;
    }

    // Click-to-select, with Ctrl/Cmd to toggle and Shift to extend - the same
    // gesture as the main track list's. Focus comes here so the arrows and
    // Enter below act on this list.
    private void TrackRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        if (sender is not Border border || border.DataContext is not ExpandedTrackRowViewModel row)
            return;

        var toggle = e.KeyModifiers.HasFlag(PlatformShortcuts.Primary);
        var rangeSelect = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        Album?.SelectTrack(row, toggle, rangeSelect);
        Focus();
        e.Handled = true;
    }

    private void TrackRow_DownloadRequested(object? sender, DownloadIndicatorViewModel indicator)
    {
        if (indicator is ExpandedTrackRowViewModel row && this.FindDataContext<MainViewModel>() is { } vm)
            _ = vm.Downloads.DownloadRowAsync(row);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Album is not { } album)
            return;

        switch (e.Key)
        {
            case Key.Down:
                album.MoveSelection(1, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                e.Handled = true;
                break;
            case Key.Up:
                album.MoveSelection(-1, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                e.Handled = true;
                break;
            case Key.Enter:
                if (album.CurrentTrack is { } track)
                    this.FindDataContext<MainViewModel>()?.PlayTrackInAlbum(album.ExpandedTracks, track);
                e.Handled = true;
                break;
        }
    }

    private void TrackRow_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Border)?.DataContext is ExpandedTrackRowViewModel row && Album is { } album)
            this.FindDataContext<MainViewModel>()?.PlayTrackInAlbum(album.ExpandedTracks, row.Track);
    }

    // Right-click: keeps the multi-selection if the clicked song is in it,
    // otherwise collapses to that one - the main track list's rule.
    private void TrackRow_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Border border)
            return;
        e.Handled = OpenMenu(border);
    }

    // The row's "...": the same menu, the same way.
    private void TrackRow_MenuClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as Visual)?.GetVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("expandedTrack")) is { } row)
            OpenMenu(row);
    }

    private bool OpenMenu(Border border)
    {
        if (border.DataContext is not ExpandedTrackRowViewModel row)
            return false;
        if (Album is not { } album || this.FindDataContext<MainViewModel>() is not { } vm)
            return false;

        if (!row.IsSelected)
            album.SelectTrack(row, toggle: false, rangeSelect: false);

        PlatformContextMenu.Open(BuildTrackContextMenu(row.Track, album.SelectedTracks, vm), border);
        return true;
    }

    // Built fresh per right-click rather than reusing MainView's own
    // _trackMenu: that menu acts on MusicList.SelectedTracks (the main track
    // list's selection), which is unrelated to a selection made here.
    private ContextMenu BuildTrackContextMenu(Track clickedTrack, IReadOnlyList<Track> selectedTracks, MainViewModel vm)
    {
        var getInfoItem = new MenuItem { Header = "Get Info" };
        getInfoItem.Click += (_, _) => OpenTrackInfo(selectedTracks, vm);

        var addToPlaylistItem = new MenuItem { Header = "Add To Playlist" };
        PopulateAddToPlaylistMenu(addToPlaylistItem, selectedTracks, vm);

        var locateFileItem = new MenuItem { Header = "Locate File" };
        locateFileItem.Click += (_, _) => FileLocator.Reveal(clickedTrack.Path);

        var downloadable = selectedTracks.Where(vm.Availability.IsDownloadable).ToList();
        var downloadItem = new MenuItem
        {
            Header    = downloadable.Count > 1 ? $"Download {downloadable.Count} Songs" : "Download",
            IsVisible = downloadable.Count > 0,
            IsEnabled = !vm.Downloads.IsBulkDownloading,
        };
        downloadItem.Click += (_, _) => _ = vm.Downloads.DownloadAllAsync(downloadable);

        var deleteLocalFileItem = new MenuItem { Header = "Delete Local File" };
        LocalFileDeletionDialog.UpdateMenuItem(deleteLocalFileItem, selectedTracks);
        deleteLocalFileItem.Click += async (_, _) =>
            await LocalFileDeletionDialog.DeleteAsync(TopLevel.GetTopLevel(this), vm, selectedTracks);

        var removeFromLibraryItem = new MenuItem();
        LibraryRemovalDialog.UpdateMenuItem(removeFromLibraryItem, selectedTracks);
        removeFromLibraryItem.Click += async (_, _) =>
            await LibraryRemovalDialog.RemoveAsync(TopLevel.GetTopLevel(this), selectedTracks);

        var playNextItem = new MenuItem { Header = "Play Next" };
        playNextItem.Click += (_, _) => vm.PlayNext(selectedTracks);
        var addToQueueItem = new MenuItem { Header = "Add to Queue" };
        addToQueueItem.Click += (_, _) => vm.AddToQueue(selectedTracks);

        var menu = new ContextMenu();
        menu.Items.Add(playNextItem);
        menu.Items.Add(addToQueueItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(getInfoItem);
        menu.Items.Add(addToPlaylistItem);
        menu.Items.Add(locateFileItem);
        menu.Items.Add(downloadItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(deleteLocalFileItem);
        menu.Items.Add(removeFromLibraryItem);
        return menu;
    }

    private static void PopulateAddToPlaylistMenu(MenuItem addToPlaylistItem, IReadOnlyList<Track> tracks, MainViewModel vm)
    {
        var newPlaylistItem = new MenuItem { Header = "New Playlist" };
        newPlaylistItem.Click += async (_, _) => await vm.CreatePlaylistWithTracks(tracks);
        addToPlaylistItem.Items.Add(newPlaylistItem);

        if (vm.Library.Playlists.Count > 0)
        {
            addToPlaylistItem.Items.Add(new Separator());
            foreach (var playlist in vm.Library.Playlists)
            {
                var target = playlist; // capture
                var item = new MenuItem { Header = target.Name };
                item.Click += async (_, _) => await vm.AddTracksToPlaylist(tracks, target);
                addToPlaylistItem.Items.Add(item);
            }
        }
    }

    // Several songs selected open together for a batch edit; one opens with
    // the rest of the album to step through with Prev/Next.
    private void OpenTrackInfo(IReadOnlyList<Track> selectedTracks, MainViewModel vm)
    {
        TrackInfoWindow infoWindow;
        if (selectedTracks.Count > 1)
        {
            infoWindow = new TrackInfoWindow(selectedTracks, vm) { ShowInTaskbar = false };
        }
        else
        {
            if (selectedTracks.Count == 0)
                return;
            var track = selectedTracks[0];
            var tracks = Album?.ExpandedTracks.ToList() ?? new List<Track>();
            var index = tracks.IndexOf(track);
            if (index < 0)
                index = 0;
            infoWindow = new TrackInfoWindow(tracks, index, vm) { ShowInTaskbar = false };
        }

        if (TopLevel.GetTopLevel(this) is Window owner)
            infoWindow.Show(owner);
        else
            infoWindow.Show();
    }
}
