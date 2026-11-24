using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;

namespace Flower.Services;

// Combines two edits of one playlist made from the same version of it: the copy
// this device last agreed on with its server (PlaylistAgreement), this device's
// copy, and the server's. Null when they cannot both be kept, which is when
// PlaylistSyncPlanner asks. See docs/PLAYLIST-SYNC-PLAN.md, "Merging".
//
// Field by field. The name, the folder and the place in it: one side changed
// it, take that; both changed it to different values, null. A smart playlist's
// rules and a Vibe Playlist's sentence: one side changed it, take that; both
// did, the newer edit, since no hand-built list is at stake. Their songs come
// with the rules or sentence kept, because they are its answer, not a list.
// A playlist that changed kind (rules added or cleared) never combines.
//
// Rows are told apart by EntryId, so a song in a playlist twice is two entries,
// and removing one of them is never read as removing the other. A side sharing
// no entries with the base has no history with it (a copy from before rows had
// ids): its rows are paired with the base's by song, nth copy to nth.
//
// What combines: each side adding songs, each side removing songs, and one side
// reordering - the other side's additions and removals are applied to the
// reordered list, each added song landing at the end if that is where it was
// added, and otherwise after the song it followed on its own side. What does
// not: both sides reordering differently.
public static class PlaylistThreeWayMerge
{
    public static Playlist? Merge(Playlist before, Playlist ours, Playlist theirs)
    {
        if (KindOf(ours) != KindOf(before) || KindOf(theirs) != KindOf(before))
            return null;

        var name = Pick(before.Name, ours.Name, theirs.Name, string.Equals);
        var parent = Pick(before.ParentId, ours.ParentId, theirs.ParentId, Nullable.Equals);
        var place = Pick(before.SortKey, ours.SortKey, theirs.SortKey, Nullable.Equals);
        if (name == Side.Neither || parent == Side.Neither || place == Side.Neither)
            return null;

        // Which side's query is kept, for a smart or Vibe Playlist: both
        // changed it, the newer edit.
        var newer = ours.UpdatedAt >= theirs.UpdatedAt ? Side.Ours : Side.Theirs;
        var query = KindOf(before) switch
        {
            Kind.Smart => Pick(before.Rules, ours.Rules, theirs.Rules, SmartPlaylistRules.Equivalent),
            Kind.Vibe => Pick(
                (before.VibePrompt, before.VibeInstrumental), (ours.VibePrompt, ours.VibeInstrumental),
                (theirs.VibePrompt, theirs.VibeInstrumental), (a, b) => a == b),
            _ => Side.Theirs,
        };
        if (query == Side.Neither)
            query = newer;
        var asked = query == Side.Ours ? ours : theirs;

        var entries = KindOf(before) switch
        {
            Kind.Ordinary => MergeEntries(before.Entries, ours.Entries, theirs.Entries),
            Kind.Folder => [],
            _ => asked.Entries.ToList(),
        };
        if (entries == null)
            return null;

        var result = new Playlist(
            ours.Id, Of(name, ours, theirs).Name, [], ours.UpdatedAt, ours.Comment, ours.IsPublic, ours.CreatedAt,
            rules: asked.Rules, listener: ours.Listener, isFolder: ours.IsFolder,
            parentId: Of(parent, ours, theirs).ParentId, sortKey: Of(place, ours, theirs).SortKey,
            vibePrompt: asked.VibePrompt, vibeInstrumental: asked.VibeInstrumental, entries: entries);
        // Newer than both: it is an edit made after each of them.
        result.MarkEditedAfter(ours.UpdatedAt > theirs.UpdatedAt ? ours.UpdatedAt : theirs.UpdatedAt);
        return result;
    }

    private enum Kind
    {
        Ordinary,
        Smart,
        Vibe,
        Folder,
    }

    private static Kind KindOf(Playlist playlist) =>
        playlist.IsFolder ? Kind.Folder : playlist.IsSmart ? Kind.Smart : playlist.IsVibe ? Kind.Vibe : Kind.Ordinary;

    private enum Side
    {
        Ours,
        Theirs,
        Neither,
    }

    // Whose value of one field the result takes: the side that changed it,
    // either when both made the same change, neither when they differ.
    private static Side Pick<T>(T before, T ours, T theirs, Func<T, T, bool> same)
    {
        if (same(ours, before))
            return Side.Theirs;
        if (same(theirs, before) || same(theirs, ours))
            return Side.Ours;
        return Side.Neither;
    }

    private static Playlist Of(Side side, Playlist ours, Playlist theirs) => side == Side.Ours ? ours : theirs;

    private static List<PlaylistEntry> AlignedTo(IReadOnlyList<PlaylistEntry> baseEntries, IReadOnlyList<PlaylistEntry> side)
    {
        var known = baseEntries.Select(e => e.EntryId).ToHashSet();
        if (side.Count == 0 || side.Any(e => known.Contains(e.EntryId)))
            return side.ToList();

        return TakingIdsFrom(baseEntries, side);
    }

    // Both sides adding the same song is one addition, not two copies of it.
    private static List<PlaylistEntry> PairingAdditions(
        IReadOnlyList<PlaylistEntry> before, List<PlaylistEntry> ours, List<PlaylistEntry> theirs)
    {
        var old = before.Select(e => e.EntryId).ToHashSet();
        var ourIds = ours.Select(e => e.EntryId).ToHashSet();
        var ourAdditions = ours.Where(e => !old.Contains(e.EntryId)).ToList();
        bool IsTheirAddition(PlaylistEntry e) => !old.Contains(e.EntryId) && !ourIds.Contains(e.EntryId);

        var next = new Queue<PlaylistEntry>(TakingIdsFrom(ourAdditions, theirs.Where(IsTheirAddition).ToList()));
        return theirs.Select(e => IsTheirAddition(e) ? next.Dequeue() : e).ToList();
    }

    // Each row of `side` takes the id of the nth entry of `source` holding its
    // song, when there is one.
    private static List<PlaylistEntry> TakingIdsFrom(IReadOnlyList<PlaylistEntry> source, IReadOnlyList<PlaylistEntry> side)
    {
        var waiting = new CopiesBySong<Guid>();
        foreach (var entry in source)
            waiting.Add(entry.TrackId, entry.EntryId);

        return side.Select(e => waiting.TryTake(e.TrackId, out var id) ? e with { EntryId = id } : e).ToList();
    }

    private static List<PlaylistEntry>? MergeEntries(
        IReadOnlyList<PlaylistEntry> before, IReadOnlyList<PlaylistEntry> oursGiven, IReadOnlyList<PlaylistEntry> theirsGiven)
    {
        var ours = AlignedTo(before, oursGiven);
        var theirs = PairingAdditions(before, ours, AlignedTo(before, theirsGiven));
        var baseEntries = before.Select(e => e.EntryId).ToList();
        var ourEntries = ours.Select(e => e.EntryId).ToList();
        var theirEntries = theirs.Select(e => e.EntryId).ToList();

        if (ourEntries.SequenceEqual(baseEntries) || ourEntries.SequenceEqual(theirEntries))
            return theirs;
        if (theirEntries.SequenceEqual(baseEntries))
            return ours;

        // The songs both sides kept, in each side's order: a side whose order
        // differs from the original's reordered them.
        var baseSet = baseEntries.ToHashSet();
        var ourSet = ourEntries.ToHashSet();
        var theirSet = theirEntries.ToHashSet();
        var kept = baseEntries.Where(e => ourSet.Contains(e) && theirSet.Contains(e)).ToHashSet();
        var baseOrder = baseEntries.Where(kept.Contains).ToList();
        var ourOrder = ourEntries.Where(kept.Contains).ToList();
        var theirOrder = theirEntries.Where(kept.Contains).ToList();
        var weReordered = !ourOrder.SequenceEqual(baseOrder);
        var theyReordered = !theirOrder.SequenceEqual(baseOrder);
        if (weReordered && theyReordered && !ourOrder.SequenceEqual(theirOrder))
            return null;

        // Start from the side that reordered (theirs, when neither did), and
        // bring the other side's removals and additions over to it.
        var (skeleton, other, otherSet) = weReordered
            ? (ourEntries, theirEntries, theirSet)
            : (theirEntries, ourEntries, ourSet);

        var result = skeleton.Where(e => !baseSet.Contains(e) || otherSet.Contains(e)).ToList();
        var inResult = result.ToHashSet();
        for (var i = 0; i < other.Count; i++)
        {
            var entry = other[i];
            if (baseSet.Contains(entry) || inResult.Contains(entry))
                continue;

            // Added at the end of its own side - the way a song is almost
            // always added - stays at the end. Otherwise after the nearest
            // entry before it on its own side that the result has; at the top
            // when there is none.
            if (other.Skip(i + 1).All(e => !baseSet.Contains(e)))
            {
                result.Add(entry);
                inResult.Add(entry);
                continue;
            }

            var at = 0;
            for (var j = i - 1; j >= 0; j--)
            {
                var anchor = result.IndexOf(other[j]);
                if (anchor >= 0)
                {
                    at = anchor + 1;
                    break;
                }
            }
            result.Insert(at, entry);
            inResult.Add(entry);
        }

        // A row both sides still have keeps whichever copy can play its song.
        var byId = new Dictionary<Guid, PlaylistEntry>();
        foreach (var entry in ours.Concat(theirs).Concat(before))
        {
            if (!byId.TryGetValue(entry.EntryId, out var held) || (held.Track == null && entry.Track != null))
                byId[entry.EntryId] = entry;
        }
        return result.Select(id => byId[id]).ToList();
    }
}
