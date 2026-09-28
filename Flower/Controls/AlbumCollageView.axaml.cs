using System.Collections.Generic;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Flower.Controls;

// A cover made of several albums' - an artist's, a playlist's. See
// AlbumCollage for which albums, and AlbumCollageSurface for how they are
// laid out.
public partial class AlbumCollageView : UserControl
{
    public static readonly StyledProperty<IReadOnlyList<Bitmap>?> CoversProperty =
        AvaloniaProperty.Register<AlbumCollageView, IReadOnlyList<Bitmap>?>(nameof(Covers));

    public IReadOnlyList<Bitmap>? Covers
    {
        get => GetValue(CoversProperty);
        set => SetValue(CoversProperty, value);
    }

    public AlbumCollageView()
    {
        InitializeComponent();
    }
}

// Draws the covers into one square: one fills it, two split it along the
// diagonal from the top-right corner to the bottom-left one (the first above
// the line), and four take a quarter each, first to last left to right, top
// to bottom. A quarter is its cover cropped to fill it, the way AlbumArtView's
// UniformToFill does; a half is its cover laid over the whole square and cut
// along the line, so each shows its own half of the art rather than the whole
// of it squashed into a triangle.
public sealed class AlbumCollageSurface : Control
{
    public static readonly StyledProperty<IReadOnlyList<Bitmap>?> CoversProperty =
        AvaloniaProperty.Register<AlbumCollageSurface, IReadOnlyList<Bitmap>?>(nameof(Covers));

    public static readonly DirectProperty<AlbumCollageSurface, bool> HasArtProperty =
        AvaloniaProperty.RegisterDirect<AlbumCollageSurface, bool>(nameof(HasArt), o => o.HasArt);

    static AlbumCollageSurface()
    {
        AffectsRender<AlbumCollageSurface>(CoversProperty);
    }

    public IReadOnlyList<Bitmap>? Covers
    {
        get => GetValue(CoversProperty);
        set => SetValue(CoversProperty, value);
    }

    // Whether there is anything to draw: false leaves the frame's placeholder
    // showing.
    private bool _hasArt;
    public bool HasArt
    {
        get => _hasArt;
        private set => SetAndRaise(HasArtProperty, ref _hasArt, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CoversProperty)
            HasArt = Covers is { Count: > 0 };
    }

    public override void Render(DrawingContext context)
    {
        if (!HasArt || Covers is not { } covers)
            return;

        var bounds = new Rect(Bounds.Size);
        switch (covers.Count)
        {
            case 1:
                DrawCover(context, covers[0], bounds, bounds);
                break;
            case 2 or 3:
                DrawCover(context, covers[0], bounds, Triangle(bounds.TopLeft, bounds.TopRight, bounds.BottomLeft));
                DrawCover(context, covers[1], bounds, Triangle(bounds.TopRight, bounds.BottomRight, bounds.BottomLeft));
                break;
            default:
                var half = new Size(bounds.Width / 2, bounds.Height / 2);
                for (var i = 0; i < 4; i++)
                {
                    var quarter = new Rect(new Point(i % 2 * half.Width, i / 2 * half.Height), half);
                    DrawCover(context, covers[i], quarter, quarter);
                }
                break;
        }
    }

    // The cover cropped to fill area, painted only inside clip.
    private void DrawCover(DrawingContext context, Bitmap cover, Rect area, Rect clip) =>
        DrawCover(context, cover, area, new RectangleGeometry(clip));

    private void DrawCover(DrawingContext context, Bitmap cover, Rect area, Geometry clip)
    {
        using var _ = context.PushGeometryClip(clip);
        var source = new Rect(cover.Size);
        var scale = System.Math.Max(area.Width / source.Width, area.Height / source.Height);
        var cropped = new Size(area.Width / scale, area.Height / scale);
        var crop = new Rect(
            new Point((source.Width - cropped.Width) / 2, (source.Height - cropped.Height) / 2),
            cropped);
        context.DrawImage(cover, crop, area);
    }

    private static Geometry Triangle(Point a, Point b, Point c)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(a, isFilled: true);
            ctx.LineTo(b);
            ctx.LineTo(c);
            ctx.EndFigure(isClosed: true);
        }
        return geometry;
    }
}
