using System;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

using Flower.Controls;
using Flower.ViewModels;
using Flower.ViewModels.Mobile;

namespace Flower.Views.Mobile.Screens;

// One album cover on the phone - see the markup. Its item is an
// AlbumTileViewModel, or a ContinuePlayingItemViewModel around one; the cover
// itself follows the album (AlbumCard), and this follows a Continue Playing
// item, whose place in the album moves as it plays.
public partial class AlbumTileView : Button
{
    protected override Type StyleKeyOverride => typeof(Button);

    private readonly ModelWatch _item;
    private IAlbumTileHost? _host;

    public AlbumTileView()
    {
        InitializeComponent();
        _item = new ModelWatch(this, Show);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _item.Watch(DataContext as ContinuePlayingItemViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _host = null;
    }

    private void Show()
    {
        switch (DataContext)
        {
            case ContinuePlayingItemViewModel item:
                Command = item.Home.ResumeCommand;
                CommandParameter = item;
                Show(item.Tile, item.Progress, item.Detail);
                break;
            case AlbumTileViewModel tile:
                Command = Host?.OpenAlbumCommand;
                CommandParameter = tile;
                Show(tile, null, null);
                break;
        }
    }

    private void Show(AlbumTileViewModel tile, double? progress, string? detail)
    {
        LongPress.SetCommandParameter(this, tile);
        Card.Tile = tile;
        Card.ShowsProgress = progress != null;
        Card.Progress = progress ?? 0;
        Card.Detail = detail;
    }

    // Found once it is on screen, and kept: a cover stays in the row it was
    // built for.
    private IAlbumTileHost? Host =>
        _host ??= this.IsAttachedToVisualTree() ? this.GetVisualAncestors().OfType<IAlbumTileHost>().FirstOrDefault() : null;
}
