using System;
using System.Runtime.InteropServices;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;

using Flower.Controls;

using Xunit;

namespace Flower.Tests;

// The header band's blur (ScreenSlot) was a blurred VisualBrush copy of the
// screen, and the copy was taken when the screen opened: scrolled, the band
// went on blurring a cover that was no longer under it. BackdropBlur blurs
// whatever is under it in the frame being drawn, which is what these hold it
// to - blurring at all, and blurring what the list has since scrolled to.
public class BackdropBlurTests
{
    private const int Width = 200;
    private const int Height = 300;
    private const int BlurHeight = 100;

    [AvaloniaFact]
    public void It_blurs_what_is_under_it_and_follows_a_scroll()
    {
        // 4px black and white stripes, then solid red.
        var content = new StackPanel();
        for (var i = 0; i < 100; i++)
            content.Children.Add(new Border { Height = 4, Background = i % 2 == 0 ? Brushes.Black : Brushes.White });
        content.Children.Add(new Border { Height = 600, Background = Brushes.Red });
        // Moved by a transform rather than a ScrollViewer, which has no
        // template without a theme; to what is drawn, it is the same move.
        var moved = new TranslateTransform();
        content.RenderTransform = moved;

        var window = new Window
        {
            Width = Width,
            Height = Height,
            Content = new Panel
            {
                Children =
                {
                    content,
                    new BackdropBlur { VerticalAlignment = VerticalAlignment.Top, Height = BlurHeight, Radius = 18 },
                },
            },
        };
        window.Show();

        // Under the blur the stripes are one grey; below it they are still
        // stripes, so it is the blur doing that and nothing else.
        var (stripes, below) = Sample(window);
        Assert.InRange(stripes.R, 60, 195);
        Assert.True(Math.Abs(stripes.R - stripes.G) < 8 && Math.Abs(stripes.G - stripes.B) < 8, $"grey expected, got {stripes}");
        Assert.True(below.R is < 20 or > 235, $"below the blur should be unblurred, got {below}");

        // Scrolled to the red: the blur is red now, not the stripes it opened on.
        moved.Y = -400;
        var (red, _) = Sample(window);
        Assert.True(red.R > 200 && red.G < 40 && red.B < 40, $"red expected under the blur after scrolling, got {red}");

        window.Close();
    }

    private static ((int R, int G, int B) UnderBlur, (int R, int G, int B) BelowBlur) Sample(Window window)
    {
        using var frame = window.CaptureRenderedFrame()!;
        using var pixels = frame.Lock();

        (int R, int G, int B) At(int x, int y)
        {
            var bytes = new byte[4];
            Marshal.Copy(pixels.Address + y * pixels.RowBytes + x * 4, bytes, 0, 4);
            return pixels.Format == Avalonia.Platform.PixelFormat.Rgba8888
                ? (bytes[0], bytes[1], bytes[2])
                : (bytes[2], bytes[1], bytes[0]);
        }

        return (At(Width / 2, BlurHeight / 2), At(Width / 2, BlurHeight + 41));
    }
}
