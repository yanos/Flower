using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;

namespace Flower.ViewModels;

// Which entry of a playlist each of its rows on screen is. The list can be
// sorted or filtered, so a row's position says nothing; but a filter keeps or
// drops every copy of a song together and the sort is stable, so the nth row
// of a song is always the nth entry of that song.
public static class PlaylistRowSlots
{
    // The slots of `wanted`, leaving out any row that is not one of `rows`.
    public static List<int> SlotsOf(
        IReadOnlyList<Track> entries, IReadOnlyList<TrackRowViewModel> rows, IEnumerable<TrackRowViewModel> wanted)
    {
        var slotsBySong = new Dictionary<Guid, List<int>>();
        for (var slot = 0; slot < entries.Count; slot++)
        {
            if (!slotsBySong.TryGetValue(entries[slot].Id, out var slots))
                slotsBySong[entries[slot].Id] = slots = [];
            slots.Add(slot);
        }

        var seen = new Dictionary<Guid, int>();
        var slotOfRow = new Dictionary<TrackRowViewModel, int>(ReferenceEqualityComparer.Instance);
        foreach (var row in rows)
        {
            var occurrence = seen.GetValueOrDefault(row.Track.Id);
            seen[row.Track.Id] = occurrence + 1;
            if (slotsBySong.TryGetValue(row.Track.Id, out var slots) && occurrence < slots.Count)
                slotOfRow[row] = slots[occurrence];
        }

        return wanted.Where(slotOfRow.ContainsKey).Select(row => slotOfRow[row]).ToList();
    }
}
