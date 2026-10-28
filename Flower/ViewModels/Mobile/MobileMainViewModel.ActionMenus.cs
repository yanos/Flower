using System.Collections.Generic;
using System.Windows.Input;

using CommunityToolkit.Mvvm.Input;

using Flower.Services;

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
    // file in, so no Locate File. Raised on Home, it is over a Recently Played
    // song - the one list of songs Home has. Go to Album and Go to Artist
    // everywhere but the page they would go to.
    public IReadOnlyList<ActionMenuEntry> SongActions => ActionTarget is { } track
        ? ActionMenus.Flat(ActionMenus.ForSongs(
            Main.MenuFactsFor([track], _libraryRemoval),
            canRemoveFromQueue: CanRemoveActionTargetFromQueue,
            canLocateFiles: false,
            isRecentlyPlayed: IsShowingHome,
            canGoToAlbum: !string.IsNullOrEmpty(track.Album) && !IsAtAlbum(track.Album),
            canGoToArtist: !string.IsNullOrEmpty(track.Artists) && !IsAtArtist(track.Artists)))
        : [];

    // The album menu, for AlbumActionTarget - or for the playlist whose row
    // raised the same sheet.
    public IReadOnlyList<ActionMenuEntry> AlbumActions => AlbumActionTarget is { } tile
        ? ActionMenus.Flat(ActionMenus.ForAlbum(
            Main.MenuFactsFor(AlbumActionTargetTracks(), _libraryRemoval),
            isContinuing: CanForgetAlbumActionTargetProgress,
            isPlaylist: IsActingOnAPlaylist,
            isArtist: _isActingOnAnArtist,
            isFolder: PlaylistActionTarget?.Playlist is { IsFolder: true },
            canMoveToFolder: CanMoveActionTargetToFolder,
            isSmart: PlaylistActionTarget?.Playlist is { IsSmart: true } && CanCreateSmartPlaylist,
            canGoToArtist: AlbumActionTargetArtist() is { } artist && !IsAtArtist(artist)))
        : [];

    // The one artist an album's menu can go to: an album's, not a playlist's
    // or an artist's own, and only when every song on it is by them - a
    // compilation's songs have no one artist page to land on.
    private string? AlbumActionTargetArtist()
    {
        if (AlbumActionTarget is not { } tile || PlaylistActionTarget != null || _isActingOnAnArtist)
            return null;

        return MainViewModel.ShareArtist(tile.Tracks) ? tile.Tracks[0].Artists : null;
    }

    public ICommand RunSongActionCommand => _runSongAction ??= new RelayCommand<ActionMenuEntry>(entry =>
    {
        var command = entry?.Action switch
        {
            MenuAction.PlayNext => PlayNextActionTargetCommand,
            MenuAction.AddToQueue => AddActionTargetToQueueCommand,
            MenuAction.AddToPlaylist => OpenAddToPlaylistCommand,
            MenuAction.RemoveFromQueue => RemoveActionTargetFromQueueCommand,
            MenuAction.HideFromRecentlyPlayed => HideActionTargetFromRecentlyPlayedCommand,
            MenuAction.GetInfo => ViewTrackInfoCommand,
            MenuAction.Download => DownloadActionTargetCommand,
            MenuAction.DeleteLocalFiles => DeleteDownloadedFileCommand,
            MenuAction.RemoveFromLibrary => RemoveFromLibraryCommand,
            _ => null,
        };
        if (entry?.Action == MenuAction.GoToAlbum)
            GoToAlbumAsync(ActionTarget?.Album).Forget(_logger, "Going to a song's album");
        else if (entry?.Action == MenuAction.GoToArtist)
            GoToArtist(ActionTarget?.Artists);
        else
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
            MenuAction.RenamePlaylist => RenamePlaylistActionTargetCommand,
            MenuAction.EditRules => EditRulesActionTargetCommand,
            MenuAction.ConvertToOrdinary => ConvertActionTargetToOrdinaryCommand,
            MenuAction.NewPlaylistInFolder => NewPlaylistInFolderCommand,
            MenuAction.NewFolderInFolder => NewFolderInFolderCommand,
            MenuAction.MoveToFolder => OpenMoveToFolderCommand,
            MenuAction.DeleteLocalFiles => DeleteAlbumActionTargetLocalFilesCommand,
            MenuAction.RemoveFromLibrary => RemoveAlbumFromLibraryCommand,
            MenuAction.DeletePlaylist => DeletePlaylistActionTargetCommand,
            _ => null,
        };
        if (entry?.Action == MenuAction.GoToArtist)
            GoToArtist(AlbumActionTargetArtist());
        else
            command?.Execute(null);
    });

    private ICommand? _runSongAction;
    private ICommand? _runAlbumAction;
}
