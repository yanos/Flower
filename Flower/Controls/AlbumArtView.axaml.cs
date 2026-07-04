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
}
