using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Flower.Models;
using Flower.Services;

namespace Flower.ViewModels;

public class TrackRowViewModel : ViewModelBase, IDisposable
{
    public const double RowHeight      = 28.0;
    public const double ArtColumnWidth = 80.0;
    public const double ArtMaxSize     = 76.0; // ArtColumnWidth - 2px margin each side

    // ── Data ─────────────────────────────────────────────────────────────────

    // Settable rather than init-only because a rebuild reuses this instance
    // instead of allocating a fresh one (see TrackRowMerge): a rescan hands the
    // same track a brand-new Track object, and a filter or sort moves it into a
    // different album run. Only ApplyPlan writes them, and only on the UI
    // thread.
    private Track _track = null!;
    public Track Track
    {
        get => _track;
        init => _track = value;
    }

    private bool _isFirstInAlbumGroup;
    public bool IsFirstInAlbumGroup
    {
        get => _isFirstInAlbumGroup;
        init => _isFirstInAlbumGroup = value;
    }

    private int _albumGroupSize;
    public int AlbumGroupSize
    {
        get => _albumGroupSize;
        init => _albumGroupSize = value;
    }

    internal static TrackRowViewModel FromPlan(in TrackRowPlan plan, AnimationClock? clock) => new()
    {
        Clock               = clock,
        Track               = plan.Track,
        IsFirstInAlbumGroup = plan.IsFirstInAlbumGroup,
        AlbumGroupSize      = plan.AlbumGroupSize,
        IsCurrentlyPlaying  = plan.IsCurrentlyPlaying,
        IsAvailable         = plan.IsAvailable,
        IsAlbumGroupUnavailable = plan.IsAlbumGroupUnavailable,
    };

    // Re-points a surviving row at what the rebuild says it should now be,
    // raising PropertyChanged only for what actually moved. UI thread only.
    internal void ApplyPlan(in TrackRowPlan plan)
    {
        if (!ReferenceEquals(_track, plan.Track))
        {
            var previous = _track;
            _track = plan.Track;

            // Everything bound through the Track.* paths TrackRowControl
            // builds (Track.Title, Track.Artists, ...) re-reads off this one
            // notification; the rest are this class's own derived displays.
            OnPropertyChanged(nameof(Track));
            OnPropertyChanged(nameof(TrackNumberDisplay));
            OnPropertyChanged(nameof(PlayCountDisplay));
            OnPropertyChanged(nameof(DateAddedDisplay));
            OnPropertyChanged(nameof(LastPlayedDisplay));
            OnPropertyChanged(nameof(DurationDisplay));
            OnPropertyChanged(nameof(IsPlaceholder));
            OnPropertyChanged(nameof(IsUnavailable));
            OnPropertyChanged(nameof(IsDownloadable));

            // The whole point of reuse is that the already-decoded bitmap
            // survives instead of being discarded and re-fetched. It only
            // stops being the right image if what AlbumArtLoader would key on
            // changed under us - an edited album tag, or a placeholder that
            // has since been downloaded and now has a real file to read art
            // from.
            if (!ArtSourceMatches(previous, plan.Track))
                ResetAlbumArt();
        }

        if (_isFirstInAlbumGroup != plan.IsFirstInAlbumGroup)
        {
            _isFirstInAlbumGroup = plan.IsFirstInAlbumGroup;
            OnPropertyChanged(nameof(IsFirstInAlbumGroup));
        }

        if (_albumGroupSize != plan.AlbumGroupSize)
        {
            _albumGroupSize = plan.AlbumGroupSize;
            OnPropertyChanged(nameof(AlbumGroupSize));
            OnPropertyChanged(nameof(AlbumArtDisplaySize));
        }

        IsCurrentlyPlaying = plan.IsCurrentlyPlaying;
        IsAvailable        = plan.IsAvailable;
        IsAlbumGroupUnavailable = plan.IsAlbumGroupUnavailable;
    }

    private static bool ArtSourceMatches(Track a, Track b) =>
        a.Album == b.Album &&
        a.Path == b.Path &&
        a.OriginAlbumArtHash == b.OriginAlbumArtHash &&
        a.OriginDeviceFingerprint == b.OriginDeviceFingerprint;

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

    public string LastPlayedDisplay => Track.LastPlayedAt is { } lastPlayed ? lastPlayed.LocalDateTime.ToString("MMM d, yyyy") : "";

    // Track is not INotifyPropertyChanged and these two read straight off it,
    // so a play-count/LastPlayedAt bump has to be pushed in from outside. Rows
    // used to be rebuilt wholesale on every play, which is what made that
    // unnecessary - and is exactly the cost Tier 1.1 removed. Called from
    // MainViewModel's Library.TrackStatsChanged handler, on the UI thread.
    public void NotifyStatsChanged()
    {
        OnPropertyChanged(nameof(PlayCountDisplay));
        OnPropertyChanged(nameof(LastPlayedDisplay));
    }

    // Not yet downloaded (see LibrarySyncService/LibraryDownloadService,
    // SYNC-PLAN.md Phase 3) - mobile-only for v1, see MobileMainView's row
    // template. Track itself isn't INotifyPropertyChanged, but that's fine here:
    // a successful download fires Library.TracksUpdated, which rebuilds Rows
    // entirely (see MainViewModel.PopulateTracks), so the placeholder row this
    // property was read from is simply replaced by a fresh non-placeholder one -
    // this value never needs to change out from under a still-alive instance.
    public bool IsPlaceholder => Track.Path == null;

    // Whether this placeholder can actually be streamed/downloaded right now -
    // i.e. its origin device is the Client's currently paired, reachable
    // Server (see TrackAvailability.IsAvailable, the single place this is
    // computed - PeerTrackResolver.Resolve/SyncRolePolicy.MayRequestFrom gate
    // every real download/stream/art request the exact same way, so a
    // placeholder from some other reachable-but-not-paired peer, an
    // unreachable paired server, or no paired server at all all land here as
    // false). Set by TrackListBuilder.BuildRows at construction time and kept
    // live afterward by TrackAvailability.Apply, called once whenever
    // PairedServerReachability.Changed fires. Defaults to false so a
    // just-built row never flashes as available before it's actually known
    // to be.
    private bool _isAvailable;
    public bool IsAvailable
    {
        get => _isAvailable;
        set
        {
            if (_isAvailable == value)
                return;
            _isAvailable = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsUnavailable));
            OnPropertyChanged(nameof(IsDownloadable));
        }
    }

    // Drives the row's dimmed/faded visual (see MobileMainView's
    // Button.trackRow.placeholder style) - a placeholder is only actually
    // "unavailable" while it can't currently be streamed/downloaded; one
    // that IsAvailable can be played or downloaded right now and should look
    // like any other row.
    public bool IsUnavailable => IsPlaceholder && !IsAvailable;

    // The greyed-out state of the album art cell, which spans the whole album
    // run this row may only be one line of (see IsFirstInAlbumGroup /
    // AlbumGroupSize) - so it follows the run, not this row: art beside a
    // half-downloaded album stays at full strength while any of its tracks can
    // still be played. Computed per run by TrackListBuilder.PlanRows and
    // pushed in the same way IsAvailable is, including by
    // TrackAvailability.Apply when the server comes or goes.
    private bool _isAlbumGroupUnavailable;
    public bool IsAlbumGroupUnavailable
    {
        get => _isAlbumGroupUnavailable;
        set
        {
            if (_isAlbumGroupUnavailable == value)
                return;
            _isAlbumGroupUnavailable = value;
            OnPropertyChanged();
        }
    }

    // Gates the row's own download button (see TrackRowTemplate) - hidden
    // entirely rather than shown-then-failing when there's nobody to
    // actually download this specific track from right now. Separate from
    // IsDownloadUnavailable below, which reflects an attempt that was
    // already made and failed (a different, rarer case - the peer was
    // reachable at tap time but the transfer itself didn't succeed) rather
    // than "there was never anyone to try".
    public bool IsDownloadable => IsPlaceholder && IsAvailable;

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

    // Supplied by whoever built this row (LibraryBrowserViewModel threads the
    // container's instance down through TrackRowMerge). Null only for a row
    // built by a static builder with no container behind it - mobile's search
    // results, the previewer, a test - which falls back to the shared default.
    private AnimationClock? _clock;
    public AnimationClock? Clock
    {
        get => _clock;
        init => _clock = value;
    }

    private IDisposable? _spin;
    private double _spinAngle;
    public double SpinAngle
    {
        get => _spinAngle;
        private set { _spinAngle = value; OnPropertyChanged(); }
    }

    private void StartSpin()
    {
        _spin?.Dispose();
        // ~1 revolution/second, derived from elapsed time rather than
        // accumulated per tick, so several rows downloading at once stay in
        // phase and a dropped frame doesn't leave one lagging behind forever.
        _spin = (_clock ?? AnimationClock.Current).Subscribe(
            elapsed => SpinAngle = elapsed.TotalSeconds * 360 % 360);
    }

    private void StopSpin()
    {
        _spin?.Dispose();
        _spin = null;
        SpinAngle = 0;
    }

    // A row that does not survive a rebuild (see TrackRowMerge) is dropped, and
    // StopSpin above only ever ran from the IsDownloading setter - so a
    // discarded row left its animation subscription registered forever, with
    // the callback keeping this view-model alive and keeping the shared clock
    // awake. Rows that *are* reused must not be disposed: their spinner is
    // still on screen and still downloading.
    public void Dispose() => StopSpin();

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

    // Both guard on the value rather than raising unconditionally: a rebuild
    // now re-applies these to every surviving row (see ApplyPlan), and an
    // unguarded setter would invalidate a binding on all ~16k of them to say
    // nothing changed.
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    private bool _isCurrentlyPlaying;
    public bool IsCurrentlyPlaying
    {
        get => _isCurrentlyPlaying;
        set
        {
            if (_isCurrentlyPlaying == value)
                return;
            _isCurrentlyPlaying = value;
            OnPropertyChanged();
        }
    }

    // ── Album art (lazy, async) ───────────────────────────────────────────────

    private Bitmap? _albumArt;
    private int     _artState; // 0=idle, 1=loading, 2=done
    // Bumped by ResetAlbumArt so a load already in flight for the *previous*
    // track can tell it has been superseded and drop its result, rather than
    // publishing the old album's cover and parking _artState at "done" where
    // nothing would ever reload it.
    private int     _artGeneration;

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

    // Back to state 0 (idle) rather than straight to a reload: nothing may ever
    // read AlbumArt on this row again (it can be scrolled far off screen), and
    // the getter is what decides that.
    private void ResetAlbumArt()
    {
        Interlocked.Increment(ref _artGeneration);
        Interlocked.Exchange(ref _artState, 0);
        AlbumArt = null;
    }

    private async Task LoadArtAsync()
    {
        var generation = Volatile.Read(ref _artGeneration);
        var bmp = await AlbumArtLoader.Current.LoadAsync(Track);
        if (Volatile.Read(ref _artGeneration) != generation)
            return;
        Interlocked.Exchange(ref _artState, 2);
        AlbumArt = bmp;
    }
}
