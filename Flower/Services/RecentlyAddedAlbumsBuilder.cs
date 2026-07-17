using System.Collections.Generic;
using System.Linq;

using Flower.Models;
using Flower.ViewModels.Mobile;

namespace Flower.Services;

// Groups tracks into per-album summaries ordered by recency - the data behind
// mobile's default "Recently Added" album grid (see MobileMainViewModel).
// Grouped by (Album, EffectiveAlbumArtist), not Album alone, so two different
// artists' same-named album ("Greatest Hits") do not collide into one tile -
// same reasoning as LibraryOpenSubsonicMapper's server-side grouping.
// EffectiveAlbumArtist (rather than raw per-track Artists) keeps a various-
// artists compilation - same Album, differing per-track Artists, but a
// consistent (or absent) AlbumArtists tag - as a single tile instead of
// fragmenting into one per distinct track artist. Placeholder tracks (Path ==
// null, not yet downloaded - see SYNC-PLAN.md Phase 3) count toward an
// album's presence/recency same as any other track: they are legitimately
// known to this device already, just not downloaded yet.
public static class RecentlyAddedAlbumsBuilder
{
    public static List<AlbumTileViewModel> Build(IEnumerable<Track> tracks) =>
        tracks
            .Where(t => !string.IsNullOrEmpty(t.Album))
            .GroupBy(t => (Album: t.Album!, Artist: t.EffectiveAlbumArtist))
            .Select(g =>
            {
                var mostRecent = g.OrderByDescending(t => t.DateAdded).First();
                return new AlbumTileViewModel
                {
                    Name = g.Key.Album,
                    Artist = g.Key.Artist,
                    RepresentativeTrack = mostRecent,
                    MostRecentlyAdded = g.Max(t => t.DateAdded),
                };
            })
            .OrderByDescending(a => a.MostRecentlyAdded)
            .ToList();
}
