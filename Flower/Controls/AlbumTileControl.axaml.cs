using Avalonia.Controls;

namespace Flower.Controls;

// One tile in AlbumGridPanel - DataContext is always an AlbumTileViewModel,
// set by AlbumGridPanel when it creates/recycles this control (same pattern
// as MusicListPanel/TrackRowControl). Purely visual - see AlbumGridPanel/
// AlbumGridView/MainView.axaml.cs for click/selection/drag handling.
public partial class AlbumTileControl : UserControl
{
    public AlbumTileControl()
    {
        InitializeComponent();
    }
}
