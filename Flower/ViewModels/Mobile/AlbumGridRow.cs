using System;
using System.Collections.Generic;

namespace Flower.ViewModels.Mobile;

// One row of album tiles backing the album grids' item source (see
// MobileMainViewModel's RecentlyAddedAlbumRows/AlbumGridRows). Grouping tiles
// into rows lets the grid use a real virtualizing panel
// (VirtualizingStackPanel, one row per item) while still rendering as a
// multi-column grid - a flat collection in a plain UniformGrid isn't
// virtualizable (UniformGrid has no viewport awareness), so it had to
// realize/lay out every tile in the library at once. On a library with ~1,400
// albums that was a multi-second stall the first time the grid became visible.
//
// The column count is not fixed: two tiles fill a phone in portrait but leave
// the art absurdly large in landscape (and on a tablet), so it is derived from
// the grid's own measured width by ColumnsFor and the rows re-chunked whenever
// that changes - see MobileMainViewModel.AlbumGridColumns and
// AlbumGridColumnSizing, which does the measuring. Columns is carried on the
// row itself so the row template's UniformGrid can just bind to it. Desktop's
// own tile grid has always worked this way - see AlbumGridView.RebuildRows,
// which chunks the same AlbumTileViewModels against its own measured width.
public sealed class AlbumGridRow
{
    public required IReadOnlyList<AlbumTileViewModel> Tiles { get; init; }

    // Always the grid's full column count, not Tiles.Count - a short trailing
    // row still has to size its tiles like every other row rather than
    // stretching them across the width.
    public required int Columns { get; init; }

    // Tile width at which the art stops looking oversized and starts looking
    // cramped; the grid fits as many of these as it can. 150 is what puts two
    // columns on every phone in portrait (the layout these grids were designed
    // at) and five on that same phone turned sideways.
    private const double MinTileWidth = 150;

    // Must match the ColumnSpacing the grid views lay their tiles out with.
    private const double ColumnSpacing = 12;

    /// <summary>
    /// How many tiles fit across <paramref name="availableWidth"/> (the grid's
    /// content width, inside its own margin). Never fewer than two - a
    /// one-column album grid is a list, and the narrowest real phone still
    /// fits two.
    /// </summary>
    public static int ColumnsFor(double availableWidth)
    {
        if (!double.IsFinite(availableWidth) || availableWidth <= 0)
            return 2;

        // n columns need n*MinTileWidth plus (n-1) gaps, so solve for n by
        // handing every column a gap and giving one back to the width.
        var columns = (int)Math.Floor((availableWidth + ColumnSpacing) / (MinTileWidth + ColumnSpacing));
        return Math.Max(2, columns);
    }

    public static List<AlbumGridRow> Chunk(IReadOnlyList<AlbumTileViewModel> tiles, int columns)
    {
        if (columns < 1)
            throw new ArgumentOutOfRangeException(nameof(columns), columns, "A grid row needs at least one column.");

        var rows = new List<AlbumGridRow>(tiles.Count / columns + 1);
        for (var i = 0; i < tiles.Count; i += columns)
        {
            var take = Math.Min(columns, tiles.Count - i);
            var slice = new AlbumTileViewModel[take];
            for (var j = 0; j < take; j++)
                slice[j] = tiles[i + j];

            rows.Add(new AlbumGridRow { Tiles = slice, Columns = columns });
        }

        return rows;
    }
}
