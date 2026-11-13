using System.Collections.Generic;
using System.Linq;

using Flower.Models;
using Flower.ViewModels;

namespace Flower.Services;

// The album page's metadata, independent of navigation and server reachability.
public static class AlbumHeaderBuilder
{
    // album is the AlbumGroup key the album screen is picked by.
    public static AlbumTileViewModel? Build(IEnumerable<Track> library, string album)
    {
        var tracks = library.Where(t => AlbumGroup.KeyOf(t) == album).ToList();
        if (tracks.Count == 0)
            return null;

        var representative = AlbumCover.TrackOf(tracks)!;
        // The songs' own artist (Track.Artists) when they all share one,
        // whatever the case it is spelled in - the album artist is a sorting
        // tag, and often not the name the artist goes by on the songs. Songs
        // by several (a guest on one track, a compilation) fall back to the
        // album artist, with the same "Various Artists" as AlbumGridBuilder
        // where even that differs: a header naming one arbitrary artist out of
        // several would be misleading.
        var songArtists = ArtistNames.Of(tracks);
        var albumArtists = tracks.Select(t => t.EffectiveAlbumArtist).Distinct().ToList();
        var artist = songArtists.Count == 1 && tracks.All(t => !string.IsNullOrEmpty(t.Artists)) ? songArtists[0]
            : albumArtists.Count == 1 ? albumArtists[0]
            : "Various Artists";

        var header = new AlbumTileViewModel
        {
            Name = AlbumGroup.NameOf(album),
            Artist = artist,
            // The earliest, as the artist page's album sections take it
            // (BrowseSort.YearOf): a reissue's bonus tracks
            // carry the reissue's.
            Year = tracks.Select(t => t.Year).Where(y => !string.IsNullOrWhiteSpace(y)).Min(),
            RepresentativeTrack = representative,
            MostRecentlyAdded = tracks.Max(t => t.DateAdded),
            Tracks = tracks,
        };
        return header;
    }
}
