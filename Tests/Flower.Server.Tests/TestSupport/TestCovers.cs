using SkiaSharp;

namespace Flower.Server.Tests.TestSupport;

// Real pictures, for the tests that need the server to decode and shrink one
// (CoverArtThumbnail). Flat colour is enough: what is checked is the size
// that comes back, never what it looks like.
internal static class TestCovers
{
    public static byte[] Jpeg(int width, int height) => Encode(width, height, SKEncodedImageFormat.Jpeg);

    public static byte[] Png(int width, int height) => Encode(width, height, SKEncodedImageFormat.Png);

    private static byte[] Encode(int width, int height, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        bitmap.Erase(new SKColor(0x40, 0x80, 0xC0));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    public static SKSizeI SizeOf(byte[] bytes)
    {
        using var codec = SKCodec.Create(SKData.CreateCopy(bytes)) ?? throw new InvalidOperationException("not a picture");
        return codec.Info.Size;
    }
}
