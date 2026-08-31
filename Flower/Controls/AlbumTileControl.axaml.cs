using System.Linq;

using Avalonia.Controls;

using Flower.ViewModels;
using Flower.ViewModels.Mobile;

namespace Flower.Controls;

// One tile in AlbumGridPanel - DataContext is always an AlbumTileViewModel,
// set by AlbumGridPanel when it creates/recycles this control (same pattern
// as MusicListPanel/TrackRowControl). Selection, expansion, drag and the
// context menu are all handled above it - see AlbumGridPanel/AlbumGridView/
// MainView.axaml.cs - so the only pointer handling here is the download
// button's own click, which is deliberately swallowed rather than left to
// bubble into any of those.
public partial class AlbumTileControl : UserControl
{
    public AlbumTileControl()
    {
        InitializeComponent();
    }

    // The tile's download icon - one click for the whole album, which is what
    // an album-level icon can only sensibly mean. Reaches the view-model the
    // same way the grid's other tile-level actions do (see AlbumGridRowControl),
    // since a tile is created by a panel, not bound to a command.
    private void Download_Requested(object? sender, DownloadIndicatorViewModel indicator)
    {
        if (indicator is AlbumTileViewModel tile && this.FindDataContext<MainViewModel>() is { } vm)
            _ = vm.Downloads.DownloadAlbumAsync(tile, tile.Tracks.Where(vm.Availability.IsDownloadable).ToList());
    }
}
