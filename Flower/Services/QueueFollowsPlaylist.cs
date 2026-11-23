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
// Ids rather than tracks for the two orders, and all of a playlist's entries:
// a song on an unplugged drive is not in Playlist.Tracks, and has not left.
//
// A song in the playlist twice is two entries, and an edit is to one of them.
// The two orders are all that arrives, from a drag, a menu or another device
// alike, so Align works out which entry went where. The nth copy of a song in
// the queue is taken to be its nth copy in the playlist.
public static class QueueFollowsPlaylist
{
    public readonly record struct Result(List<Track> Queue, int Place, bool PlayingLeft);

    private sealed class Entry(Track track, bool isPlaying)
    {
        public Track Track { get; } = track;
        public bool IsPlaying { get; } = isPlaying;
        public bool IsPlaylists { get; set; }

        // Which copy of its song this is, or -1 for the song playing after it
        // has left the playlist.
        public int Copy { get; set; } = -1;
    }

    public static Result Apply(
        IReadOnlyList<Track> queue, int playing, int linedUp, bool playingHasLeft, bool followsOrder,
        IReadOnlyList<Guid> before, IReadOnlyList<Guid> after, IReadOnlyList<Track> present)
    {
        var block = new List<Track>();
        var entries = new List<Entry>(queue.Count);
        for (var i = 0; i < queue.Count; i++)
        {
            if (playing >= 0 && i > playing && i <= playing + linedUp)
                block.Add(queue[i]);
            else
                entries.Add(new Entry(queue[i], i == playing));
        }

        var sources = Align(before, after);
        var copyOf = CopyNumbers(before);
        var survivors = new HashSet<(Guid Id, int Copy)>();
        foreach (var source in sources)
        {
            if (source >= 0)
                survivors.Add((before[source], copyOf[source]));
        }

        var left = playingHasLeft;
        var kept = new List<Entry>(entries.Count);
        var copies = new Dictionary<Guid, int>();
        var inBefore = Count(before);
        foreach (var entry in entries)
        {
            if (!(entry.IsPlaying && playingHasLeft))
            {
                var copy = copies.GetValueOrDefault(entry.Track.Id);
                copies[entry.Track.Id] = copy + 1;
                entry.Copy = copy;
                if (copy < inBefore.GetValueOrDefault(entry.Track.Id) && !survivors.Contains((entry.Track.Id, copy)))
                {
                    if (!entry.IsPlaying)
                        continue;
                    left = true;
                }
            }
            kept.Add(entry);
        }

        foreach (var entry in kept)
        {
            if (entry.IsPlaying && left)
                continue;
            entry.IsPlaylists = survivors.Contains((entry.Track.Id, entry.Copy));
        }

        var own = kept.Where(e => e.IsPlaylists).ToList();
        var arranged = Arrange(own, followsOrder, before, after, sources, copyOf, present);

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
            if (entry.IsPlaylists)
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
        var result = ordered.Select(e => e.Track).ToList();
        if (place >= 0)
            result.InsertRange(place + 1, block);
        return new Result(result, place, left);
    }

    private static List<Entry> Arrange(
        List<Entry> own, bool followsOrder, IReadOnlyList<Guid> before, IReadOnlyList<Guid> after,
        int[] sources, int[] copyOf, IReadOnlyList<Track> present)
    {
        var byId = new Dictionary<Guid, Track>();
        foreach (var track in present)
            byId.TryAdd(track.Id, track);

        var waiting = followsOrder
            ? own.ToDictionary(entry => (entry.Track.Id, entry.Copy))
            : [];

        var arranged = new List<Entry>(own.Count);
        if (!followsOrder)
            arranged.AddRange(own);

        for (var slot = 0; slot < after.Count; slot++)
        {
            var source = sources[slot];
            if (source >= 0)
            {
                if (waiting.Remove((before[source], copyOf[source]), out var entry))
                    arranged.Add(entry);
            }
            // Absent from the library right now: nothing to queue.
            else if (byId.TryGetValue(after[slot], out var track))
            {
                arranged.Add(new Entry(track, isPlaying: false) { IsPlaylists = true });
            }
        }
        return arranged;
    }

    // For each slot of `after`, the slot of `before` it came from, or -1 for
    // one that is new. Exact for what a single gesture does - entries removed,
    // entries added, one entry moved - except that twin copies ending up side
    // by side cannot be told apart. Anything else, which only a sync brings,
    // pairs the nth copy of a song with its nth copy.
    private static int[] Align(IReadOnlyList<Guid> before, IReadOnlyList<Guid> after)
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

    private static int[] CopyNumbers(IReadOnlyList<Guid> ids)
    {
        var seen = new Dictionary<Guid, int>();
        var copies = new int[ids.Count];
        for (var i = 0; i < ids.Count; i++)
        {
            copies[i] = seen.GetValueOrDefault(ids[i]);
            seen[ids[i]] = copies[i] + 1;
        }
        return copies;
    }

    private static Dictionary<Guid, int> Count(IReadOnlyList<Guid> ids)
    {
        var counts = new Dictionary<Guid, int>();
        foreach (var id in ids)
            counts[id] = counts.GetValueOrDefault(id) + 1;
        return counts;
    }
}
