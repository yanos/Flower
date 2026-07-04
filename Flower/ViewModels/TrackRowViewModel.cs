using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Flower.Models;
using Flower.Services;

namespace Flower.ViewModels;

public class TrackRowViewModel : ViewModelBase
{
    public const double RowHeight      = 28.0;
    public const double ArtColumnWidth = 80.0;
    public const double ArtMaxSize     = 76.0; // ArtColumnWidth - 2px margin each side

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

    public string DateAddedDisplay => Track.DateAdded.LocalDateTime.ToString("MMM d, yyyy");

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
        var bmp = await AlbumArtLoader.LoadAsync(Track);
        Interlocked.Exchange(ref _artState, 2);
        AlbumArt = bmp;
    }
}
