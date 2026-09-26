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
    // found among the albums and answered with the row it is in.
    // AlbumGridBuilder sorts on the raw name, hence no skipping punctuation.
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

        var index = AlphabetIndex.FirstIndexFor(tiles, t => AlphabetIndex.LetterOf(t.Tile.Name, skipPunctuation: false), letter);
        return index < 0 ? -1 : tiles[index].Row;
    }
}
