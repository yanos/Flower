using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Flower.Services;

// What a cover looks like from far away: a tiny copy of it, and the colour
// it is mostly made of. The album page draws the tiny copy stretched back up
// behind its header, which a smooth upscale turns into a soft blur for
// nothing - a live blur effect would be redrawn every frame the page scrolls,
// on a phone. Now Playing washes its whole sheet in the colour, darkened far
// enough that white reads on it.
//
// Both are worked out once per bitmap and remembered for as long as the
// bitmap lives: AlbumArtLoader hands the same instance to everything showing
// one album, so this is once per album.
public static class ArtBackdrop
{
    // Small enough that fine detail does not survive the stretch back up,
    // large enough that the cover's shapes do - a flower still reads as a
    // flower, softened, rather than as a blot of red where it was.
    private const int TinySide = 32;

    private static readonly ConditionalWeakTable<Bitmap, Bitmap> Tinies = new();
    private static readonly ConditionalWeakTable<Bitmap, object> Tints = new();
    private static readonly ConditionalWeakTable<Bitmap, object> TopEdges = new();

    // What Now Playing falls back to with no cover to take a colour from: the
    // app's own brick red, darkened the way a cover's colour is.
    public static readonly Color FallbackTint = Color.FromRgb(0x7A, 0x3A, 0x33);

    public static Bitmap? Tiny(Bitmap? source)
    {
        if (source is null)
            return null;

        try
        {
            return Tinies.GetValue(source, s =>
                s.CreateScaledBitmap(new PixelSize(TinySide, TinySide), BitmapInterpolationMode.HighQuality));
        }
        catch (Exception)
        {
            // A bitmap that cannot be scaled (disposed under us, a format
            // Skia will not resample) just has no backdrop.
            return null;
        }
    }

    // The cover's most common colourful colour, rather than its average: the
    // average of red flowers on cream is a muddy beige no one would pick. The
    // pixels are sorted into coarse buckets, each counted by how colourful it
    // is, so a large pale background does not outvote a smaller vivid subject;
    // the winning bucket's pixels are averaged. A cover with no colour at all
    // (black and white) comes back grey, which is what it is.
    public static Color DominantColor(Bitmap? source)
    {
        if (source is null)
            return FallbackTint;

        if (Tints.TryGetValue(source, out var known))
            return (Color)known;

        var tint = Measure(source) ?? FallbackTint;
        Tints.AddOrUpdate(source, tint);
        return tint;
    }

    // The colour along the top of the tiny copy, where the album page's
    // backdrop starts: what the page is filled with above it when it is
    // pulled down past its top, so the backdrop seems to carry on rather than
    // ending at a hard edge over plain background. The middle of the row
    // only - the backdrop is taller than it is wide, so the cover is drawn
    // cropped at both sides and the ends of the row are not on screen.
    public static Color? TopEdgeColor(Bitmap? source)
    {
        if (source is null)
            return null;

        if (TopEdges.TryGetValue(source, out var known))
            return (Color)known;

        if (Pixels(source) is not { } read)
            return null;

        var (pixels, width, rgba) = read;

        double r = 0, g = 0, b = 0;
        var from = width / 5;
        var to = width - from;
        for (var x = from; x < to; x++)
        {
            var i = x * 4;
            r += rgba ? pixels[i] : pixels[i + 2];
            g += pixels[i + 1];
            b += rgba ? pixels[i + 2] : pixels[i];
        }
        var n = to - from;
        var edge = Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
        TopEdges.AddOrUpdate(source, edge);
        return edge;
    }

    // The tiny copy's pixels, top row first, four bytes each, and whether
    // they are RGBA (rather than BGRA) - whichever the platform's decoder
    // produced.
    private static (byte[] Pixels, int Width, bool Rgba)? Pixels(Bitmap source)
    {
        if (Tiny(source) is not { } tiny)
            return null;

        var width = tiny.PixelSize.Width;
        var height = tiny.PixelSize.Height;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            tiny.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), pixels.Length, stride);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            handle.Free();
        }

        return (pixels, width, tiny.Format == PixelFormats.Rgba8888);
    }

    private static Color? Measure(Bitmap source)
    {
        if (Pixels(source) is not { } read)
            return null;

        var (pixels, _, rgba) = read;

        // 4 levels a channel: 64 buckets, coarse enough that one flower's
        // reds land together.
        var weight = new double[64];
        var sumR = new double[64];
        var sumG = new double[64];
        var sumB = new double[64];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var r = rgba ? pixels[i] : pixels[i + 2];
            var g = pixels[i + 1];
            var b = rgba ? pixels[i + 2] : pixels[i];
            var a = pixels[i + 3];
            if (a < 128)
                continue;

            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            var saturation = max == 0 ? 0 : (max - min) / (double)max;
            // Every pixel counts a little, so a grey cover still has an answer,
            // but colour counts far more, and more than in proportion: red
            // flowers on a cream ground are a red cover, though the cream has
            // the most pixels. Squared, a vivid pixel outweighs a pale one
            // several times over rather than twice.
            var w = 0.05 + 4 * saturation * saturation * (max / 255.0);

            var bucket = (r >> 6) * 16 + (g >> 6) * 4 + (b >> 6);
            weight[bucket] += w;
            sumR[bucket] += r * w;
            sumG[bucket] += g * w;
            sumB[bucket] += b * w;
        }

        var best = -1;
        for (var i = 0; i < 64; i++)
        {
            if (weight[i] > 0 && (best < 0 || weight[i] > weight[best]))
                best = i;
        }
        if (best < 0)
            return null;

        return Color.FromRgb(
            (byte)(sumR[best] / weight[best]),
            (byte)(sumG[best] / weight[best]),
            (byte)(sumB[best] / weight[best]));
    }

    // Now Playing's background: the cover's colour pulled into a band dark
    // enough for white text, lit from the top left - lighter there, deepening
    // to the bottom right, the way the design's mockup is.
    //
    // The hue is the cover's own, untouched, and so is the lack of one: a
    // black-and-white cover gets a grey sheet. There used to be a floor on
    // saturation, which gave a grey cover the hue grey nominally has - red -
    // and turned it mauve; and a nudge toward orange that suited the mockup's
    // rust and nothing else, which turned a pink cover into a dull raspberry.
    // Saturation is only scaled down a fifth (and capped), so a vivid cover
    // stays recognisably its colour without the sheet shouting.
    public static IBrush SheetBrush(Color dominant)
    {
        var hsl = dominant.ToHsl();
        var saturation = Math.Min(hsl.S * 0.8, 0.55);
        var lightness = Math.Clamp(hsl.L, 0.30, 0.40);

        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(new HslColor(1, hsl.H, saturation * 0.9, lightness + 0.16).ToRgb(), 0),
                new GradientStop(new HslColor(1, hsl.H, saturation, lightness + 0.05).ToRgb(), 0.35),
                new GradientStop(new HslColor(1, hsl.H, saturation, lightness).ToRgb(), 0.6),
                new GradientStop(new HslColor(1, hsl.H, saturation, lightness - 0.10).ToRgb(), 1),
            },
        };
    }
}

// Cover -> the tiny copy the album page stretches behind its header.
public sealed class TinyArtConverter : IValueConverter
{
    public static readonly TinyArtConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ArtBackdrop.Tiny(value as Bitmap);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

// Cover -> the colour the album page's backdrop carries on in above its top.
public sealed class TopEdgeBrushConverter : IValueConverter
{
    public static readonly TopEdgeBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ArtBackdrop.TopEdgeColor(value as Bitmap) is { } edge ? new SolidColorBrush(edge) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

// Cover -> Now Playing's tinted background.
public sealed class ArtSheetBrushConverter : IValueConverter
{
    public static readonly ArtSheetBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ArtBackdrop.SheetBrush(ArtBackdrop.DominantColor(value as Bitmap));

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
