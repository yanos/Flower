using Avalonia;
using Avalonia.Headless.XUnit;

using Flower.Controls;

using Xunit;

namespace Flower.Tests;

public sealed class SquareAlbumArtViewTests
{
    [AvaloniaFact]
    public void It_uses_the_available_width_for_both_dimensions()
    {
        var art = new SquareAlbumArtView();

        art.Measure(new Size(180, double.PositiveInfinity));

        Assert.Equal(new Size(180, 180), art.DesiredSize);
    }
}
