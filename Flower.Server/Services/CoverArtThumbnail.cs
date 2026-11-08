using Flower.Services;

using SkiaSharp;

namespace Flower.Server.Services;

// A cover shrunk to CoverArtBatch.ThumbnailPixels wide, for a song list's
// rows (docs/CITED-DECISIONS.md, October C2). Computed once per cover and kept
// beside the original in CoverArtCache.
//
// Shrunk by width, keeping the aspect, because that is how a client decodes
// a thumbnail (AlbumArtLoader.DecodeThumbnail): the same picture either way,
// fewer bytes on the way. Art that is already no wider is handed back as it
// is, and so is anything Skia cannot read - the client's own decoder may
// manage it, and the full cover is what it would have got before there were
// thumbnails.
public static class CoverArtThumbnail
{
    private const int JpegQuality = 85;

    public static LocalAlbumArt Of(LocalAlbumArt art, int width, ILogger logger)
    {
        try
        {
            return Shrink(art, width) ?? art;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not shrink a {ByteCount}-byte cover; sending it at full size", art.Bytes.Length);
            return art;
        }
    }

    private static LocalAlbumArt? Shrink(LocalAlbumArt art, int width)
    {
        using var data = SKData.CreateCopy(art.Bytes);
        using var codec = SKCodec.Create(data);
        if (codec == null)
            return null;

        var info = codec.Info;
        if (info.Width <= width || info.Height <= 0)
            return null;

        var height = Math.Max(1, (int)Math.Round((double)info.Height * width / info.Width));

        // A JPEG decoder can do most of the shrinking inside the decode, in
        // eighths, which is far cheaper than decoding 1400px to throw most of
        // it away. Only a scale that still leaves at least the width wanted,
        // so the last step is always down.
        var decodeSize = codec.GetScaledDimensions((float)width / info.Width);
        if (decodeSize.Width < width || decodeSize.Height < height)
            decodeSize = info.Size;

        var decodeInfo = new SKImageInfo(decodeSize.Width, decodeSize.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var decoded = new SKBitmap(decodeInfo);
        var result = codec.GetPixels(decodeInfo, decoded.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            return null;

        using var shrunk = decoded.Resize(
            new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul),
            new SKSamplingOptions(SKCubicResampler.Mitchell));
        if (shrunk == null)
            return null;

        // JPEG unless the cover has transparency to keep: a JPEG has none, and
        // what it would put behind it is black.
        var opaque = info.AlphaType == SKAlphaType.Opaque;
        using var image = SKImage.FromBitmap(shrunk);
        using var encoded = opaque
            ? image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality)
            : image.Encode(SKEncodedImageFormat.Png, 100);
        if (encoded == null)
            return null;

        return new LocalAlbumArt(encoded.ToArray(), opaque ? "image/jpeg" : "image/png");
    }
}
