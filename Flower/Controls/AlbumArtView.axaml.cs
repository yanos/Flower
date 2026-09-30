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
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != ArtCornerRadiusProperty)
            return;

        var radius = ArtCornerRadius;
        Frame.CornerRadius = new CornerRadius(radius);
        ClipBorder.CornerRadius = new CornerRadius(System.Math.Max(0, radius - 1));
    }
}
