using System;
using System.Collections.Generic;
using System.Linq;

namespace Flower.Models
{
    // Playlists as the tree folders make of them. Nothing stores the tree: each
    // playlist names its folder (Playlist.ParentId), and this reads the shape
    // back out of a flat list - Library.Playlists, in its own order, which is
    // the order siblings are shown in.
    //
    // The one rule everything here applies is what a ParentId that does not
    // hold up means: a parent that is not in the list, is not a folder, belongs
    // to another listener, or sits inside the playlist naming it, reads as no
    // parent at all. Each of those is reachable without anyone doing anything
    // wrong - a folder deleted on one device while another moved a playlist
    // into it, or two devices each moving one of two folders into the other -
    // and every one of them shows the playlist at the top level, where it can
    // be seen and moved, rather than nowhere.
    public static class PlaylistTree
    {
        public static Playlist? ParentOf(Playlist playlist, IReadOnlyList<Playlist> all) =>
            ParentOf(playlist, all.ToDictionary(p => p.Id));

        private static Playlist? ParentOf(Playlist playlist, IReadOnlyDictionary<Guid, Playlist> byId)
        {
            if (DeclaredParent(playlist, byId) is not { } parent)
                return null;

            // Walk up from the declared parent: arriving back here is a cycle,
            // and a cycle has no top to hang from, so every playlist on it is
            // put at the top level. A loop further up that does not pass
            // through this playlist is not this playlist's problem - the
            // folders on it are each at the top level by this same rule, and
            // this one stays in its folder - so the walk only stops there.
            var seen = new HashSet<Guid>();
            for (var at = parent; at != null && seen.Add(at.Id); at = DeclaredParent(at, byId))
            {
                if (at == playlist)
                    return null;
            }

            return parent;
        }

        private static Playlist? DeclaredParent(Playlist playlist, IReadOnlyDictionary<Guid, Playlist> byId) =>
            playlist.ParentId is { } id
            && byId.TryGetValue(id, out var parent)
            && parent.IsFolder
            && parent.Listener == playlist.Listener
                ? parent
                : null;

        // The tree, depth first, each row with how deep it sits. A folder's
        // contents are left out while isCollapsed says so, and the folder
        // itself is still listed.
        public static List<PlaylistTreeRow> Flatten(IReadOnlyList<Playlist> all, Func<Playlist, bool>? isCollapsed = null)
        {
            var byId = all.ToDictionary(p => p.Id);
            var children = new Dictionary<Guid, List<Playlist>>();
            var roots = new List<Playlist>();
            foreach (var playlist in all)
            {
                if (ParentOf(playlist, byId) is { } parent)
                {
                    if (!children.TryGetValue(parent.Id, out var list))
                        children[parent.Id] = list = [];
                    list.Add(playlist);
                }
                else
                {
                    roots.Add(playlist);
                }
            }

            var rows = new List<PlaylistTreeRow>(all.Count);
            void Visit(Playlist playlist, int depth)
            {
                rows.Add(new PlaylistTreeRow(playlist, depth));
                if (isCollapsed?.Invoke(playlist) == true || !children.TryGetValue(playlist.Id, out var inside))
                    return;
                foreach (var child in inside)
                    Visit(child, depth + 1);
            }

            foreach (var root in roots)
                Visit(root, 0);
            return rows;
        }

        // Everything inside a folder, however deep, in the order Flatten shows it.
        public static List<Playlist> DescendantsOf(Playlist folder, IReadOnlyList<Playlist> all)
        {
            var rows = Flatten(all);
            var at = rows.FindIndex(r => r.Playlist == folder);
            if (at < 0)
                return [];

            var result = new List<Playlist>();
            for (var i = at + 1; i < rows.Count && rows[i].Depth > rows[at].Depth; i++)
                result.Add(rows[i].Playlist);
            return result;
        }

        // What a playlist plays: its own songs, or for a folder every song in
        // the playlists inside it, each once, in the order they first appear.
        // Once rather than as often as they are listed, because a song in two
        // of a folder's playlists is not a song the folder holds twice -
        // Music.app reads a folder the same way.
        public static IReadOnlyList<Track> TracksOf(Playlist playlist, IReadOnlyList<Playlist> all)
        {
            if (!playlist.IsFolder)
                return playlist.Tracks;

            var seen = new HashSet<Track>(ReferenceEqualityComparer.Instance);
            var result = new List<Track>();
            foreach (var inside in DescendantsOf(playlist, all))
            {
                foreach (var track in inside.Tracks)
                {
                    if (seen.Add(track))
                        result.Add(track);
                }
            }
            return result;
        }

        // Whether a playlist may go into a folder (or to the top level, for
        // null): only into a folder of the same listener, and never into
        // itself or anything inside it, which would make a cycle.
        public static bool CanMove(Playlist playlist, Playlist? folder, IReadOnlyList<Playlist> all)
        {
            if (folder == null)
                return true;
            if (!folder.IsFolder || folder == playlist || folder.Listener != playlist.Listener)
                return false;

            return !DescendantsOf(playlist, all).Contains(folder);
        }

        // The folders a playlist could be moved into, in tree order with their
        // depth - what a Move to Folder menu lists.
        public static List<PlaylistTreeRow> FoldersFor(IReadOnlyList<Playlist> playlists, IReadOnlyList<Playlist> all) =>
            Flatten(all)
                .Where(r => r.Playlist.IsFolder && playlists.All(p => CanMove(p, r.Playlist, all)))
                .ToList();
    }

    public readonly record struct PlaylistTreeRow(Playlist Playlist, int Depth);
}
