using Avalonia.Controls;

using Flower.Controls;

namespace Flower.Views;

public partial class HomeView : UserControl
{
    // The Albums grid's cell: a 180 tile and its 8 either side.
    private const double CellWidth = 180 + 16;

    private readonly HomeShelfRowsHost _rows;

    public HomeView()
    {
        InitializeComponent();
        _rows = new HomeShelfRowsHost(Rows, width => HomeShelfRowsHost.DesktopColumnsFor(width, CellWidth));
    }
}
