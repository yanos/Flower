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

    // What an image file with this extension should be tagged/served as, or
    // null for an extension nothing here can decode. Exposed so the write side
    // (Track Info's Artwork tab, embedding a picked file into a track's tag)
    // accepts exactly the set the read side above already understands, rather
    // than keeping its own second list of image extensions.
    public static string? MimeTypeForExtension(string? extension) =>
        extension != null && ImageMimeTypes.TryGetValue(extension, out var mime) ? mime : null;

    // The extensions a file picker should offer, for the same reason.
    public static IReadOnlyCollection<string> ImageExtensions => ImageMimeTypes.Keys;

    // The file extension to give a copy of art carrying this MIME type - the
    // reverse of the table above, for the one caller that has bytes plus a type
    // and needs a filename to put them in (dragging the artwork out of Track
    // Info onto the desktop). ".jpg" for anything unrecognised: a wrong-but-
    // plausible extension on a dragged-out copy is a far smaller problem than
    // an extensionless file the OS cannot open at all.
    public static string ExtensionForMimeType(string? mimeType)
    {
        foreach (var (extension, mime) in ImageMimeTypes)
        {
            if (string.Equals(mime, mimeType, StringComparison.OrdinalIgnoreCase))
                return extension;
        }

        return ".jpg";
    }

    // What these bytes actually are, by magic number. The tag-reading path
    // above never needs this - an embedded picture states its own MIME type,
    // and a cover file states it in its extension - but art fetched from a
    // peer and kept in the content-addressed disk cache is bytes and nothing
    // else (see AlbumArtLoader.TryGetArt), so this is the only way to name it.
    // Null when the bytes match nothing here, which callers treat as "no type
    // to report" rather than guessing.
    public static string? MimeTypeForBytes(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 12)
            return null;

        if (bytes[0] == 0xFF && bytes[1] == 0xD8)
            return "image/jpeg";
        if (bytes[0] == 0x89 && bytes[1] == 'P' && bytes[2] == 'N' && bytes[3] == 'G')
            return "image/png";
        if (bytes[0] == 'G' && bytes[1] == 'I' && bytes[2] == 'F')
            return "image/gif";
        if (bytes[0] == 'B' && bytes[1] == 'M')
            return "image/bmp";
        if (bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F' &&
            bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P')
            return "image/webp";
        // Both endiannesses of a TIFF header ("II*\0" and "MM\0*").
        if ((bytes[0] == 'I' && bytes[1] == 'I' && bytes[2] == 0x2A && bytes[3] == 0x00) ||
            (bytes[0] == 'M' && bytes[1] == 'M' && bytes[2] == 0x00 && bytes[3] == 0x2A))
            return "image/tiff";

        return null;
    }

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
