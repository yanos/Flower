using System.Collections.Generic;
using System.Linq;

using Avalonia.Controls;

using Microsoft.Extensions.Logging;

using Flower.Logging;
using Flower.Models;
using Flower.Services;
using Flower.ViewModels;

namespace Flower.Views;

// The song menu (ActionMenus.ForSongs) for a list of songs that is not the
// main track list - an album opened in a grid (AlbumTrackList), a smart
// playlist's preview (SmartPlaylistEditorWindow) - built and carried out the
// same way for each. The main track list keeps its own (MainView.
// OnRowContextMenu): it alone has the queue's Remove from Queue and Track
// Info stepping through whatever page it shows.
internal static class DesktopSongMenu
{
    private static readonly ILogger Logger = AppLogging.CreateLogger(typeof(DesktopSongMenu).FullName!);

    // clicked: the song right-clicked, which Go to Album/Artist and Locate
    // File are about. selected: the selection it is in, in list order, which
    // everything else acts on. browse: what Track Info's Prev/Next step
    // through when one song is open. owner: whose window the dialogs and
    // Track Info belong to.
    public static ContextMenu Build(
        Control owner,
        MainViewModel vm,
        Track clicked,
        IReadOnlyList<Track> selected,
        IReadOnlyList<Track> browse,
        bool canGoToAlbum,
        bool canGoToArtist)
    {
        var groups = ActionMenus.ForSongs(
            vm.MenuFactsFor(selected, LibraryRemovalDialog.Service),
            canRemoveFromQueue: false,
            canLocateFiles: PlatformShell.IsAvailable,
            canGoToAlbum: canGoToAlbum && MainViewModel.ShareAlbum(selected),
            canGoToArtist: canGoToArtist && MainViewModel.ShareArtist(selected));

        return DesktopActionMenu.Build(groups, async action =>
        {
            switch (action)
            {
                case MenuAction.GoToAlbum:
                    vm.GoToAlbumAsync(clicked).Forget(Logger, "Going to a song's album");
                    break;
                case MenuAction.GoToArtist:
                    vm.GoToArtistAsync(clicked).Forget(Logger, "Going to a song's artist");
                    break;
                case MenuAction.PlayNext:
                    vm.PlayNext(selected);
                    break;
                case MenuAction.AddToQueue:
                    vm.AddToQueue(selected);
                    break;
                case MenuAction.GetInfo:
                    OpenTrackInfo(owner, vm, selected, browse);
                    break;
                case MenuAction.LocateFile:
                    // Revealing several files at once is not one action: the
                    // song clicked, or - streamed, with no file here - the
                    // first of the selection that has one, which is what
                    // offered the entry.
                    PlatformShell.Reveal(clicked.Path ?? LocalFileDeletion.LocalFiles(selected).FirstOrDefault()?.Path);
                    break;
                case MenuAction.Download:
                    _ = vm.Downloads.DownloadAllAsync(selected.Where(vm.Availability.IsDownloadable).ToList());
                    break;
                case MenuAction.DeleteLocalFiles:
                    await LocalFileDeletionDialog.DeleteAsync(TopLevel.GetTopLevel(owner), vm, selected);
                    break;
                case MenuAction.RemoveFromLibrary:
                    await LibraryRemovalDialog.RemoveAsync(TopLevel.GetTopLevel(owner), selected);
                    break;
            }
        }, selected, vm);
    }

    // Several songs selected open together for a batch edit; one opens with
    // the rest of the list to step through with Prev/Next.
    private static void OpenTrackInfo(Control owner, MainViewModel vm, IReadOnlyList<Track> selected, IReadOnlyList<Track> browse)
    {
        TrackInfoWindow infoWindow;
        if (selected.Count > 1)
        {
            infoWindow = new TrackInfoWindow(selected, vm) { ShowInTaskbar = false };
        }
        else
        {
            if (selected.Count == 0)
                return;
            var track = selected[0];
            var tracks = browse.ToList();
            var index = tracks.IndexOf(track);
            if (index < 0)
            {
                tracks = [track];
                index = 0;
            }
            infoWindow = new TrackInfoWindow(tracks, index, vm) { ShowInTaskbar = false };
        }

        if (TopLevel.GetTopLevel(owner) is Window window)
            infoWindow.Show(window);
        else
            infoWindow.Show();
    }
}
