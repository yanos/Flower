using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Flower.Models;

namespace Flower.ViewModels;

public class TrackRowViewModel : ViewModelBase
{
    public const double RowHeight      = 28.0;
    public const double ArtColumnWidth = 80.0;
    public const double ArtMaxSize     = 76.0; // ArtColumnWidth - 2px margin each side

    private static readonly string[] ImageExtensions =
        [".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tiff", ".tif"];

    // Key: directory path.  WeakReference so GC can reclaim bitmaps under memory pressure.
    private static readonly ConcurrentDictionary<string, WeakReference<Bitmap>> ArtCache = new();

    // ── Data ─────────────────────────────────────────────────────────────────

    public Track Track { get; init; } = null!;

    public bool IsFirstInAlbumGroup { get; init; }
    public int  AlbumGroupSize      { get; init; }

    // Height of the album art image — capped at ArtMaxSize so it never bleeds into the next group.
    // For short albums (1–2 tracks) the image is proportionally smaller; for 3+ tracks it's square.
    public double AlbumArtDisplaySize => Math.Min(AlbumGroupSize * RowHeight, ArtMaxSize);

    // ── Display helpers ───────────────────────────────────────────────────────

    public string TrackNumberDisplay => Track.TrackNumber > 0 ? Track.TrackNumber.ToString() : "";

    public string PlayCountDisplay => Track.PlayCount > 0 ? Track.PlayCount.ToString() : "";

    public string DurationDisplay
    {
        get
        {
            var ts = Track.Duration;
            return (int)ts.TotalHours > 0
                ? ts.ToString(@"h\:mm\:ss")
                : ts.ToString(@"m\:ss");
        }
    }

    // ── Selection / playing ───────────────────────────────────────────────────

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    private bool _isCurrentlyPlaying;
    public bool IsCurrentlyPlaying
    {
        get => _isCurrentlyPlaying;
        set { _isCurrentlyPlaying = value; OnPropertyChanged(); }
    }

    // ── Album art (lazy, async) ───────────────────────────────────────────────

    private Bitmap? _albumArt;
    private int     _artState; // 0=idle, 1=loading, 2=done

    public Bitmap? AlbumArt
    {
        get
        {
            if (IsFirstInAlbumGroup && Interlocked.CompareExchange(ref _artState, 1, 0) == 0)
                _ = LoadArtAsync();
            return _albumArt;
        }
        private set { _albumArt = value; OnPropertyChanged(); }
    }

    private async Task LoadArtAsync()
    {
        var key = Path.GetDirectoryName(Track.Path ?? "") ?? "";

        if (ArtCache.TryGetValue(key, out var weak) && weak.TryGetTarget(out var cached))
        {
            Interlocked.Exchange(ref _artState, 2);
            AlbumArt = cached;
            return;
        }

        var bmp = await Task.Run(LoadBitmap);
        Interlocked.Exchange(ref _artState, 2);

        if (bmp != null)
            ArtCache[key] = new WeakReference<Bitmap>(bmp);

        AlbumArt = bmp;
    }

    private Bitmap? LoadBitmap()
    {
        // 1. Embedded tag art
        if (Track.Path != null)
        {
            try
            {
                using var tagFile = TagLib.File.Create(Track.Path);
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
                var dir = Path.GetDirectoryName(Track.Path);
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
                    if (file != null) return new Bitmap(file);
                }
            }
            catch { }
        }

        return null;
    }
}
