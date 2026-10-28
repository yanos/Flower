using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;

namespace Flower.Services;

// Whose cover an album is drawn with, and whose a song is.
//
// An album's is the first of its songs, in disc and track order, that has a
// picture - the one the server's album cover route reads too
// (LocalAlbumArtReader.FirstFor over LibrarySnapshot's order), so a tile and
// the picture fetched for it are the same. A song's is its own, where it has
// one, so songs of one album that carry different pictures each show theirs:
// in Now Playing, in a list of songs, in the queue. A song with none is drawn
// with its album's.
public static class AlbumCover
{
    public static bool HasOwn(Track track) =>
        track.ArtWidth > 0 && track.ArtHeight > 0 || track.ArtHash != null;

    // The song an album's cover comes from, or the first song when none has
    // one - something to stand for the album still.
    public static Track? TrackOf(IEnumerable<Track> albumTracks)
    {
        var ordered = albumTracks.OrderBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber).ToList();
        return ordered.FirstOrDefault(HasOwn) ?? ordered.FirstOrDefault();
    }

    // What the server names a song's cover in its catalog (TrackDto.CoverArt):
    // its album's id, unless the song carries a picture that is not its
    // album's, and then its own id - which the cover-art routes answer with
    // that song's picture (MediaEndpoints.CoverArtCandidates). An album whose
    // songs all carry its cover is still one picture, under one name.
    public static string CoverArtId(Track track, string? albumCoverHash) =>
        track.ArtHash is { } own && albumCoverHash != null && !string.Equals(own, albumCoverHash, StringComparison.Ordinal)
            ? track.Id.ToKey()
            : CatalogIdentity.AlbumId(track.EffectiveAlbumArtist, track.Album);
}
