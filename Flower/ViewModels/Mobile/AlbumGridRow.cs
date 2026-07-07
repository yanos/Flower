using System.Collections.Generic;

namespace Flower.ViewModels.Mobile;

// Two-tile row backing the album grids' item source (see MobileMainViewModel's
// RecentlyAddedAlbumRows/AlbumGridRows). Grouping tiles into rows of two lets the
// grid use a real virtualizing panel (VirtualizingStackPanel, one row per item)
// while still rendering as a 2-column grid - a flat collection in a plain
// UniformGrid isn't virtualizable (UniformGrid has no viewport awareness), so it
// had to realize/lay out every tile in the library at once. On a library with
// ~1,400 albums that was a multi-second stall the first time the grid became
// visible.
public sealed class AlbumGridRow
{
    public required AlbumTileViewModel First { get; init; }
    public AlbumTileViewModel? Second { get; init; }

    public static List<AlbumGridRow> Chunk(IReadOnlyList<AlbumTileViewModel> tiles)
    {
        var rows = new List<AlbumGridRow>(tiles.Count / 2 + 1);
        for (var i = 0; i < tiles.Count; i += 2)
        {
            rows.Add(new AlbumGridRow
            {
                First = tiles[i],
                Second = i + 1 < tiles.Count ? tiles[i + 1] : null,
            });
        }

        return rows;
    }
}
