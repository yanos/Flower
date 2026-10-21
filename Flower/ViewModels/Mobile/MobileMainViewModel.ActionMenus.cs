using System.Collections.Generic;
using System.Windows.Input;

using CommunityToolkit.Mvvm.Input;

namespace Flower.ViewModels.Mobile;

// ── Song and album menus ──────────────────────────────────────────────
//
// The two action sheets (TrackActionsView, AlbumActionsView) list what
// ActionMenus says - the same entries, in the same order, as the desktop's
// context menus - and each entry runs the command this view model already
// had for it, confirmation sheets and all.
public partial class MobileMainViewModel
{
    // The song menu, for ActionTarget. A phone has no file manager to show a
    // file in, so no Locate File.
    public IReadOnlyList<ActionMenuEntry> SongActions => ActionTarget is { } track
        ? ActionMenus.Flat(ActionMenus.ForSongs(
            Main.MenuFactsFor([track], _libraryRemoval),
            canRemoveFromQueue: CanRemoveActionTargetFromQueue,
            canLocateFiles: false))
        : [];

    // The album menu, for AlbumActionTarget - or for the playlist whose row
    // raised the same sheet.
    public IReadOnlyList<ActionMenuEntry> AlbumActions => AlbumActionTarget is { } tile
        ? ActionMenus.Flat(ActionMenus.ForAlbum(
            Main.MenuFactsFor(AlbumActionTargetTracks(), _libraryRemoval),
            isContinuing: CanForgetAlbumActionTargetProgress,
            isRecentlyPlayed: Main.Home.IsOnRecentlyPlayed(tile),
            isPlaylist: IsActingOnAPlaylist))
        : [];

    public ICommand RunSongActionCommand => _runSongAction ??= new RelayCommand<ActionMenuEntry>(entry =>
    {
        var command = entry?.Action switch
        {
            MenuAction.PlayNext => PlayNextActionTargetCommand,
            MenuAction.AddToQueue => AddActionTargetToQueueCommand,
            MenuAction.AddToPlaylist => OpenAddToPlaylistCommand,
            MenuAction.RemoveFromQueue => RemoveActionTargetFromQueueCommand,
            MenuAction.GetInfo => ViewTrackInfoCommand,
            MenuAction.Download => DownloadActionTargetCommand,
            MenuAction.DeleteLocalFiles => DeleteDownloadedFileCommand,
            MenuAction.RemoveFromLibrary => RemoveFromLibraryCommand,
            _ => null,
        };
        command?.Execute(null);
    });

    public ICommand RunAlbumActionCommand => _runAlbumAction ??= new RelayCommand<ActionMenuEntry>(entry =>
    {
        var command = entry?.Action switch
        {
            MenuAction.Play => PlayAlbumActionTargetCommand,
            MenuAction.Shuffle => ShuffleAlbumActionTargetCommand,
            MenuAction.PlayNext => PlayNextAlbumActionTargetCommand,
            MenuAction.AddToQueue => AddAlbumActionTargetToQueueCommand,
            MenuAction.AddToPlaylist => AddAlbumActionTargetToPlaylistCommand,
            MenuAction.GetInfo => ViewAlbumActionTargetTrackInfoCommand,
            MenuAction.Download => DownloadAlbumActionTargetCommand,
            MenuAction.ForgetProgress => ForgetAlbumActionTargetProgressCommand,
            MenuAction.HideFromRecentlyPlayed => HideAlbumActionTargetFromRecentlyPlayedCommand,
            MenuAction.RenamePlaylist => RenamePlaylistActionTargetCommand,
            MenuAction.DeleteLocalFiles => DeleteAlbumActionTargetLocalFilesCommand,
            MenuAction.RemoveFromLibrary => RemoveAlbumFromLibraryCommand,
            MenuAction.DeletePlaylist => DeletePlaylistActionTargetCommand,
            _ => null,
        };
        command?.Execute(null);
    });

    private ICommand? _runSongAction;
    private ICommand? _runAlbumAction;
}
