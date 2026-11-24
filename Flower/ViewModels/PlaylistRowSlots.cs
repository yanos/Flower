using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;

namespace Flower.ViewModels;

// Which entry of a playlist each of its rows on screen is, as a slot in its
// Tracks: the row carries the entry it shows (TrackRowViewModel.EntryId), and
// one song in the playlist twice is two entries.
public static class PlaylistRowSlots
{
    // The slots of `wanted`, leaving out any row that is not one of this
    // playlist's entries - a row built before a sync replaced them, say.
    public static List<int> SlotsOf(Playlist playlist, IEnumerable<TrackRowViewModel> wanted)
    {
        var shown = playlist.ShownEntries;
        var slotOf = new Dictionary<Guid, int>(shown.Count);
        for (var slot = 0; slot < shown.Count; slot++)
            slotOf[shown[slot].EntryId] = slot;

        return wanted
            .Select(row => row.EntryId is { } id && slotOf.TryGetValue(id, out var slot) ? slot : -1)
            .Where(slot => slot >= 0)
            .ToList();
    }
}
