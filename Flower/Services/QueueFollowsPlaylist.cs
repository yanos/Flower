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
public static class QueueFollowsPlaylist
{
    public readonly record struct Result(List<Track> Queue, int Place, bool PlayingLeft);

    private sealed class Entry(Track track, bool isPlaying)
    {
        public Track Track { get; } = track;
        public bool IsPlaying { get; } = isPlaying;
        public bool IsPlaylists { get; set; }
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

        var counted = Count(after);
        var gone = Subtract(Count(before), counted);
        var added = Subtract(counted, Count(before));

        // The first copy of a song in twice goes, as Playlist.RemoveEntries
        // takes the first.
        var left = playingHasLeft;
        var kept = new List<Entry>(entries.Count);
        foreach (var entry in entries)
        {
            var alreadyLeft = entry.IsPlaying && playingHasLeft;
            if (!alreadyLeft && gone.GetValueOrDefault(entry.Track.Id) > 0)
            {
                gone[entry.Track.Id]--;
                if (!entry.IsPlaying)
                    continue;
                left = true;
            }
            kept.Add(entry);
        }

        foreach (var entry in kept)
        {
            if ((entry.IsPlaying && left) || counted.GetValueOrDefault(entry.Track.Id) == 0)
                continue;
            counted[entry.Track.Id]--;
            entry.IsPlaylists = true;
        }

        var own = kept.Where(e => e.IsPlaylists).ToList();
        var arranged = Arrange(own, followsOrder, after, added, present);

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
        List<Entry> own, bool followsOrder, IReadOnlyList<Guid> after, Dictionary<Guid, int> added, IReadOnlyList<Track> present)
    {
        var byId = new Dictionary<Guid, Track>();
        foreach (var track in present)
            byId.TryAdd(track.Id, track);

        var waiting = new Dictionary<Guid, Queue<Entry>>();
        if (followsOrder)
        {
            foreach (var entry in own)
            {
                if (!waiting.TryGetValue(entry.Track.Id, out var copies))
                    waiting[entry.Track.Id] = copies = new Queue<Entry>();
                copies.Enqueue(entry);
            }
        }

        var arranged = new List<Entry>(own.Count);
        if (!followsOrder)
            arranged.AddRange(own);

        foreach (var id in after)
        {
            if (waiting.TryGetValue(id, out var copies) && copies.Count > 0)
                arranged.Add(copies.Dequeue());
            else if (added.GetValueOrDefault(id) > 0)
            {
                added[id]--;
                // Absent from the library right now: nothing to queue.
                if (byId.TryGetValue(id, out var track))
                    arranged.Add(new Entry(track, isPlaying: false) { IsPlaylists = true });
            }
        }
        return arranged;
    }

    private static Dictionary<Guid, int> Count(IReadOnlyList<Guid> ids)
    {
        var counts = new Dictionary<Guid, int>();
        foreach (var id in ids)
            counts[id] = counts.GetValueOrDefault(id) + 1;
        return counts;
    }

    private static Dictionary<Guid, int> Subtract(Dictionary<Guid, int> from, Dictionary<Guid, int> counts)
    {
        var difference = new Dictionary<Guid, int>();
        foreach (var (id, count) in from)
        {
            var left = count - counts.GetValueOrDefault(id);
            if (left > 0)
                difference[id] = left;
        }
        return difference;
    }
}
