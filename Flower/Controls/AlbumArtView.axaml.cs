using System;
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

    // Every cover's corner radius, and the one place it is written: large or
    // small, phone or desktop, a cover rounds this much. The Borders that
    // cast a cover's shadow or take its tap round with it through
    // CoverCornerRadius. Only a picture meant to be round - an artist, the
    // mini player's cover - sets ArtCornerRadius, to half its width.
    public const double CoverRadius = 6;

    public static readonly CornerRadius CoverCornerRadius = new(CoverRadius);

    public static readonly StyledProperty<double> ArtCornerRadiusProperty =
        AvaloniaProperty.Register<AlbumArtView, double>(nameof(ArtCornerRadius), CoverRadius);

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
        ClipBorder.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
                UpdateStretch();
        };
        UpdateLayers();
        UpdateStretch();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == AlbumArtProperty || change.Property == StretchProperty)
            UpdateStretch();
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

    // How far a cover may be zoomed past its own edges to fill the box: a
    // cover a little off square loses a sliver rather than gaining two thin
    // bars, while one much further off - a wide CD booklet scan, a portrait
    // photo - is shown whole rather than have a band of it cut away.
    private const double MaxFillZoom = 1.10;

    private void UpdateStretch() =>
        ArtImage.Stretch = StretchFor(Stretch, AlbumArt?.PixelSize, ClipBorder.Bounds.Size);

    // UniformToFill becomes Uniform when filling the box would mean zooming
    // in by more than MaxFillZoom; every other stretch is left as asked. A
    // box not yet laid out is taken to be square, as every cover cell is.
    private static Stretch StretchFor(Stretch requested, PixelSize? art, Size box)
    {
        if (requested != Stretch.UniformToFill || art is not { Width: > 0, Height: > 0 } pixels)
            return requested;

        var artAspect = (double)pixels.Width / pixels.Height;
        var boxAspect = box.Width > 0 && box.Height > 0 ? box.Width / box.Height : 1;
        var zoom = Math.Max(artAspect / boxAspect, boxAspect / artAspect);
        return zoom <= MaxFillZoom ? Stretch.UniformToFill : Stretch.Uniform;
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
