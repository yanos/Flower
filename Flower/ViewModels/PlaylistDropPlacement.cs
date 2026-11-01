using System.Collections.Generic;
using System.Linq;

using Flower.Models;

namespace Flower.ViewModels;

// Where a line is drawn while a playlist is dragged over the sidebar: at the
// top or bottom edge of a row, or over a whole folder row for "into it".
public enum PlaylistDropEdge
{
    None,
    Above,
    Below,
    Into,
}

// What a drop there does: put the playlist in Folder (null for the top level)
// at Index among what is already there (PlaylistTree.Place), and what to
// light up meanwhile.
public readonly record struct PlaylistDrop(Playlist? Folder, int Index, Playlist Row, PlaylistDropEdge Edge);

// Where a playlist dragged over a sidebar row would go - the arithmetic of
// MainView's playlist drag, apart from the pointer so a test reaches it. The
// Finder's rule for a list of folders: the top or bottom half of a playlist's
// row is before or after it; a folder's row is that too at its top and bottom
// quarter, and its middle is into it, at the end. Just under an open folder
// is the first place inside it, since that is where the line is drawn.
public static class PlaylistDropPlacement
{
    private const double FolderEdge = 0.25;

    // fraction: how far down the row the pointer is, 0 at its top and 1 at
    // its bottom. Null where it cannot go - onto itself, or into a folder of
    // its own.
    public static PlaylistDrop? For(
        Playlist moving, Playlist over, bool overIsExpanded, double fraction, IReadOnlyList<Playlist> all)
    {
        if (over == moving || PlaylistTree.DescendantsOf(moving, all).Contains(over))
            return null;

        var edge = !over.IsFolder ? (fraction < 0.5 ? PlaylistDropEdge.Above : PlaylistDropEdge.Below)
            : fraction < FolderEdge ? PlaylistDropEdge.Above
            : fraction > 1 - FolderEdge ? PlaylistDropEdge.Below
            : PlaylistDropEdge.Into;

        PlaylistDrop? drop = edge switch
        {
            PlaylistDropEdge.Into => new(over, int.MaxValue, over, edge),
            PlaylistDropEdge.Below when over.IsFolder && overIsExpanded && Others(over, moving, all).Count > 0
                => new(over, 0, over, edge),
            _ => Beside(moving, over, edge, all),
        };

        return drop is { } d && PlaylistTree.CanMove(moving, d.Folder, all) ? d : null;
    }

    // Past the last row, in the room under the playlists: the end of the
    // top level. The line goes under the last top-level playlist.
    public static PlaylistDrop? AtEnd(Playlist moving, IReadOnlyList<Playlist> all)
    {
        var top = Others(null, moving, all);
        return top.Count == 0 ? null : new PlaylistDrop(null, int.MaxValue, top[^1], PlaylistDropEdge.Below);
    }

    private static PlaylistDrop Beside(Playlist moving, Playlist over, PlaylistDropEdge edge, IReadOnlyList<Playlist> all)
    {
        var folder = PlaylistTree.ParentOf(over, all);
        var at = Others(folder, moving, all).IndexOf(over);
        return new PlaylistDrop(folder, edge == PlaylistDropEdge.Below ? at + 1 : at, over, edge);
    }

    // A folder's playlists without the one being dragged, which is how
    // PlaylistTree.Place counts its index.
    private static List<Playlist> Others(Playlist? folder, Playlist moving, IReadOnlyList<Playlist> all) =>
        PlaylistTree.ChildrenOf(folder, all).Where(p => p != moving).ToList();
}
