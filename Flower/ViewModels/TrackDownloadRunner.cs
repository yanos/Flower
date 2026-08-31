using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Flower.Models;
using Flower.Services;

namespace Flower.ViewModels;

// Drives placeholder downloads on behalf of the UI: the per-row idle/in-flight/
// failed icon state, the minimum spinner duration, and the concurrency cap for
// a batch. The actual transfer is LibraryDownloadService's job, reached through
// the download delegate this is handed (MainViewModel.DownloadTrackAsync).
//
// Shared by mobile (the row's own download button, and the top bar's
// "download everything on this screen") and desktop (the track list's download
// icon, and the Download items in its right-click menus) - the two used to be
// mobile-only code inside MobileMainViewModel, and desktop growing the same
// feature is exactly the moment to stop that being a mobile detail.
public sealed class TrackDownloadRunner : ViewModelBase
{
    private readonly Func<Track, Task<TrackDownloadResult>> _download;
    private readonly Func<string, TrackRowViewModel?> _resolveRow;
    private readonly Func<string?, IDisposable>? _beginBusyScope;

    // resolveRow maps a Track.SyncKey back to whichever row currently shows it,
    // deliberately re-resolved per download rather than captured up front - see
    // DownloadAllAsync.
    public TrackDownloadRunner(
        Func<Track, Task<TrackDownloadResult>> download,
        Func<string, TrackRowViewModel?> resolveRow,
        Func<string?, IDisposable>? beginBusyScope = null)
    {
        _download       = download;
        _resolveRow     = resolveRow;
        _beginBusyScope = beginBusyScope;
    }

    // A download over the local LAN can finish in well under a UI frame, in
    // which case IsDownloading would flip true then straight back to false
    // before anything ever actually painted it - observed in practice as the
    // spinner "sometimes not even appearing". Holding it visible for at
    // least this long guarantees it's actually seen, at the cost of a barely
    // perceptible artificial delay on an already-fast download.
    private static readonly TimeSpan MinDownloadSpinnerDuration = TimeSpan.FromMilliseconds(400);

    // How many of a batch's downloads run at once - a middle ground between
    // "fast" and "gentle on the peer being downloaded from".
    private const int MaxConcurrentDownloads = 3;

    // True while DownloadAllAsync is working through a batch - drives mobile's
    // download-all icon swapping to a spinner and disabling itself against a
    // second overlapping run (see ScreenSlot.axaml), and gates the same on
    // desktop's own menu items.
    private bool _isBulkDownloading;
    public bool IsBulkDownloading
    {
        get => _isBulkDownloading;
        private set { if (_isBulkDownloading != value) { _isBulkDownloading = value; OnPropertyChanged(); } }
    }

    // One row's download, with that row's own icon showing its progress. Used
    // directly by a single-track action and by every task in a batch, so a
    // row started either way looks identical.
    public Task DownloadRowAsync(TrackRowViewModel row) => DownloadOneAsync(row, row.Track);

    // The same, for an expanded album's own song rows (desktop's album grid),
    // which are a different view-model over the same icon.
    public Task DownloadRowAsync(ExpandedTrackRowViewModel row) => DownloadOneAsync(row, row.Track);

    private async Task<bool> DownloadOneAsync(DownloadIndicatorViewModel indicator, Track track)
    {
        if (indicator.IsDownloading)
            return false;

        indicator.IsDownloadUnavailable = false;
        indicator.IsDownloading = true;
        var started = DateTime.UtcNow;
        var result = await _download(track);
        var remaining = MinDownloadSpinnerDuration - (DateTime.UtcNow - started);
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining);
        indicator.IsDownloading = false;
        var failed = result is TrackDownloadResult.PeerUnavailable or TrackDownloadResult.Failed;
        indicator.IsDownloadUnavailable = failed;
        return !failed;
    }

    // A whole album behind one icon - the grid tile's own download button,
    // which spins for as long as the batch runs and ends up showing the error
    // state if any single track in it failed. The tracks' own rows, if any of
    // them happen to be on screen, still animate individually underneath (see
    // DownloadAllAsync).
    public async Task DownloadAlbumAsync(DownloadIndicatorViewModel indicator, IReadOnlyList<Track> tracks)
    {
        if (indicator.IsDownloading || IsBulkDownloading)
            return;

        indicator.IsDownloadUnavailable = false;
        indicator.IsDownloading = true;
        try
        {
            var succeeded = await DownloadAllAsync(tracks);
            indicator.IsDownloadUnavailable = !succeeded;
        }
        finally
        {
            indicator.IsDownloading = false;
        }
    }

    // Every not-yet-downloaded track in the given set, up to
    // MaxConcurrentDownloads at a time. The caller decides the scope: mobile's
    // whole current screen, or desktop's current selection / right-clicked
    // album.
    //
    // Captures SyncKeys, not TrackRowViewModel instances - a download
    // completing mid-batch fires Library.TracksUpdated, which (via
    // MainViewModel's own debounced ScheduleFilter) eventually replaces the
    // row list wholesale with a fresh set of TrackRowViewModel objects.
    // Holding onto row objects from a snapshot taken before that swap meant
    // later iterations kept mutating IsDownloading on orphaned rows nothing on
    // screen was bound to anymore - confirmed on a real device as the spinner
    // working for the first couple of songs in a batch, then never appearing
    // again. SyncKey is what survives the swap intact, so each task re-resolves
    // right before it actually starts downloading (after acquiring a throttle
    // slot, not when the task was created) - safe to do without locking despite
    // running "concurrently" here, since every one of these tasks' own code
    // (everything except the actual HTTP I/O in flight) still only ever runs on
    // the UI thread, the same as the ViewModels driving it.
    //
    // A track with no row behind it at all (desktop's album grid, where the
    // right-clicked album's songs may not be in the track list) still
    // downloads - it just has no per-row icon to animate, which is what the
    // busy scope covers instead.
    // Returns whether every track in the batch actually arrived - false also
    // when a second batch was already running and this one did nothing, which
    // is the same answer from the caller's point of view: what was asked for is
    // not all downloaded.
    public async Task<bool> DownloadAllAsync(IEnumerable<Track> tracks)
    {
        if (IsBulkDownloading)
            return false;

        // Keyed by SyncKey for the same reason the row lookup is, and kept as
        // the fallback target for a track no row is showing. Such a Track
        // instance can in principle be replaced by a rescan mid-batch, exactly
        // like the one a single-row download holds - the row path re-resolves
        // because it cheaply can, this one accepts the same (rare, and no
        // worse) staleness the per-row path already lives with.
        var byKey = new Dictionary<string, Track>();
        foreach (var track in tracks)
        {
            if (track.Path == null)
                byKey[track.SyncKey] = track;
        }

        if (byKey.Count == 0)
            return true;

        IsBulkDownloading = true;
        var allSucceeded = true;
        var busy = _beginBusyScope?.Invoke(byKey.Count == 1
            ? "Downloading 1 track…"
            : $"Downloading {byKey.Count} tracks…");
        try
        {
            using var throttle = new SemaphoreSlim(MaxConcurrentDownloads);
            var tasks = byKey.Keys.ToList().Select(async syncKey =>
            {
                await throttle.WaitAsync();
                try
                {
                    if (_resolveRow(syncKey) is { } row)
                    {
                        if (row.Track.Path == null && !await DownloadOneAsync(row, row.Track))
                            allSucceeded = false;
                    }
                    else if (byKey[syncKey].Path == null &&
                             await _download(byKey[syncKey]) is TrackDownloadResult.PeerUnavailable or TrackDownloadResult.Failed)
                        allSucceeded = false;
                }
                finally
                {
                    throttle.Release();
                }
            });
            await Task.WhenAll(tasks);
        }
        finally
        {
            busy?.Dispose();
            IsBulkDownloading = false;
        }

        return allSucceeded;
    }
}
