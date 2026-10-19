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
        MainViewModel vm)
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

    // New Playlist, then every playlist the songs can go into. Smart playlists
    // are left out rather than listed and disabled: adding to one is not a
    // thing that can be done later or under some condition, it is not a thing
    // at all (PlaylistManagementViewModel.AddTracks refuses it), and a greyed
    // row invites the user to wonder why.
    private static void FillAddToPlaylist(MenuItem addToPlaylistItem, IReadOnlyList<Track> tracks, MainViewModel vm)
    {
        var newPlaylistItem = new MenuItem { Header = "New Playlist" }.WithIcon(MaterialIconKind.Plus);
        newPlaylistItem.Click += async (_, _) => await vm.CreatePlaylistWithTracks(tracks);
        addToPlaylistItem.Items.Add(newPlaylistItem);

        var targets = vm.Library.Playlists.Where(p => !p.IsSmart).ToList();
        if (targets.Count == 0)
            return;

        addToPlaylistItem.Items.Add(new Separator());
        foreach (var playlist in targets)
        {
            var target = playlist; // capture
            var item = new MenuItem { Header = target.Name };
            item.Click += async (_, _) => await vm.AddTracksToPlaylist(tracks, target);
            addToPlaylistItem.Items.Add(item);
        }
    }
}
