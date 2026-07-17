using System.Collections.Generic;
using System.Linq;

using Flower.Models;
using Flower.ViewModels.Mobile;

namespace Flower.Services;

// Data behind mobile's Albums tab grid (see MobileMainViewModel.AlbumGridItems) -
// same art-tile presentation as the Recently Added grid, but grouped by Album
// name alone and ordered alphabetically, matching MainViewModel.RebuildSubListItems'
// existing Albums case (the plain-text picker this grid replaces on mobile) rather
// than RecentlyAddedAlbumsBuilder's (Album, EffectiveAlbumArtist) grouping.
public static class AlbumGridBuilder
{
    public static List<AlbumTileViewModel> Build(IEnumerable<Track> tracks) =>
        tracks
            .Where(t => !string.IsNullOrEmpty(t.Album))
            .GroupBy(t => t.Album!)
            .Select(g =>
            {
                var representative = g.OrderByDescending(t => t.DateAdded).First();

                // Grouped by Album name alone here (unlike RecentlyAddedAlbumsBuilder),
                // so a single group can legitimately span several distinct
                // EffectiveAlbumArtist values - a various-artists compilation. Labeling
                // it with just the representative track's own artist would be
                // misleading (an arbitrary one of several), so fall back to "Various
                // Artists" whenever the group isn't consistently attributed to one.
                var artists = g.Select(t => t.EffectiveAlbumArtist).Distinct().ToList();
                var artist = artists.Count == 1 ? artists[0] : "Various Artists";

                return new AlbumTileViewModel
                {
                    Name = g.Key,
                    Artist = artist,
                    RepresentativeTrack = representative,
                    MostRecentlyAdded = g.Max(t => t.DateAdded),
                };
            })
            .OrderBy(a => a.Name)
            .ToList();
}
