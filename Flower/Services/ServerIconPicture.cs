using System;
using System.IO;

using Avalonia;
using Avalonia.Media.Imaging;

namespace Flower.Services;

// A picture chosen as a server's icon (ServerIcon), made into what is sent:
// the middle square of it - the sidebar draws a square - no larger than
// ServerIcon.ImagePixels across, as PNG. Done on the device that chose it,
// which has a decoder to hand; the server has none and only sniffs.
public static class ServerIconPicture
{
    // Null for anything that does not decode as a picture.
    public static byte[]? Prepare(byte[] bytes)
    {
        using var source = Decode(bytes);
        if (source == null)
            return null;

        var side = Math.Min(source.Size.Width, source.Size.Height);
        var crop = new Rect((source.Size.Width - side) / 2, (source.Size.Height - side) / 2, side, side);
        var pixels = Math.Max(1, Math.Min(ServerIcon.ImagePixels, (int)Math.Round(side)));

        using var target = new RenderTargetBitmap(new PixelSize(pixels, pixels));
        using (var context = target.CreateDrawingContext())
            context.DrawImage(source, crop, new Rect(0, 0, pixels, pixels));

        using var png = new MemoryStream();
        target.Save(png);
        return png.ToArray();
    }

    public static Bitmap? Decode(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 })
            return null;

        try
        {
            using var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
