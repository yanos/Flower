using System.Collections.Generic;

using Avalonia.Controls;

using Flower.Services;
using Flower.ViewModels.Mobile;

namespace Flower.Views.Mobile.Screens;

public partial class AlbumGridScreenView : UserControl
{
    public AlbumGridScreenView()
    {
        InitializeComponent();
        IndexBar.IndexOfLetter = RowOfLetter;
    }

    // The bar scrolls rows, and a row holds several albums, so the letter is
    // found among the albums and answered with the row it is in - by name or
    // by artist, whichever the grid is sorted on. Both are sorted on their
    // TrackListBuilder.SortKey, which drops punctuation, so the letter does too.
    private int RowOfLetter(char letter)
    {
        if (DataContext is not MobileMainViewModel vm)
            return -1;

        var tiles = new List<(AlbumTileViewModel Tile, int Row)>();
        for (var row = 0; row < vm.AlbumGridRows.Count; row++)
        {
            foreach (var tile in vm.AlbumGridRows[row].Tiles)
                tiles.Add((tile, row));
        }

        var index = AlphabetIndex.FirstIndexFor(tiles, t => AlphabetIndex.LetterOf(vm.AlbumGridLetterText(t.Tile), skipPunctuation: true), letter,
            descending: vm.AlbumGridRunsZToA);
        return index < 0 ? -1 : tiles[index].Row;
    }
}
