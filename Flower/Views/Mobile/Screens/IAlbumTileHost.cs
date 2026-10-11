using System.Windows.Input;

namespace Flower.Views.Mobile.Screens;

// Implemented by each phone screen that shows album covers (AlbumTileView) -
// the Albums grid, an artist's albums, Home - to say what tapping one does,
// which is the one thing about a cover that differs between them: each opens
// the album its own way, so Back returns to the screen it was opened from.
// Run with the cover's AlbumTileViewModel. A Continue Playing cover is not
// asked: it picks its album up where it was left.
public interface IAlbumTileHost
{
    ICommand? OpenAlbumCommand { get; }
}
