using System;

using Flower.Services;

namespace Flower.ViewModels;

// The state behind one download icon (TrackDownloadButton): whether there is
// anything to fetch, whether a fetch is in flight, whether the last one failed,
// and the spinner's angle while it runs.
//
// Shared because the same icon now appears in four places over three different
// view-models - the main track list's rows (TrackRowViewModel), an expanded
// album's song rows and its tiles (ExpandedTrackRowViewModel,
// AlbumTileViewModel), on both desktop and mobile. It was originally
// TrackRowViewModel's own state, which is why the comments here talk about
// rows; a tile is the same thing standing for a whole album's worth of tracks
// at once.
public abstract class DownloadIndicatorViewModel : ViewModelBase, IDisposable
{
    // Gates the icon's visibility - hidden entirely rather than shown-then-
    // failing when there's nobody to actually download this from right now.
    // Separate from IsDownloadUnavailable below, which reflects an attempt that
    // was already made and failed (a different, rarer case - the peer was
    // reachable at click time but the transfer itself didn't succeed) rather
    // than "there was never anyone to try".
    //
    // Pushed in by whoever owns the availability question for this kind of
    // indicator (TrackAvailability.Apply for rows and tiles,
    // AlbumGridRowViewModel.Availability for an expanded album's rows) rather
    // than computed here, since each of them asks it of a different thing.
    private bool _isDownloadable;
    public bool IsDownloadable
    {
        get => _isDownloadable;
        set
        {
            if (_isDownloadable == value)
                return;
            _isDownloadable = value;
            OnPropertyChanged();
        }
    }

    // Transient UI state for an in-flight download - set by TrackDownloadRunner,
    // not derived from any Track.
    //
    // Drives SpinAngle below directly (rather than a separate View-side
    // attached-property/animation helper) so the spinner's rotation is owned
    // entirely by this view-model's lifetime, not the View's - confirmed on a
    // real device this matters: every list this appears in virtualizes and
    // recycles its row containers as items scroll in and out, and a batch
    // download can easily have several rows downloading at once while only a
    // couple are actually realized on screen. A View-side timer tied to a
    // Control's own lifetime got started/stopped by container recycling
    // independent of whether the underlying download was still genuinely in
    // progress - observed as the spinner working for the first couple of rows
    // in a batch, then never appearing again. A plain bindable double on the
    // view-model has no such problem: ordinary data binding re-attaches
    // correctly whenever a recycled container's DataContext changes.
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

    private bool _isDownloadUnavailable;
    public bool IsDownloadUnavailable
    {
        get => _isDownloadUnavailable;
        set { _isDownloadUnavailable = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsDownloadIdle)); }
    }

    // Neither in flight nor just-failed - the default "click to download" icon
    // state. A plain computed property (not stored) kept in sync via the two
    // setters above rather than a converter, since it depends on both.
    public bool IsDownloadIdle => !_isDownloading && !_isDownloadUnavailable;

    // Supplied by whoever built this (LibraryBrowserViewModel threads the
    // container's instance down through TrackRowMerge). Null only when built by
    // a static builder with no container behind it - mobile's search results,
    // the previewer, a test - which falls back to the shared default.
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

    // Something that does not survive a rebuild (see TrackRowMerge) is dropped,
    // and StopSpin above only ever ran from the IsDownloading setter - so a
    // discarded row left its animation subscription registered forever, with the
    // callback keeping this view-model alive and keeping the shared clock awake.
    // Things that *are* reused must not be disposed: their spinner is still on
    // screen and still downloading.
    public void Dispose() => StopSpin();
}
