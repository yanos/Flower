using System.Collections.Generic;
using System.Linq;

using Material.Icons;

using Flower.Models;
using Flower.Services;

namespace Flower.ViewModels;

// Everything a song's or an album's menu can offer. What each one does is up
// to the head: a confirmation is a dialog on the desktop and a sheet on the
// phone, Add to Playlist a submenu on one and a picker on the other.
public enum MenuAction
{
    Play,
    Shuffle,
    PlayNext,
    AddToQueue,
    AddToPlaylist,
    RemoveFromQueue,
    GetInfo,
    LocateFile,
    Download,
    ForgetProgress,
    HideFromRecentlyPlayed,
    RenamePlaylist,
    DeleteLocalFiles,
    RemoveFromLibrary,
    DeletePlaylist,
}

// One line of a menu, as both heads draw it. IsDestructive draws its words in
// the danger colour; IsPrimary is the album menu's Play, which the phone
// rings.
public sealed record ActionMenuEntry(
    MenuAction Action, string Title, MaterialIconKind Icon, bool IsDestructive = false, bool IsEnabled = true)
{
    public bool IsPrimary => Action == MenuAction.Play;
}

// What about a set of songs decides which entries appear, worked out once by
// whoever opens the menu (MainViewModel.MenuFactsFor) - the rules each one is
// (Availability.IsDownloadable, LocalFileDeletion.LocalFiles,
// LibraryRemovalService.Removable) are shared already.
public sealed record MenuFacts(int Downloadable, bool IsBulkDownloading, int LocalFiles, int Removable);

// The song and album menus, once, for the desktop's context menus and the
// phone's action sheets alike. Each head used to list its own entries, in its
// own idiom, and they had drifted: the phone's album menu could shuffle and
// the desktop's could not, the desktop had three song menus of its own, and
// one of those offered smart playlists to add songs to. A new entry goes here
// and both heads have it.
//
// Groups, in order: where the songs go, then what they are, then taking them
// away. The desktop draws a rule between groups; the phone's sheet runs them
// together.
public static class ActionMenus
{
    // canRemoveFromQueue: opened on the Queue, over something other than the
    // song playing. canLocateFiles: the head can show a file in the platform's
    // file manager, which a phone cannot - and then only when one of the songs
    // has a file here to show, which one streamed from the server does not.
    public static IReadOnlyList<IReadOnlyList<ActionMenuEntry>> ForSongs(
        MenuFacts facts, bool canRemoveFromQueue, bool canLocateFiles)
    {
        var where = new List<ActionMenuEntry>
        {
            new(MenuAction.PlayNext, "Play Next", MaterialIconKind.PlaylistPlay),
            new(MenuAction.AddToQueue, "Add to Queue", MaterialIconKind.PlaylistMusic),
            new(MenuAction.AddToPlaylist, "Add to Playlist", MaterialIconKind.PlaylistPlus),
        };
        if (canRemoveFromQueue)
            where.Add(new(MenuAction.RemoveFromQueue, "Remove from Queue", MaterialIconKind.PlaylistRemove));

        var what = new List<ActionMenuEntry> { new(MenuAction.GetInfo, "Get Info", MaterialIconKind.InformationOutline) };
        if (canLocateFiles && facts.LocalFiles > 0)
            what.Add(new(MenuAction.LocateFile, "Locate File", MaterialIconKind.FolderOpenOutline));
        if (Download(facts, "Download") is { } download)
            what.Add(download);

        return Groups(where, what, Removals(facts));
    }

    // isContinuing: the album is on Home's Continue Playing shelf.
    // isRecentlyPlayed: the menu was raised over a Recently Played cover.
    // isPlaylist: the phone's playlist row opens this same menu, since play,
    // shuffle and putting the songs somewhere are the same questions of a
    // different pile of songs; the playlist adds its own two.
    public static IReadOnlyList<IReadOnlyList<ActionMenuEntry>> ForAlbum(
        MenuFacts facts, bool isContinuing = false, bool isRecentlyPlayed = false, bool isPlaylist = false)
    {
        var where = new List<ActionMenuEntry>
        {
            new(MenuAction.Play, "Play", MaterialIconKind.Play),
            new(MenuAction.Shuffle, "Shuffle", MaterialIconKind.Shuffle),
            new(MenuAction.PlayNext, "Play Next", MaterialIconKind.PlaylistPlay),
            new(MenuAction.AddToQueue, "Add to Queue", MaterialIconKind.PlaylistMusic),
            new(MenuAction.AddToPlaylist, "Add to Playlist", MaterialIconKind.PlaylistPlus),
        };

        var what = new List<ActionMenuEntry>();
        // Every song of the album at once. Not over a playlist, whose songs
        // share nothing a tag says.
        if (!isPlaylist)
            what.Add(new(MenuAction.GetInfo, "Get Info", MaterialIconKind.InformationOutline));
        if (Download(facts, "Download Album") is { } download)
            what.Add(download);
        // The one way off the shelf without playing the album to its end.
        if (isContinuing)
            what.Add(new(MenuAction.ForgetProgress, "Remove from Continue Playing", MaterialIconKind.PlaylistRemove));
        // The cover's x, as an entry - until the album is next played.
        if (isRecentlyPlayed)
            what.Add(new(MenuAction.HideFromRecentlyPlayed, "Remove from Recently Played", MaterialIconKind.PlaylistRemove));
        if (isPlaylist)
            what.Add(new(MenuAction.RenamePlaylist, "Rename", MaterialIconKind.PencilOutline));

        // Not Remove from Library over a playlist: beside Delete Playlist,
        // which leaves the songs alone, the two are far too easy to mistake
        // when one of them takes every song in the list out of the library.
        // Delete Playlist last, under everything: the one entry here that
        // cannot be undone.
        var away = Removals(facts, offerRemoval: !isPlaylist);
        if (isPlaylist)
            away.Add(new(MenuAction.DeletePlaylist, "Delete Playlist", MaterialIconKind.PlaylistRemove, IsDestructive: true));

        return Groups(where, what, away);
    }

    // Shown only when there is something to fetch right now, with the count
    // in it because these menus act on a whole selection. A batch already in
    // flight owns the per-row spinners the next one would fight over, so it
    // greys rather than going.
    private static ActionMenuEntry? Download(MenuFacts facts, string single) =>
        facts.Downloadable == 0
            ? null
            : new(MenuAction.Download,
                facts.Downloadable > 1 ? $"Download {facts.Downloadable} Songs" : single,
                MaterialIconKind.CloudDownloadOutline,
                IsEnabled: !facts.IsBulkDownloading);

    private static List<ActionMenuEntry> Removals(MenuFacts facts, bool offerRemoval = true)
    {
        var away = new List<ActionMenuEntry>();
        if (facts.LocalFiles > 0)
        {
            away.Add(new(MenuAction.DeleteLocalFiles,
                facts.LocalFiles == 1 ? "Delete Local File" : $"Delete {facts.LocalFiles} Local Files",
                MaterialIconKind.TrashCanOutline, IsDestructive: true));
        }
        if (offerRemoval && facts.Removable > 0)
        {
            away.Add(new(MenuAction.RemoveFromLibrary,
                facts.Removable > 1 ? $"Remove {facts.Removable} Songs from Library" : "Remove from Library",
                MaterialIconKind.MusicNoteOff, IsDestructive: true));
        }
        return away;
    }

    private static IReadOnlyList<IReadOnlyList<ActionMenuEntry>> Groups(params List<ActionMenuEntry>[] groups) =>
        [.. groups.Where(g => g.Count > 0)];

    // The groups run together, for a head that draws no rules.
    public static IReadOnlyList<ActionMenuEntry> Flat(IReadOnlyList<IReadOnlyList<ActionMenuEntry>> groups) =>
        [.. groups.SelectMany(g => g)];
}
