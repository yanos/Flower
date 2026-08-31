using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Flower.Models;
using Flower.Services;

namespace Flower.ViewModels;

public class TrackRowViewModel : DownloadIndicatorViewModel
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
            RefreshDownloadable();

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
        // Belt and braces for the case IsAvailable's own setter can't cover: a
        // row whose availability didn't change but whose Track did (a rescan
        // handing it a downloaded file where a placeholder used to be).
        RefreshDownloadable();
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
            RefreshDownloadable();
        }
    }

    // Drives the row's dimmed/faded visual (see MobileMainView's
    // Button.trackRow.placeholder style) - a placeholder is only actually
    // "unavailable" while it can't currently be streamed/downloaded; one
    // that IsAvailable can be played or downloaded right now and should look
    // like any other row.
    public bool IsUnavailable => IsPlaceholder && !IsAvailable;

    // A row is worth showing a download icon on when it is a placeholder the
    // paired server can serve right now - see DownloadIndicatorViewModel.
    // IsDownloadable, which every other kind of indicator has its own answer
    // for, hence pushed rather than computed there.
    // Reads _track directly rather than through IsPlaceholder: an object
    // initializer can run the IsAvailable setter (which calls this) before
    // Track has been assigned, and this is not the place to depend on the
    // order of FromPlan's initializer.
    private void RefreshDownloadable() => IsDownloadable = _track is { Path: null } && IsAvailable;

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
