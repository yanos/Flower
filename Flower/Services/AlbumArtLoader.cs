using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Media.Imaging;

using Flower.Models;

namespace Flower.Services;

// Shared album-art lookup (embedded tag picture, falling back to a cover/folder
// image file) used by both TrackRowViewModel (track list art column) and
// TrackInfoWindow (header thumbnail) - extracted here so there's one cache and
// one implementation instead of two copies drifting apart.
public static class AlbumArtLoader
{
    private static readonly string[] ImageExtensions =
        [".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tiff", ".tif"];

    // Key: directory path. WeakReference so GC can reclaim bitmaps under memory pressure.
    private static readonly ConcurrentDictionary<string, WeakReference<Bitmap>> Cache = new();

    public static async Task<Bitmap?> LoadAsync(Track track)
    {
        var key = Path.GetDirectoryName(track.Path ?? "") ?? "";

        if (Cache.TryGetValue(key, out var weak) && weak.TryGetTarget(out var cached))
            return cached;

        var bmp = await Task.Run(() => LoadBitmap(track));
        if (bmp != null)
            Cache[key] = new WeakReference<Bitmap>(bmp);

        return bmp;
    }

    private static Bitmap? LoadBitmap(Track track)
    {
        if (track.Path == null)
            return null;

        // 1. Embedded tag art
        try
        {
            using var tagFile = TagLib.File.Create(track.Path);
            var pic = tagFile.Tag.Pictures.FirstOrDefault();
            if (pic?.Data?.Data is { Length: > 0 } data)
            {
                using var ms = new MemoryStream(data);
                return new Bitmap(ms);
            }
        }
        catch { }

        // 2. cover.*/folder.* in the same directory
        try
        {
            var dir = Path.GetDirectoryName(track.Path);
            if (dir != null)
            {
                var file = Directory.EnumerateFiles(dir).FirstOrDefault(f =>
                {
                    var stem = Path.GetFileNameWithoutExtension(f);
                    var ext  = Path.GetExtension(f).ToLowerInvariant();
                    return (stem.Equals("cover",  StringComparison.OrdinalIgnoreCase) ||
                            stem.Equals("folder", StringComparison.OrdinalIgnoreCase))
                        && ImageExtensions.Contains(ext);
                });
                if (file != null)
                    return new Bitmap(file);
            }
        }
        catch { }

        return null;
    }
}
