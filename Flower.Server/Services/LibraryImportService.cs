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
    FlowerDb db,
    IOptions<FlowerServerOptions> options,
    ILogger<Flower.Importer.Importer> importerLogger,
    ILogger<Library> libraryLogger,
    ILogger<LibraryImportService> logger)
{
    public async Task RescanAsync(CancellationToken ct = default)
    {
        var importer = new Flower.Importer.Importer(importerLogger);
        var libraryPaths = options.Value.LibraryPaths;
        var imported = await importer.ImportAsync(libraryPaths);
        logger.LogInformation(
            "Importer found {Count} tracks across {PathCount} configured path(s)", imported.Count, libraryPaths.Count);

        ct.ThrowIfCancellationRequested();

        var repository = new TrackRepository(db);

        // Loaded, reconciled and written back rather than upserted row by row.
        // At the 16k-track scale SYNC-PLAN.md targets this is one full read and
        // one transaction at startup, not per request, and it is the only way
        // to get UpdateTracks' carry-forward rules applied - they compare each
        // fresh track against the stored one.
        var library = new Library(repository.LoadAll(), libraryLogger);
        var before = library.Tracks.Count;
        library.UpdateTracks(imported);
        repository.ReplaceAll(library.Tracks);

        logger.LogInformation(
            "Library sync complete: {Before} -> {After} track(s)", before, library.Tracks.Count);
    }
}
