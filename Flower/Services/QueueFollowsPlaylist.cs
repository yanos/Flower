using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;

namespace Flower.Services;

// What an edit to a playlist does to the queue playing it. The queue is a copy
// with edits of its own, so it takes the difference between the playlist's old
// and new contents rather than being rebuilt from the new ones:
//
//   - A song that left the playlist leaves the queue, unless it is playing:
//     that one stays in its place to finish (PlayingLeft).
//   - A song new to the playlist joins at its place there.
//   - The playlist's songs take its new order, unless the queue was played
//     in an order of its own (a sorted column).
//   - A song taken out of the queue by hand stays out. Songs lined up by hand
//     stay straight after the song playing, and songs added at the end stay
//     after the playlist's own.
//
// All of a playlist's entries rather than its tracks: a song on an unplugged
// drive is not in Playlist.Tracks, and has not left.
//
// A song in the playlist twice is two entries, and an edit is to one of them.
// So each of the queue's songs from the playlist is that playlist's entry,
// under its id, and those ids say where each of them went, whichever device
// made the edit; a song lined up by hand has an id the playlist never had. A
// copy with no ids in common has no history with this one (a sync that gave
// this device's rows the other side's ids), and is paired by song.
public static class QueueFollowsPlaylist
{
    // Queue is the queue's entries afterwards, its songs from the playlist
    // under the ids they have there now. Changed says whether its songs or
    // their order did; Renamed, when they did not, whether those ids did.
    public readonly record struct Result(
        List<PlaylistEntry> Queue, int Place, bool PlayingLeft, bool Changed, bool Renamed = false);

    private sealed class Entry(PlaylistEntry row, bool isPlaying, int origin)
    {
        public PlaylistEntry Row { get; } = row;
        public bool IsPlaying { get; } = isPlaying;

        // Its slot in the playlist as it is now, or -1.
        public int Origin { get; set; } = origin;
    }

    public static Result Apply(
        IReadOnlyList<PlaylistEntry> queue, int playing, int linedUp, bool playingHasLeft,
        bool followsOrder, IReadOnlyList<PlaylistEntry> before, IReadOnlyList<PlaylistEntry> after)
    {
        var slotOf = new Dictionary<Guid, int>(before.Count);
        for (var slot = 0; slot < before.Count; slot++)
            slotOf[before[slot].EntryId] = slot;

        var origins = queue.Select(row => slotOf.TryGetValue(row.EntryId, out var slot) ? slot : -1).ToList();
        PlaylistEntry Named(PlaylistEntry row, int slot) =>
            slot >= 0 && row.EntryId != after[slot].EntryId ? row with { EntryId = after[slot].EntryId } : row;

        var sources = Align(before, after, slotOf);
        if (IsSameOrder(sources, before.Count))
        {
            var named = queue.Select((row, i) => Named(row, origins[i])).ToList();
            var renamed = named.Where((row, i) => row.EntryId != queue[i].EntryId).Any();
            return new Result(named, playing, playingHasLeft, Changed: false, Renamed: renamed);
        }

        var movedTo = new int[before.Count];
        Array.Fill(movedTo, -1);
        for (var slot = 0; slot < sources.Length; slot++)
        {
            if (sources[slot] >= 0)
                movedTo[sources[slot]] = slot;
        }

        var left = playingHasLeft;
        var block = new List<Entry>();
        var kept = new List<Entry>(queue.Count);
        for (var i = 0; i < queue.Count; i++)
        {
            var isPlaying = i == playing;
            var origin = origins[i];
            var now = origin >= 0 ? movedTo[origin] : -1;

            if (playing >= 0 && i > playing && i <= playing + linedUp)
            {
                block.Add(new Entry(queue[i], false, now));
            }
            else if (isPlaying && playingHasLeft)
            {
                kept.Add(new Entry(queue[i], true, -1));
            }
            else if (origin >= 0 && now < 0)
            {
                if (!isPlaying)
                    continue;
                left = true;
                kept.Add(new Entry(queue[i], true, -1));
            }
            else
            {
                kept.Add(new Entry(queue[i], isPlaying, now));
            }
        }

        var own = kept.Where(e => e.Origin >= 0).ToList();
        var arranged = Arrange(own, followsOrder, after, sources);

        // What is not the playlist's follows the song it followed. Songs past
        // the last of the playlist's stay past it, whatever joins, except the
        // song playing: what joins after it has to play after it.
        var first = new List<Entry>();
        var tail = new List<Entry>();
        var following = new Dictionary<Entry, List<Entry>>();
        Entry? previous = null;
        var seen = 0;
        foreach (var entry in kept)
        {
            if (entry.Origin >= 0)
            {
                previous = entry;
                seen++;
            }
            else if (!entry.IsPlaying && seen == own.Count)
                tail.Add(entry);
            else if (previous == null)
                first.Add(entry);
            else
            {
                if (!following.TryGetValue(previous, out var trailing))
                    following[previous] = trailing = [];
                trailing.Add(entry);
            }
        }

        var ordered = new List<Entry>(first);
        foreach (var entry in arranged)
        {
            ordered.Add(entry);
            if (following.TryGetValue(entry, out var behind))
                ordered.AddRange(behind);
        }
        ordered.AddRange(tail);

        var place = ordered.FindIndex(e => e.IsPlaying);
        if (place >= 0)
            ordered.InsertRange(place + 1, block);
        return new Result(ordered.Select(e => Named(e.Row, e.Origin)).ToList(), place, left, Changed: true);
    }

    private static List<Entry> Arrange(
        List<Entry> own, bool followsOrder, IReadOnlyList<PlaylistEntry> after, int[] sources)
    {
        var waiting = followsOrder ? own.ToDictionary(entry => entry.Origin) : [];

        var arranged = new List<Entry>(own.Count);
        if (!followsOrder)
            arranged.AddRange(own);

        for (var slot = 0; slot < after.Count; slot++)
        {
            if (sources[slot] >= 0)
            {
                if (waiting.Remove(slot, out var entry))
                    arranged.Add(entry);
            }
            // Absent from the library right now: nothing to queue.
            else if (after[slot].Track != null)
            {
                arranged.Add(new Entry(after[slot], isPlaying: false, slot));
            }
        }
        return arranged;
    }

    private static bool IsSameOrder(int[] sources, int beforeCount)
    {
        if (sources.Length != beforeCount)
            return false;

        for (var slot = 0; slot < sources.Length; slot++)
        {
            if (sources[slot] != slot)
                return false;
        }
        return true;
    }

    // For each slot of `after`, the slot of `before` it came from, or -1 for
    // one that is new.
    private static int[] Align(
        IReadOnlyList<PlaylistEntry> before, IReadOnlyList<PlaylistEntry> after, Dictionary<Guid, int> slotOf)
    {
        var sources = new int[after.Count];
        Array.Fill(sources, -1);
        var sharesIds = false;
        for (var slot = 0; slot < after.Count; slot++)
        {
            if (!slotOf.TryGetValue(after[slot].EntryId, out var source))
                continue;

            sources[slot] = source;
            sharesIds = true;
        }

        if (sharesIds)
            return sources;

        var waiting = new CopiesBySong<int>();
        for (var slot = 0; slot < before.Count; slot++)
            waiting.Add(before[slot].TrackId, slot);

        for (var slot = 0; slot < after.Count; slot++)
        {
            if (waiting.TryTake(after[slot].TrackId, out var source))
                sources[slot] = source;
        }
        return sources;
    }
}
