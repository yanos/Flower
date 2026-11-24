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
// So each queue row keeps its origin, the slot of the entry it came from in
// `before` (-1 for one that is not the playlist's), and the entries' keys say
// where each of them went. A copy that arrived from another device has no keys
// in common with this one, and only its order to go on (AlignByOrder).
public static class QueueFollowsPlaylist
{
    public readonly record struct Result(
        List<Track> Queue, int Place, bool PlayingLeft, List<int> Origins, bool Changed);

    private sealed class Entry(Track track, bool isPlaying, int origin)
    {
        public Track Track { get; } = track;
        public bool IsPlaying { get; } = isPlaying;

        // Its slot in the playlist as it is now, or -1.
        public int Origin { get; set; } = origin;
    }

    // The entries a queue just made from a playlist came from: the queue was
    // built from the playlist's rows, and a sort or filter keeps a song's
    // copies in order, so its nth copy here is the nth copy there.
    public static List<int> OriginsIn(IReadOnlyList<Track> queue, IReadOnlyList<Playlist.StoredEntry> entries)
    {
        var waiting = new Dictionary<Guid, Queue<int>>();
        for (var slot = 0; slot < entries.Count; slot++)
        {
            if (!waiting.TryGetValue(entries[slot].TrackId, out var slots))
                waiting[entries[slot].TrackId] = slots = new Queue<int>();
            slots.Enqueue(slot);
        }

        var origins = new List<int>(queue.Count);
        foreach (var track in queue)
            origins.Add(waiting.TryGetValue(track.Id, out var slots) && slots.Count > 0 ? slots.Dequeue() : -1);
        return origins;
    }

    // Whether saved origins still describe this queue and these entries: each
    // names a distinct entry holding the song in its row.
    public static bool OriginsFit(
        IReadOnlyList<Track> queue, IReadOnlyList<int> origins, IReadOnlyList<Playlist.StoredEntry> entries)
    {
        if (origins.Count != queue.Count)
            return false;

        var used = new HashSet<int>();
        for (var i = 0; i < queue.Count; i++)
        {
            var origin = origins[i];
            if (origin == -1)
                continue;
            if (origin < 0 || origin >= entries.Count || entries[origin].TrackId != queue[i].Id || !used.Add(origin))
                return false;
        }
        return true;
    }

    public static Result Apply(
        IReadOnlyList<Track> queue, IReadOnlyList<int> origins, int playing, int linedUp, bool playingHasLeft,
        bool followsOrder, IReadOnlyList<Playlist.StoredEntry> before, IReadOnlyList<Playlist.StoredEntry> after,
        IReadOnlyList<Track> present)
    {
        var playingOrigin = playing >= 0 && !playingHasLeft ? origins[playing] : -1;
        var sources = Align(before, after, playingOrigin);
        if (IsSameOrder(sources, before.Count))
            return new Result([.. queue], playing, playingHasLeft, [.. origins], Changed: false);

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
        var arranged = Arrange(own, followsOrder, after, sources, present);

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
        return new Result(
            ordered.Select(e => e.Track).ToList(), place, left, ordered.Select(e => e.Origin).ToList(), Changed: true);
    }

    private static List<Entry> Arrange(
        List<Entry> own, bool followsOrder, IReadOnlyList<Playlist.StoredEntry> after, int[] sources,
        IReadOnlyList<Track> present)
    {
        var byId = new Dictionary<Guid, Track>();
        foreach (var track in present)
            byId.TryAdd(track.Id, track);

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
            else if (byId.TryGetValue(after[slot].TrackId, out var track))
            {
                arranged.Add(new Entry(track, isPlaying: false, slot));
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
        IReadOnlyList<Playlist.StoredEntry> before, IReadOnlyList<Playlist.StoredEntry> after, int playingOrigin)
    {
        var slotOfKey = new Dictionary<long, int>(before.Count);
        for (var slot = 0; slot < before.Count; slot++)
        {
            if (before[slot].Key != 0)
                slotOfKey[before[slot].Key] = slot;
        }

        var sources = new int[after.Count];
        Array.Fill(sources, -1);
        var claimed = new bool[before.Count];
        var sharesKeys = false;
        for (var slot = 0; slot < after.Count; slot++)
        {
            if (after[slot].Key != 0 && slotOfKey.TryGetValue(after[slot].Key, out var source))
            {
                sources[slot] = source;
                claimed[source] = true;
                sharesKeys = true;
            }
        }

        if (!sharesKeys)
        {
            sources = AlignByOrder(before.Select(e => e.TrackId).ToList(), after.Select(e => e.TrackId).ToList());
            KeepPlayingCopy(before, sources, playingOrigin);
            return sources;
        }

        // A song back from an unplugged drive had no key while it was away.
        var returning = new Dictionary<Guid, Queue<int>>();
        for (var slot = 0; slot < before.Count; slot++)
        {
            if (claimed[slot] || before[slot].Key != 0)
                continue;
            if (!returning.TryGetValue(before[slot].TrackId, out var slots))
                returning[before[slot].TrackId] = slots = new Queue<int>();
            slots.Enqueue(slot);
        }
        for (var slot = 0; slot < after.Count; slot++)
        {
            if (sources[slot] < 0 && returning.TryGetValue(after[slot].TrackId, out var slots) && slots.Count > 0)
                sources[slot] = slots.Dequeue();
        }
        return sources;
    }

    // Copies side by side look the same from another device, so when one of
    // them went there is no saying which. It is not the one playing.
    private static void KeepPlayingCopy(IReadOnlyList<Playlist.StoredEntry> before, int[] sources, int playingOrigin)
    {
        if (playingOrigin < 0 || playingOrigin >= before.Count || Array.IndexOf(sources, playingOrigin) >= 0)
            return;

        var id = before[playingOrigin].TrackId;
        foreach (var step in new[] { -1, 1 })
        {
            for (var twin = playingOrigin + step; twin >= 0 && twin < before.Count && before[twin].TrackId == id; twin += step)
            {
                var slot = Array.IndexOf(sources, twin);
                if (slot < 0)
                    continue;

                sources[slot] = playingOrigin;
                return;
            }
        }
    }

    // The same, from the two orders alone. Exact for what a single gesture
    // does - entries removed, entries added, one entry moved - except that
    // twin copies ending up side by side cannot be told apart. Anything else
    // pairs the nth copy of a song with its nth copy.
    private static int[] AlignByOrder(IReadOnlyList<Guid> before, IReadOnlyList<Guid> after)
    {
        var sources = new int[after.Count];
        Array.Fill(sources, -1);

        var shorter = Math.Min(before.Count, after.Count);
        var start = 0;
        while (start < shorter && before[start] == after[start])
        {
            sources[start] = start;
            start++;
        }

        var end = 0;
        while (end < shorter - start && before[before.Count - 1 - end] == after[after.Count - 1 - end])
        {
            sources[after.Count - 1 - end] = before.Count - 1 - end;
            end++;
        }

        var beforeLength = before.Count - start - end;
        var afterLength = after.Count - start - end;
        if (beforeLength == 0 || afterLength == 0)
            return sources;

        if (beforeLength == afterLength && TryAlignMove(before, after, start, beforeLength, sources))
            return sources;

        if (beforeLength != afterLength && TryAlignInOrder(before, after, start, beforeLength, afterLength, sources))
            return sources;

        var waiting = new Dictionary<Guid, Queue<int>>();
        for (var i = start; i < start + beforeLength; i++)
        {
            if (!waiting.TryGetValue(before[i], out var slots))
                waiting[before[i]] = slots = new Queue<int>();
            slots.Enqueue(i);
        }
        for (var i = start; i < start + afterLength; i++)
        {
            if (waiting.TryGetValue(after[i], out var slots) && slots.Count > 0)
                sources[i] = slots.Dequeue();
        }
        return sources;
    }

    // One entry taken from one end of the changed stretch to the other.
    private static bool TryAlignMove(
        IReadOnlyList<Guid> before, IReadOnlyList<Guid> after, int start, int length, int[] sources)
    {
        var last = start + length - 1;
        if (before[last] == after[start] && Same(before, start, after, start + 1, length - 1))
        {
            sources[start] = last;
            for (var i = 1; i < length; i++)
                sources[start + i] = start + i - 1;
            return true;
        }

        if (before[start] == after[last] && Same(before, start + 1, after, start, length - 1))
        {
            sources[last] = start;
            for (var i = 0; i < length - 1; i++)
                sources[start + i] = start + i + 1;
            return true;
        }

        return false;
    }

    // Entries removed or added with the rest left in order.
    private static bool TryAlignInOrder(
        IReadOnlyList<Guid> before, IReadOnlyList<Guid> after, int start, int beforeLength, int afterLength, int[] sources)
    {
        var matched = new List<(int After, int Before)>();
        var (b, a) = (0, 0);
        while (b < beforeLength && a < afterLength)
        {
            if (before[start + b] == after[start + a])
            {
                matched.Add((start + a, start + b));
                b++;
                a++;
            }
            else if (afterLength < beforeLength)
            {
                b++;
            }
            else
            {
                a++;
            }
        }

        if (matched.Count != Math.Min(beforeLength, afterLength))
            return false;

        foreach (var (afterSlot, beforeSlot) in matched)
            sources[afterSlot] = beforeSlot;
        return true;
    }

    private static bool Same(IReadOnlyList<Guid> first, int firstStart, IReadOnlyList<Guid> second, int secondStart, int length)
    {
        for (var i = 0; i < length; i++)
        {
            if (first[firstStart + i] != second[secondStart + i])
                return false;
        }
        return true;
    }
}
