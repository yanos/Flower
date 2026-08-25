using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.Extensions.Logging;

namespace Flower.Services;

// Raw art bytes plus the MIME type they should be served as. The type is
// carried out of the lookup rather than sniffed back out of the bytes later:
// an embedded picture states its own MIME type in the tag, and a cover file
// states it in its extension, so re-deriving it downstream only ever loses
// information (the app's own listener used to sniff PNG-vs-JPEG magic bytes and
// call everything else JPEG).
public sealed record LocalAlbumArt(byte[] Bytes, string MimeType);

// The one implementation of "what is this album's art file, on this disk":
// the embedded tag picture first, then a cover.*/folder.* image beside the
// track. This existed three times - AlbumArtLoader (Bitmap decoding and the
// CoverArt hash), the app listener's own cover-art handler, and a private copy
// in Flower.Server's SubsonicEndpoints - which is three places to edit when
// someone adds a format, and they had already drifted: the server's copy
// accepted only .jpg/.jpeg/.png as a cover file, so an album with a
// cover.webp served art in the app and 404'd from a self-hosted server for
// the same library (ARCHITECTURE-REVIEW Tier 2.2).
//
// Lives in Flower.Core rather than Flower because Flower is Avalonia-coupled
// and out of reach of the server (SYNC-PLAN.md's "Reuse boundary" note) -
// which was the original reason for the copy. Nothing here needs Avalonia;
// only the Bitmap decoding on top of it does, and that stays in
// AlbumArtLoader.
public static class LocalAlbumArtReader
{
    // Extensions accepted for a cover.*/folder.* file, with what to serve
    // each as. Anything Skia can decode belongs here - the client side always
    // accepted this whole set.
    private static readonly Dictionary<string, string> ImageMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".gif"] = "image/gif",
        [".tiff"] = "image/tiff",
        [".tif"] = "image/tiff",
    };

    // logger is optional because two of the three callers are static classes
    // with a logger of their own to hand and the third (a Minimal-API handler)
    // has none; a null one just means the best-effort failures below stay
    // silent, exactly as they were before.
    public static LocalAlbumArt? ForFile(string? path, ILogger? logger = null)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        // 1. Embedded tag art.
        try
        {
            using var tagFile = TagLib.File.Create(path);
            var pic = tagFile.Tag.Pictures.FirstOrDefault();
            if (pic?.Data?.Data is { Length: > 0 } data)
                return new LocalAlbumArt(data, string.IsNullOrEmpty(pic.MimeType) ? "image/jpeg" : pic.MimeType);
        }
        catch (Exception ex)
        {
            // Debug, not Warning - TagLib failing to open a file's tags
            // entirely is routine for oddball/corrupt files scattered through
            // a large real library, not something worth a warning for each.
            logger?.LogTrace(ex, "Could not read embedded art tag for {Path}", path);
        }

        // 2. cover.*/folder.* in the same directory.
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (directory != null)
            {
                var file = Directory.EnumerateFiles(directory).FirstOrDefault(f =>
                {
                    var stem = Path.GetFileNameWithoutExtension(f);
                    return (stem.Equals("cover", StringComparison.OrdinalIgnoreCase) ||
                            stem.Equals("folder", StringComparison.OrdinalIgnoreCase))
                        && ImageMimeTypes.ContainsKey(Path.GetExtension(f));
                });
                if (file != null)
                    return new LocalAlbumArt(File.ReadAllBytes(file), ImageMimeTypes[Path.GetExtension(file)]);
            }
        }
        catch (Exception ex)
        {
            logger?.LogTrace(ex, "Could not read a cover/folder image next to {Path}", path);
        }

        return null;
    }
}
