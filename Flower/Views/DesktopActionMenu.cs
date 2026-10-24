using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia.Controls;
using Avalonia.Input;

using Material.Icons;

using Flower.Models;
using Flower.Services;
using Flower.ViewModels;

namespace Flower.Views;

// The desktop's drawing of a song or album menu (ActionMenus): a ContextMenu,
// a rule between the groups, Add to Playlist as a submenu. What each entry
// does is the caller's - run is handed the action picked.
internal static class DesktopActionMenu
{
    public static ContextMenu Build(
        IReadOnlyList<IReadOnlyList<ActionMenuEntry>> groups,
        Action<MenuAction> run,
        IReadOnlyList<Track> addToPlaylist,
        MainViewModel vm,
        IReadOnlyList<Playlist>? moving = null)
    {
        var menu = new ContextMenu();
        foreach (var group in groups)
        {
            if (menu.Items.Count > 0)
                menu.Items.Add(new Separator());

            foreach (var entry in group)
            {
                var item = new MenuItem { Header = entry.Title, IsEnabled = entry.IsEnabled }.WithIcon(entry.Icon);
                if (entry.Action == MenuAction.AddToPlaylist)
                {
                    FillAddToPlaylist(item, addToPlaylist, vm);
                }
                else if (entry.Action == MenuAction.MoveToFolder && moving != null)
                {
                    FillMoveToFolder(item, moving, vm);
                }
                else
                {
                    var action = entry.Action;
                    item.Click += (_, _) => run(action);
                }

                // Shown beside it as the reminder it is; MainView handles the
                // key itself.
                if (entry.Action == MenuAction.GetInfo)
                    item.InputGesture = new KeyGesture(Key.I, PlatformShortcuts.Primary);

                menu.Items.Add(item);
            }
        }

        return menu;
    }

    // New Playlist, then every playlist the songs can go into, each folder a
    // submenu of what is in it, as the sidebar nests them. Smart playlists
    // are left out rather than listed and disabled: adding to one is not a
    // thing that can be done later or under some condition, it is not a thing
    // at all (PlaylistManagementViewModel.AddTracks refuses it), and a greyed
    // row invites the user to wonder why. A folder with nothing in it to add
    // to is left out for the same reason.
    public static void FillAddToPlaylist(MenuItem addToPlaylistItem, IReadOnlyList<Track> tracks, MainViewModel vm)
    {
        var newPlaylistItem = new MenuItem { Header = "New Playlist" }.WithIcon(MaterialIconKind.Plus);
        newPlaylistItem.Click += async (_, _) => await vm.CreatePlaylistWithTracks(tracks);
        addToPlaylistItem.Items.Add(newPlaylistItem);

        var all = vm.Library.Playlists;
        var rows = PlaylistTree.Flatten(all);
        if (!rows.Any(r => r.Playlist.IsOrdinary))
            return;

        addToPlaylistItem.Items.Add(new Separator());
        var at = 0;
        AddLevel(addToPlaylistItem, depth: 0);

        // rows is depth first, so a folder's contents are the rows straight
        // after it that sit deeper than it does.
        void AddLevel(MenuItem into, int depth)
        {
            while (at < rows.Count && rows[at].Depth >= depth)
            {
                var playlist = rows[at++].Playlist;
                if (playlist.IsFolder)
                {
                    var folderItem = new MenuItem { Header = playlist.Name }.WithIcon(MaterialIconKind.FolderOutline);
                    AddLevel(folderItem, depth + 1);
                    if (folderItem.Items.Count > 0)
                        into.Items.Add(folderItem);
                }
                else if (playlist.IsOrdinary)
                {
                    var item = new MenuItem { Header = playlist.Name };
                    item.Click += async (_, _) => await vm.AddTracksToPlaylist(tracks, playlist);
                    into.Items.Add(item);
                }
            }
        }
    }

    // Every folder the playlists can go into, indented as the sidebar shows
    // them, and the top level when any of them is in a folder now.
    public static void FillMoveToFolder(MenuItem moveItem, IReadOnlyList<Playlist> playlists, MainViewModel vm)
    {
        var all = vm.Library.Playlists;
        if (playlists.Any(p => PlaylistTree.ParentOf(p, all) != null))
        {
            var topLevel = new MenuItem { Header = "Top Level" }.WithIcon(MaterialIconKind.FormatListBulleted);
            topLevel.Click += async (_, _) => await vm.MovePlaylists(playlists, null);
            moveItem.Items.Add(topLevel);
        }

        var folders = PlaylistTree.FoldersFor(playlists, all);
        if (folders.Count > 0 && moveItem.Items.Count > 0)
            moveItem.Items.Add(new Separator());

        foreach (var row in folders)
        {
            var folder = row.Playlist;
            var item = new MenuItem
            {
                Header = new string('\u2003', row.Depth) + folder.Name,
                // Where they already all are is not somewhere to move them.
                IsEnabled = !playlists.All(p => PlaylistTree.ParentOf(p, all) == folder),
            }.WithIcon(MaterialIconKind.FolderOutline);
            item.Click += async (_, _) => await vm.MovePlaylists(playlists, folder);
            moveItem.Items.Add(item);
        }
    }

    // Whether Move to Folder has anywhere to offer.
    public static bool CanMoveToFolder(IReadOnlyList<Playlist> playlists, MainViewModel vm) =>
        playlists.Any(p => PlaylistTree.ParentOf(p, vm.Library.Playlists) != null)
        || PlaylistTree.FoldersFor(playlists, vm.Library.Playlists).Count > 0;
}
