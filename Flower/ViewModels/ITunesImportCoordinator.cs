using System;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Flower.Importer;
using Flower.Models;
using Flower.Persistence;

namespace Flower.ViewModels;

// Applies iTunes/Music.app-exported play counts and Date Added values to the
// library - split out of MainViewModel, which surfaced these as two of its six
// unrelated jobs (see docs/ARCHITECTURE-REVIEW.md Tier 4.2). Both entry points
// are called from two places each (App.axaml.cs's startup rescan and Settings'
// OK button), and the cooldown between them is the only state involved, so
// this needs nothing from the ViewModel except the busy indicator to drive.
public sealed class ITunesImportCoordinator
{
    // Both entry points below can be triggered from more than one place in
    // close succession - the startup rescan (App.axaml.cs) fires whenever the
    // checkbox is checked, and Settings' OK button whenever it has just been
    // turned on - so a box ticked shortly after launch would otherwise re-run
    // the same ~1-2s AppleScript export twice back to back. A call landing
    // within a minute of the previous one finishing is skipped.
    internal static TimeSpan Cooldown = TimeSpan.FromMinutes(1);

    private readonly Library _library;
    private readonly BusyState _busy;
    private readonly ILogger _logger;

    private readonly object _snapshotGate = new();
    private Task<ITunesSnapshot?>? _snapshot;

    private DateTimeOffset? _lastPlayCountSyncAt;
    private DateTimeOffset? _lastDateAddedSyncAt;

    public ITunesImportCoordinator(Library library, BusyState busy, ILogger<ITunesImportCoordinator> logger)
    {
        _library      = library;
        _busy         = busy;
        _logger       = logger;
    }

    // Exports a fresh XML snapshot from Music.app (via AppleScript - see
    // ITunesPlayCountImporter) and applies its play counts to
    // Track.ImportedPlayCount. Shared by MainViewModel's
    // SyncPlayCountFromITunes setter (apply-immediately-on-toggle) and
    // App.axaml.cs's startup rescan (apply-on-every-launch), both of which run
    // this off the UI thread already - the busy scope drives the status bar
    // spinner either way.
    public Task SyncPlayCountAsync() =>
        RunAsync(ref _lastPlayCountSyncAt, "play count", "Syncing play counts from Music.app…",
            (tracks, snapshot) => ITunesPlayCountImporter.Apply(tracks, snapshot, _logger));

    // Same shape as SyncPlayCountAsync above, but for Track.DateAdded via
    // ITunesDateAddedImporter - see that class for the oldest-wins conflict rule.
    public Task SyncDateAddedAsync() =>
        RunAsync(ref _lastDateAddedSyncAt, "date added", "Syncing date added from Music.app…",
            (tracks, snapshot) => ITunesDateAddedImporter.Apply(tracks, snapshot, _logger));

    private Task RunAsync(ref DateTimeOffset? lastRunAt, string what, string busyMessage, Func<System.Collections.Generic.IEnumerable<Track>, ITunesSnapshot, bool> apply)
    {
        if (lastRunAt is { } last && DateTimeOffset.UtcNow - last < Cooldown)
        {
            _logger.LogTrace("Skipping iTunes {What} sync - ran {ElapsedSeconds:F0}s ago, inside the {CooldownSeconds:F0}s cooldown",
                what, (DateTimeOffset.UtcNow - last).TotalSeconds, Cooldown.TotalSeconds);
            return Task.CompletedTask;
        }
        lastRunAt = DateTimeOffset.UtcNow;
        return ApplyAsync(busyMessage, apply);
    }

    private async Task ApplyAsync(string busyMessage, Func<System.Collections.Generic.IEnumerable<Track>, ITunesSnapshot, bool> apply)
    {
        using var _ = _busy.BeginScope(busyMessage);
        // Most launches find exactly the numbers the last one left, and
        // announcing that anyway cost a whole-table rewrite and a rebuild of
        // every list, twice per launch (October C1.3).
        var snapshot = await SnapshotAsync();
        if (snapshot == null || !await Task.Run(() => apply(_library.Tracks, snapshot)))
            return;

        // Same list, same Track instances mutated in place, all of them - so the
        // library-wide announcement rather than a TrackChanged naming every
        // track. NotifyLibraryChanged, not UpdateTracks(Library.Tracks):
        // passing Tracks back into UpdateTracks as if it were a fresh scan
        // result double-counts every placeholder (Path == null) track, since
        // UpdateTracks' own carry-forward step re-adds them a second time on
        // top of their copy already sitting in the argument. It also issues
        // the whole-table rewrite this needs - see Library's ITrackStore.
        _library.NotifyLibraryChanged();
    }

    // One export serves both imports when they run together, as they do at
    // launch. It is let go a cooldown after it was read: a parsed library can
    // be tens of megabytes, and the next import wants a fresh export anyway.
    // A failed one is kept as long, so a refused automation prompt is not
    // asked again straight away.
    private Task<ITunesSnapshot?> SnapshotAsync()
    {
        lock (_snapshotGate)
        {
            if (_snapshot is { } shared)
                return shared;

            var load = Task.Run(() => ITunesSnapshot.Load(_logger));
            _snapshot = load;
            _ = ReleaseAfterCooldownAsync(load);
            return load;
        }
    }

    private async Task ReleaseAfterCooldownAsync(Task<ITunesSnapshot?> load)
    {
        try
        {
            await load;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading Music.app's library failed");
        }

        await Task.Delay(Cooldown);
        lock (_snapshotGate)
        {
            if (ReferenceEquals(_snapshot, load))
                _snapshot = null;
        }
    }
}
