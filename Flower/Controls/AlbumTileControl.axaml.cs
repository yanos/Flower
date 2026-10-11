using System;

using Avalonia.Controls;

using Flower.ViewModels;
using Flower.ViewModels.Mobile;

namespace Flower.Controls;

// One album in the desktop's Albums grid - see the markup.
public partial class AlbumTileControl : UserControl
{
    private readonly ModelWatch _tile;

    public AlbumTileControl()
    {
        InitializeComponent();
        // The tile's download spinner turns every frame; nothing here draws it.
        _tile = new ModelWatch(this, Show, ignoring: nameof(DownloadIndicatorViewModel.SpinAngle));
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _tile.Watch(DataContext as AlbumTileViewModel);
    }

    private void Show()
    {
        var tile = DataContext as AlbumTileViewModel;
        SelectionBorder.IsVisible = tile?.IsSelected == true;
        Caret.IsVisible = tile?.IsExpanded == true;
        TileContent.Tile = tile;
    }
}
