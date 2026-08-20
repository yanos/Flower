using Microsoft.Extensions.Options;

using Flower.Models;
using Flower.Persistence.Sql;
using Flower.Server.Configuration;

namespace Flower.Server.Services;

// Flower.Server's scanner reuses the exact same Flower.Core.Importer.Importer
// the desktop app uses (see SYNC-PLAN.md's "Reuse boundary" note) - and, since
// Tier 4.1, the same Library reconciliation and the same TrackRepository write
// that desktop performs after a rescan.
//
// This used to be ~60 lines of hand-written upsert: load every row into a
// path-keyed dictionary, copy 15 fields across one at a time, work out what to
// delete. All of that already existed, twice as carefully, in
// Library.UpdateTracks (which knows that a rescan mints fresh Track instances
// and must not reset Id, DateAdded, play counts, starred or sync origin) and
// TrackRepository.ReplaceAll (one transaction, one prepared upsert, delete
// what is gone). Calling those instead is not just less code - it is what
// makes a server-served track keep the same id across a rescan, which the
// hand-written version got right only by accident of matching on path, and
// what makes Starred survive one at all.
public class LibraryImportService(
    TrackRepository repository,
    PlaylistRepository playlists,
    Library library,
    IOptions<FlowerServerOptions> options,
    ILogger<Flower.Importer.Importer> importerLogger,
    ILogger<LibraryImportService> logger)
{
    // Publishes what is already stored, without scanning the filesystem. Called
    // at startup before the rescan so the server can answer requests from the
    // moment it is listening rather than after a full disk scan - the same
    // reason App.axaml.cs loads the cached library synchronously before kicking
    // off its own background rescan.
    public void LoadStored()
    {
        var stored = repository.LoadAll();
        library.Reset(stored);

        // Playlists after tracks, and against the tracks just loaded:
        // membership is stored as ids and resolved into live Track references
        // here, so loading them the other way round would resolve every entry
        // against an empty library and drop the lot.
        //
        // ResetPlaylists, not ReplacePlaylists: replaying what is already on
        // disk is not a change, and the latter would persist the set straight
        // back out (see Library's IPlaylistStore).
        library.ResetPlaylists(playlists.Load(library.Tracks));

        logger.LogInformation(
            "Loaded {Count} stored track(s) and {PlaylistCount} playlist(s) before rescan",
            stored.Count, library.Playlists.Count);
    }

    public async Task RescanAsync(CancellationToken ct = default)
    {
        var importer = new Flower.Importer.Importer(importerLogger);
        var libraryPaths = options.Value.LibraryPaths;
        var imported = await importer.ImportAsync(libraryPaths);
        logger.LogInformation(
            "Importer found {Count} tracks across {PathCount} configured path(s)", imported.Count, libraryPaths.Count);

        ct.ThrowIfCancellationRequested();

        // Loaded, reconciled and written back rather than upserted row by row.
        // At the 16k-track scale SYNC-PLAN.md targets this is one full read and
        // one transaction at startup, not per request, and it is the only way
        // to get UpdateTracks' carry-forward rules applied - they compare each
        // fresh track against the stored one. The write is UpdateTracks' own
        // (see Library's ITrackStore), which is also what the client's startup
        // rescan relies on - neither host issues it by hand any more.
        // The one resident Library, reconciled in place. UpdateTracks swaps
        // its track list atomically and drops the derived indexes with it, so a
        // request in flight finishes against the list it started on and never
        // sees a half-built rescan.
        var before = library.Tracks.Count;

        // UpdateTracks rebinds playlist membership onto the Track objects the
        // rescan produced (Library.RebindPlaylistTracks), so the resident
        // playlists survive it without being reloaded.
        library.UpdateTracks(imported);

        logger.LogInformation(
            "Library sync complete: {Before} -> {After} track(s)", before, library.Tracks.Count);
    }
}
