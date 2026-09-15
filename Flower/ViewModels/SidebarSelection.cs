using System;
using System.Collections.Generic;
using System.Linq;

namespace Flower.ViewModels;

// The rule the desktop sidebar's multiple selection is held to, kept out of
// MainView's code-behind so it can be tested without a ListBox.
//
// Cmd/Ctrl+click extends the selection across playlists and nothing else - they
// are the one kind with a group action (the sidebar's right-click menu). What
// the track list shows is still the one SelectedSidebarItem; the rest only
// ride along for that menu.
public static class SidebarSelection
{
    // What the selection should become after a click left it as `selected`, or
    // null when it is fine as it is. `added` is what that click selected;
    // `lastSingle` the most recent one-row selection, for when nothing is left.
    public static IReadOnlyList<SidebarItem>? Settle(
        IReadOnlyList<SidebarItem> selected,
        IReadOnlyList<SidebarItem> added,
        SidebarItem? lastSingle)
    {
        var rows = selected.Where(i => !i.IsHeader).ToList();

        // A header alone, or the only selected row Cmd/Ctrl+clicked away: stay
        // where the user was rather than showing nothing.
        if (rows.Count == 0)
            return lastSingle is null ? Array.Empty<SidebarItem>() : new[] { lastSingle };

        // A playlist together with Songs, a device or another view: start again
        // from what was just clicked.
        if (rows.Count > 1 && rows.Any(i => i.Kind != SidebarItemKind.Playlist))
        {
            var clicked = added.LastOrDefault(i => !i.IsHeader && rows.Contains(i));
            return new[] { clicked ?? rows[^1] };
        }

        // A header is never part of a selection.
        return rows.Count == selected.Count ? null : rows;
    }
}
