using System.Collections.Generic;

using Flower.Models;

namespace Flower.Services;

// Where an album's songs go from one disc to the next, for the header each disc
// gets in an album's own list - the desktop's expanded album and the Artists
// page's album cards (AlbumTracksViewModel), the phone's album screen
// (TrackListScreenView). Only an album of more than one disc, in disc order,
// with every song's disc known: a list sorted some other way breaks into a run
// per song, and a song with no disc has no header to go under.
public static class DiscHeaders
{
    // The disc each song begins, or 0 for a song that carries on the one
    // above it - all 0 where the list gets no headers.
    public static int[] Starts(IReadOnlyList<Track> tracks)
    {
        var starts = new int[tracks.Count];
        if (tracks.Count == 0 || tracks[0].DiscNumber == 0)
            return starts;

        var discs = 1;
        for (var i = 1; i < tracks.Count; i++)
        {
            var disc = tracks[i].DiscNumber;
            if (disc == 0 || disc < tracks[i - 1].DiscNumber)
                return starts;
            if (disc != tracks[i - 1].DiscNumber)
                discs++;
        }
        if (discs < 2)
            return starts;

        for (var i = 0; i < tracks.Count; i++)
        {
            if (i == 0 || tracks[i].DiscNumber != tracks[i - 1].DiscNumber)
                starts[i] = (int)tracks[i].DiscNumber;
        }
        return starts;
    }

    public static string Title(int disc) => $"Disc {disc}";
}
