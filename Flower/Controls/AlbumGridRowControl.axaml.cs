using Avalonia.Controls;

namespace Flower.Controls;

// One row of tiles in AlbumGridView's grid, with the album opened in it below
// them (AlbumExpansionCard) - see AlbumGridRowViewModel. Instances are
// recycled by UniformRowPanel as the user scrolls.
public partial class AlbumGridRowControl : UserControl
{
    public AlbumGridRowControl()
    {
        InitializeComponent();
    }

    // Where the album card will end once it has finished opening, measured
    // from the top of this row - AlbumGridView scrolls to what the row will
    // be, not what it is mid-animation (RevealExpanded).
    public double ExpandedBottom => Card.Bounds.Top + Card.ExpandedBottom;

    // How tall the card is drawn right now, mid-animation included - what a
    // row closing above another takes away from everything under it.
    public double CurrentExpansionHeight => Card.CurrentHeight;
}
