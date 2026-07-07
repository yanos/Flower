using System.Collections.Generic;
using System.Linq;

using Flower.Models;
using Flower.ViewModels.Mobile;

namespace Flower.Services;

// Data behind mobile's Albums tab grid (see MobileMainViewModel.AlbumGridItems) -
// same art-tile presentation as the Recently Added grid, but grouped by Album
// name alone and ordered alphabetically, matching MainViewModel.RebuildSubListItems'
// existing Albums case (the plain-text picker this grid replaces on mobile) rather
// than RecentlyAddedAlbumsBuilder's (Album, Artist) grouping.
public static class AlbumGridBuilder
{
    public static List<AlbumTileViewModel> Build(IEnumerable<Track> tracks) =>
        tracks
            .Where(t => !string.IsNullOrEmpty(t.Album))
            .GroupBy(t => t.Album!)
            .Select(g =>
            {
                var representative = g.OrderByDescending(t => t.DateAdded).First();
                return new AlbumTileViewModel
                {
                    Name = g.Key,
                    Artist = representative.Artists,
                    RepresentativeTrack = representative,
                    MostRecentlyAdded = g.Max(t => t.DateAdded),
                };
            })
            .OrderBy(a => a.Name)
            .ToList();
}
