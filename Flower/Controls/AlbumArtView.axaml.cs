using System.Collections.Generic;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Flower.Controls;

public partial class AlbumArtView : UserControl
{
    public static readonly StyledProperty<Bitmap?> AlbumArtProperty =
        AvaloniaProperty.Register<AlbumArtView, Bitmap?>(nameof(AlbumArt));

    public static readonly StyledProperty<Stretch> StretchProperty =
        AvaloniaProperty.Register<AlbumArtView, Stretch>(nameof(Stretch), Stretch.UniformToFill);

    // The outline's corner radius. 4 everywhere a cover is one thing among
    // many on a row; the phone's album grid, where the covers are the page,
    // rounds them further.
    public static readonly StyledProperty<double> ArtCornerRadiusProperty =
        AvaloniaProperty.Register<AlbumArtView, double>(nameof(ArtCornerRadius), 4);

    public double ArtCornerRadius
    {
        get => GetValue(ArtCornerRadiusProperty);
        set => SetValue(ArtCornerRadiusProperty, value);
    }

    // Several covers instead of one, for what holds several albums: drawn as
    // a collage (AlbumCollageSurface) in place of AlbumArt. See AlbumCollage
    // for which albums.
    public static readonly StyledProperty<IReadOnlyList<Bitmap>?> CoversProperty =
        AvaloniaProperty.Register<AlbumArtView, IReadOnlyList<Bitmap>?>(nameof(Covers));

    public IReadOnlyList<Bitmap>? Covers
    {
        get => GetValue(CoversProperty);
        set => SetValue(CoversProperty, value);
    }

    public Bitmap? AlbumArt
    {
        get => GetValue(AlbumArtProperty);
        set => SetValue(AlbumArtProperty, value);
    }

    public Stretch Stretch
    {
        get => GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    public AlbumArtView()
    {
        InitializeComponent();
        Surface.PropertyChanged += (_, e) =>
        {
            if (e.Property == AlbumCollageSurface.HasArtProperty)
                UpdateLayers();
        };
        UpdateLayers();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == AlbumArtProperty || change.Property == CoversProperty)
        {
            UpdateLayers();
            return;
        }

        if (change.Property != ArtCornerRadiusProperty)
            return;

        var radius = new CornerRadius(ArtCornerRadius);
        ClipBorder.CornerRadius = radius;
        Outline.CornerRadius = radius;
    }

    // The collage if it has any art loaded, else the single cover, else the
    // note - one of the three, never two stacked.
    private void UpdateLayers()
    {
        var collage = Surface.HasArt;
        var single = !collage && AlbumArt != null;
        Surface.IsVisible = collage;
        ArtCanvas.IsVisible = single;
        Placeholder.IsVisible = !collage && !single;
    }
}
