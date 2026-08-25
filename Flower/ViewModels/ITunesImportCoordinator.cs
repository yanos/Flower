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
    // close succession - the startup rescan (App.axaml.cs) and Settings' OK
    // button both fire unconditionally based on "is the checkbox currently
    // checked," not "did it just run" - so opening Settings and clicking OK
    // shortly after launch would otherwise re-run the same ~1-2s AppleScript
    // export twice back to back. A call landing within a minute of the
    // previous one finishing is skipped.
    internal static TimeSpan Cooldown = TimeSpan.FromMinutes(1);

    private readonly Library _library;
    private readonly BusyState _busy;
    private readonly ILogger _logger;

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
            tracks => ITunesPlayCountImporter.Apply(tracks, _logger));

    // Same shape as SyncPlayCountAsync above, but for Track.DateAdded via
    // ITunesDateAddedImporter - see that class for the oldest-wins conflict rule.
    public Task SyncDateAddedAsync() =>
        RunAsync(ref _lastDateAddedSyncAt, "date added", "Syncing date added from Music.app…",
            tracks => ITunesDateAddedImporter.Apply(tracks, _logger));

    private Task RunAsync(ref DateTimeOffset? lastRunAt, string what, string busyMessage, Action<System.Collections.Generic.IEnumerable<Track>> apply)
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

    private async Task ApplyAsync(string busyMessage, Action<System.Collections.Generic.IEnumerable<Track>> apply)
    {
        using var _ = _busy.BeginScope(busyMessage);
        await Task.Run(() => apply(_library.Tracks));
        // Same list, same Track instances mutated in place - just need
        // TracksUpdated to fire so the Plays column reflects the new
        // ImportedPlayCount values immediately. NotifyTrackChanged (not
        // UpdateTracks(Library.Tracks)) specifically - see its own doc
        // comment: passing Tracks back into UpdateTracks as if it were a
        // fresh scan result double-counts every placeholder (Path == null)
        // track, since UpdateTracks' own carry-forward step re-adds them a
        // second time on top of their copy already sitting in the argument.
        // The no-argument form: this mutated every track in place, so the
        // whole-table rewrite it issues is what actually changed. Persisting
        // is NotifyTrackChanged's own now - see Library's ITrackStore.
        _library.NotifyTrackChanged();
    }
}
