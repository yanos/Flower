using System.Collections.Generic;
using System.Linq;

using Flower.Models;
using Flower.ViewModels;

namespace Flower.Services;

// Data behind the Albums grid (see MobileMainViewModel.AlbumGridRows and
// MainViewModel.AlbumGridTiles) - grouped as AlbumGroup groups an album, the
// way an album is picked (LibraryBrowserViewModel.RebuildSubListItems), and
// ordered alphabetically.
public static class AlbumGridBuilder
{
    public static List<AlbumTileViewModel> Build(IEnumerable<Track> tracks) =>
        tracks
            .Where(t => !string.IsNullOrEmpty(t.Album))
            .GroupBy(t => AlbumGroup.KeyOf(t)!)
            .Select(g =>
            {
                // The song the cover comes from (AlbumCover.TrackOf).
                var representative = AlbumCover.TrackOf(g)!;

                // One group can still span several EffectiveAlbumArtist
                // values - "X" and "X feat. Y" on an album with no album
                // artist tag (AlbumGroup). Labeling it with just the
                // representative track's own artist would be misleading (an
                // arbitrary one of several), so fall back to "Various
                // Artists" whenever the group isn't consistently attributed
                // to one.
                var artists = g.Select(t => t.EffectiveAlbumArtist).Distinct().ToList();
                var artist = artists.Count == 1 ? artists[0] : "Various Artists";

                return new AlbumTileViewModel
                {
                    Name = AlbumGroup.NameOf(g.Key),
                    Artist = artist,
                    RepresentativeTrack = representative,
                    MostRecentlyAdded = g.Max(t => t.DateAdded),
                    Tracks = g.ToList(),
                };
            })
            .OrderBy(a => a.Name)
            .ToList();
}
