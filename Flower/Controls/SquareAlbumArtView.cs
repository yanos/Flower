using Avalonia;

namespace Flower.Controls;

/// <summary>
/// An <see cref="AlbumArtView"/> that always uses the width offered by its
/// parent as both dimensions. This lets a mobile grid column define one stable
/// square cover-art cell even when the embedded artwork itself is rectangular.
/// </summary>
public sealed class SquareAlbumArtView : AlbumArtView
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var side = double.IsFinite(availableSize.Width)
            ? availableSize.Width
            : double.IsFinite(availableSize.Height)
                ? availableSize.Height
                : 0;

        base.MeasureOverride(new Size(side, side));
        return new Size(side, side);
    }
}
