using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;

using SkiaSharp;

using Flower.Services;
using Flower.Tests.TestSupport;

namespace Flower.Tests.MusicLibrary.Art;

// A cover's size read from its header, so the art loader and Track Info decode
// it once at the size they want rather than once more at full size to find out
// (docs/CODE-REVIEW-2026-09.md D1). The pictures are real encoder output, from
// Skia - the library that decodes them in the app - so the headers are the
// shapes a decoder will actually be handed. A wrong answer is worse than none:
// none falls back to the old decode, a wrong one draws the cover at the wrong
// size.
public class ImageHeaderTests
{
    private static byte[] Encoded(SKEncodedImageFormat format, int width, int height, int quality = 90)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(new SKColor(0xC0, 0x40, 0x80));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality);
        return data.ToArray();
    }

    [Theory]
    [InlineData(1400, 1400)]
    [InlineData(600, 450)]
    public void Reads_a_png(int width, int height)
    {
        Assert.Equal((width, height), ImageHeader.TryReadSize(SyntheticPng.Build(width, height)));
    }

    [Theory]
    [InlineData(1400, 1400)]
    [InlineData(500, 333)]
    public void Reads_a_jpeg(int width, int height)
    {
        Assert.Equal((width, height), ImageHeader.TryReadSize(Encoded(SKEncodedImageFormat.Jpeg, width, height)));
    }

    // An Exif block, thumbnail and all, routinely sits ahead of the frame and
    // runs to tens of kilobytes - the walk has to skip it rather than give up.
    [Fact]
    public void Reads_a_jpeg_whose_frame_comes_after_a_large_exif_block()
    {
        var jpeg = Encoded(SKEncodedImageFormat.Jpeg, 640, 480);
        var exif = new byte[60_000];
        exif[0] = 0xFF;
        exif[1] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(exif.AsSpan(2), (ushort)(exif.Length - 2));

        var withExif = jpeg[..2].Concat(exif).Concat(jpeg[2..]).ToArray();

        Assert.Equal((640, 480), ImageHeader.TryReadSize(withExif));
    }

    // Lossy and lossless WebP keep the size in different chunks (VP8, VP8L),
    // so each is asked for by name.
    [Theory]
    [InlineData(SKWebpEncoderCompression.Lossy, "VP8 ")]
    [InlineData(SKWebpEncoderCompression.Lossless, "VP8L")]
    public void Reads_a_webp(SKWebpEncoderCompression compression, string chunk)
    {
        using var bitmap = new SKBitmap(300, 200);
        bitmap.Erase(new SKColor(0xC0, 0x40, 0x80));
        using var pixels = bitmap.PeekPixels();
        using var data = pixels.Encode(new SKWebpEncoderOptions(compression, 90));
        var webp = data.ToArray();

        Assert.Equal(chunk, System.Text.Encoding.ASCII.GetString(webp, 12, 4));
        Assert.Equal((300, 200), ImageHeader.TryReadSize(webp));
    }

    // Skia decodes GIF and BMP but encodes neither, so these two are built by
    // hand - and held to what Skia reads back from them, which is the size a
    // cover file of either kind is drawn at.
    [Fact]
    public void Reads_a_gif()
    {
        // A 1x1 frame on a 320x240 logical screen, which is the picture's size.
        byte[] gif =
        [
            .. "GIF89a"u8, 0x40, 0x01, 0xF0, 0x00, 0x80, 0x00, 0x00,
            0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF,
            0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
            0x02, 0x02, 0x44, 0x01, 0x00,
            0x3B,
        ];

        Assert.Equal((320, 240), SkiaSize(gif));
        Assert.Equal((320, 240), ImageHeader.TryReadSize(gif));
    }

    // A negative height is a picture stored top row first, not a negative one.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reads_a_bmp(bool topDown)
    {
        const int width = 7, height = 3;
        var stride = (width * 3 + 3) & ~3;
        var bmp = new byte[54 + stride * height];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), topDown ? -height : height);
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(28), 24);

        Assert.Equal((width, height), SkiaSize(bmp));
        Assert.Equal((width, height), ImageHeader.TryReadSize(bmp));
    }

    private static (int, int) SkiaSize(byte[] bytes)
    {
        using var bitmap = SKBitmap.Decode(bytes);
        return (bitmap.Width, bitmap.Height);
    }

    [Fact]
    public void Leaves_the_stream_where_it_was_so_the_decode_can_follow()
    {
        using var stream = new MemoryStream(Encoded(SKEncodedImageFormat.Jpeg, 64, 64));

        ImageHeader.TryReadSize(stream);

        Assert.Equal(0, stream.Position);
    }

    // Truncated, foreign, or empty: no answer, and the caller decodes.
    [Fact]
    public void Answers_nothing_rather_than_something_wrong()
    {
        var jpeg = Encoded(SKEncodedImageFormat.Jpeg, 64, 64);

        Assert.Null(ImageHeader.TryReadSize(jpeg[..20]));
        Assert.Null(ImageHeader.TryReadSize(SyntheticPng.Build(64, 64)[..20]));
        Assert.Null(ImageHeader.TryReadSize("GIF89a"u8.ToArray()));
        Assert.Null(ImageHeader.TryReadSize("II*\0 a TIFF, not handled here"u8.ToArray()));
        Assert.Null(ImageHeader.TryReadSize([]));
    }
}
