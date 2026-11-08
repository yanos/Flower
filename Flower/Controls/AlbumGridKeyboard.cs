using System.Collections.Generic;

using Avalonia.Input;

using Flower.ViewModels;

namespace Flower.Controls;

// Where the arrow keys go in the Albums grid (AlbumGridView): Left and Right
// along the albums in order, running on from the end of one row to the start
// of the next, and Up and Down a row at a time in the same column. Down from
// a row whose next row is too short to have that column goes to the last
// album, so every album can be reached. Nothing goes past the first album or
// the last.
public static class AlbumGridKeyboard
{
    // The album the arrow lands on, or null when it lands nowhere new: not an
    // arrow, or already at that edge. With no album to start from, any arrow
    // starts at the first.
    public static int? Target(Key key, int from, int count, int columns)
    {
        if (count == 0 || columns < 1 || key is not (Key.Left or Key.Right or Key.Up or Key.Down))
            return null;
        if (from < 0 || from >= count)
            return 0;

        var to = key switch
        {
            Key.Left => from - 1,
            Key.Right => from + 1,
            Key.Up => from - columns,
            _ => from + columns,
        };
        if (key == Key.Down && to >= count && from / columns < (count - 1) / columns)
            to = count - 1;

        return to >= 0 && to < count ? to : null;
    }

    // The album the keys move from: the one they last moved to, while it is
    // still selected; otherwise the open album; otherwise the first selected
    // one. -1 when there is none.
    public static int CurrentIndex(IReadOnlyList<AlbumTileViewModel> tiles, AlbumTileViewModel? lastMovedTo)
    {
        int open = -1, selected = -1;
        for (var i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];
            if (ReferenceEquals(tile, lastMovedTo) && tile.IsSelected)
                return i;
            if (open < 0 && tile.IsExpanded)
                open = i;
            if (selected < 0 && tile.IsSelected)
                selected = i;
        }
        return open >= 0 ? open : selected;
    }
}
