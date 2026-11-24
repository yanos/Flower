using System;

using Flower.Models;

namespace Flower.Services;

// A song as a list holds it. In a playlist or the queue that is an entry, and
// EntryId says which: one song there twice is two rows that do different
// things when one of them is dragged, removed or played. Anywhere else a song
// is listed once, and is all there is to say.
public readonly record struct ListedTrack(Track Track, Guid? EntryId = null)
{
    public static ListedTrack Of(PlaylistEntry entry) => new(entry.Track!, entry.EntryId);

    // What tells this row from every other row of its list.
    public Guid Key => EntryId ?? Track.Id;
}
