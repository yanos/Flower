using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
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

    // Sum of Flower's own play count, whatever was imported from iTunes/Music.app
    // (see Track.ImportedPlayCount), and every other synced device's latest known
    // count (Track.RemotePlayCounts) - see Track.TotalPlayCount.
    public string PlayCountDisplay => Track.TotalPlayCount > 0 ? Track.TotalPlayCount.ToString() : "";

    public string DateAddedDisplay => Track.DateAdded.LocalDateTime.ToString("MMM d, yyyy");

    // Not yet downloaded (see LibrarySyncService/LibraryDownloadService,
    // SYNC-PLAN.md Phase 3) - mobile-only for v1, see MobileMainView's row
    // template. Track itself isn't INotifyPropertyChanged, but that's fine here:
    // a successful download fires Library.TracksUpdated, which rebuilds Rows
    // entirely (see MainViewModel.PopulateTracks), so the placeholder row this
    // property was read from is simply replaced by a fresh non-placeholder one -
    // this value never needs to change out from under a still-alive instance.
    public bool IsPlaceholder => Track.Path == null;

    // Whether the placeholder's origin peer is currently reachable - live-
    // updated by MainViewModel.RefreshRowReachability (device list changes,
    // and once after every fresh Rows build) rather than derived from Track,
    // since this genuinely changes over this row instance's lifetime (a peer
    // can come and go on the LAN) unlike IsPlaceholder above. Defaults to
    // true (assume reachable) so a just-built row never flashes as
    // unavailable before MainViewModel's own post-build refresh runs.
    private bool _isPeerReachable = true;
    public bool IsPeerReachable
    {
        get => _isPeerReachable;
        set
        {
            if (_isPeerReachable == value)
                return;
            _isPeerReachable = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsUnavailable));
            OnPropertyChanged(nameof(IsDownloadable));
        }
    }

    // Drives the row's dimmed/faded visual (see MobileMainView's
    // Button.trackRow.placeholder style) - a placeholder is only actually
    // "unavailable" while its peer can't currently be reached for a
    // stream/download; a placeholder whose peer IS reachable can be played
    // or downloaded right now and should look like any other row.
    public bool IsUnavailable => IsPlaceholder && !IsPeerReachable;

    // Gates the row's own download button (see TrackRowTemplate) - hidden
    // entirely rather than shown-then-failing when there's nobody to
    // actually download this specific track from right now. Separate from
    // IsDownloadUnavailable below, which reflects an attempt that was
    // already made and failed (a different, rarer case - the peer was
    // reachable at tap time but the transfer itself didn't succeed) rather
    // than "there was never anyone to try".
    public bool IsDownloadable => IsPlaceholder && IsPeerReachable;

    // Transient UI state for an in-flight/failed download attempt on this row -
    // set directly by MobileMainViewModel's download command, not derived from
    // Track. See the comment above for why a stale value here is harmless: any
    // instance holding it gets discarded once the download actually succeeds.
    //
    // Drives SpinAngle below directly (rather than a separate View-side
    // attached-property/animation helper) so the download spinner's rotation
    // is owned entirely by this row's own ViewModel lifetime, not the View's -
    // confirmed on a real device this matters: TrackListBox virtualizes/
    // recycles row containers as items scroll in and out, and a batch
    // download (DownloadAllVisibleCommand) can easily have several rows
    // downloading at once while only a couple are actually realized on
    // screen. A View-side timer tied to a Control's own lifetime got
    // started/stopped by container recycling independent of whether the
    // underlying download was still genuinely in progress - observed as the
    // spinner working for the first couple of rows in a batch, then never
    // appearing again. A plain bindable double on the row itself has no such
    // problem: Avalonia's ordinary data binding re-attaches correctly
    // whenever a recycled container's DataContext changes, same as every
    // other property on this class already relies on.
    private bool _isDownloading;
    public bool IsDownloading
    {
        get => _isDownloading;
        set
        {
            if (_isDownloading == value)
                return;
            _isDownloading = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDownloadIdle));
            if (value)
                StartSpin();
            else
                StopSpin();
        }
    }

    private DispatcherTimer? _spinTimer;
    private double _spinAngle;
    public double SpinAngle
    {
        get => _spinAngle;
        private set { _spinAngle = value; OnPropertyChanged(); }
    }

    private void StartSpin()
    {
        _spinTimer?.Stop();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _spinTimer = timer;
        timer.Tick += (_, _) => SpinAngle = (SpinAngle + 6) % 360; // ~1 revolution/second.
        timer.Start();
    }

    private void StopSpin()
    {
        _spinTimer?.Stop();
        _spinTimer = null;
        SpinAngle = 0;
    }

    private bool _isDownloadUnavailable;
    public bool IsDownloadUnavailable
    {
        get => _isDownloadUnavailable;
        set { _isDownloadUnavailable = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsDownloadIdle)); }
    }

    // Neither in flight nor just-failed - the default "tap to download" icon
    // state. A plain computed property (not stored) kept in sync via the two
    // setters above rather than a converter, since it depends on both.
    public bool IsDownloadIdle => !_isDownloading && !_isDownloadUnavailable;

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

    // Loads regardless of IsFirstInAlbumGroup - desktop's MusicListView only
    // ever shows this for the group leader (IsVisible="{Binding
    // IsFirstInAlbumGroup}" in TrackRowControl.axaml gates that independently),
    // but mobile's flat row-per-track list (no spanning) wants every row to
    // show its own thumbnail. AlbumArtLoader caches by directory, so repeat
    // loads within one album are cheap regardless of platform.
    public Bitmap? AlbumArt
    {
        get
        {
            if (Interlocked.CompareExchange(ref _artState, 1, 0) == 0)
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
