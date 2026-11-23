using System.Collections.Generic;
using System.Linq;

using Flower.Models;

namespace Flower.Services;

// Songs the library holds more than once: the same title, artist, album and
// length (Track.SyncKey) on separate rows, as when a server has two files of
// one album under slightly different names. Untitled tracks are left out,
// because their key is little more than a duration and says nothing about
// whether two of them are the same song.
public static class DuplicateSongs
{
    public static IReadOnlyList<IGrouping<string, Track>> Find(IEnumerable<Track> tracks) =>
        tracks
            .Where(t => !string.IsNullOrWhiteSpace(t.Title))
            .GroupBy(t => t.SyncKey)
            .Where(group => group.Skip(1).Any())
            .ToList();

    // What tells two copies apart, since everything else about them matches:
    // the file here, or for a song only on the server, the server's file.
    public static string? FileNameOf(Track track) =>
        System.IO.Path.GetFileName(track.Path ?? track.OriginRelativePath);
}
