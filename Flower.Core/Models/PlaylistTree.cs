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

            foreach (var list in children.Values)
                Order(list);
            Order(roots);

            foreach (var root in roots)
                Visit(root, 0);
            return rows;
        }

        // Siblings by Playlist.SortKey, the ones never placed after the rest
        // in the library's own order - a stable sort, so that order holds.
        private static void Order(List<Playlist> siblings)
        {
            var ordered = siblings.OrderBy(p => p.SortKey ?? double.PositiveInfinity).ToList();
            siblings.Clear();
            siblings.AddRange(ordered);
        }

        // What sits directly inside a folder, or at the top level for null, in
        // the order Flatten shows it.
        public static List<Playlist> ChildrenOf(Playlist? folder, IReadOnlyList<Playlist> all)
        {
            var byId = all.ToDictionary(p => p.Id);
            var children = all.Where(p => ParentOf(p, byId) == folder).ToList();
            Order(children);
            return children;
        }

        // Puts these playlists, in the order given, into a folder (or the top
        // level, for null) at index among what is already there - the place
        // counted without them, so "before the third one" means the same
        // whether or not one of them is the third. The ones that cannot go
        // there (CanMove) are left where they are.
        //
        // Only the playlists moved are given new keys - halfway between their
        // new neighbours - so a drag is an edit of what was dragged and of
        // nothing else, and two devices each moving a different playlist
        // never conflict over a third. The exception is a folder whose
        // playlists were never placed, or whose keys have run too close
        // together to fit any between: then every one of them is numbered
        // afresh, once. Returns whether anything moved.
        public static bool Place(IReadOnlyList<Playlist> moving, Playlist? folder, int index, IReadOnlyList<Playlist> all)
        {
            var placed = moving.Where(p => CanMove(p, folder, all)).Distinct().ToList();
            if (placed.Count == 0)
                return false;

            var current = ChildrenOf(folder, all);
            var others = current.Where(p => !placed.Contains(p)).ToList();
            index = Math.Clamp(index, 0, others.Count);
            var wanted = others.Take(index).Concat(placed).Concat(others.Skip(index)).ToList();
            if (wanted.SequenceEqual(current))
                return false;

            var before = index > 0 ? others[index - 1].SortKey : null;
            var after = index < others.Count ? others[index].SortKey : null;
            var unplaced = others.Any(p => p.SortKey == null);
            var lo = before ?? (after is { } a ? a - placed.Count - 1 : 0);
            var hi = after ?? lo + placed.Count + 1;
            var step = (hi - lo) / (placed.Count + 1);

            if (unplaced || step < MinimumGap)
            {
                for (var i = 0; i < wanted.Count; i++)
                    wanted[i].SortKey = i + 1;
            }
            else
            {
                for (var i = 0; i < placed.Count; i++)
                    placed[i].SortKey = lo + step * (i + 1);
            }

            foreach (var playlist in placed)
                playlist.ParentId = folder?.Id;
            return true;
        }

        // Closer than this and halving the gap again starts to lose the
        // difference to rounding - which takes about forty drags into the
        // same spot.
        private const double MinimumGap = 1e-9;

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
