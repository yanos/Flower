using System;
using System.IO;
using System.Threading.Tasks;

using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;

namespace Flower.Services;

// A cover pasted from the clipboard, for Track Info's Paste Cover Art on the
// phone and Paste on the desktop's Artwork tab: whatever image the clipboard
// holds - a screenshot, an image copied from a browser or Photos - as PNG
// bytes, which is what AlbumArtEditor writes into the tag. Null when there is
// no image there, or the platform would not hand it over. The desktop's drop
// targets use EncodePng for an image dragged in as a picture, not a file.
public static class ClipboardArt
{
    public const string MimeType = "image/png";

    public static async Task<byte[]?> TryReadPngAsync(IClipboard? clipboard)
    {
        if (clipboard is null)
            return null;

        try
        {
            using var bitmap = await clipboard.TryGetBitmapAsync();
            return EncodePng(bitmap);
        }
        catch (Exception)
        {
            // A clipboard the platform cannot read is the same to the user as
            // no image at all.
            return null;
        }
    }

    public static byte[]? EncodePng(Bitmap? bitmap)
    {
        if (bitmap is null)
            return null;

        try
        {
            using var png = new MemoryStream();
            bitmap.Save(png, PngBitmapEncoderOptions.Default);
            return png.ToArray();
        }
        catch (Exception)
        {
            // An image Skia cannot encode is no image either.
            return null;
        }
    }
}
