using System;
using System.Collections.Generic;
using System.IO;

using Microsoft.Extensions.Logging;

using Flower.Models;
using Flower.Persistence.Sql;

namespace Flower.Persistence
{
    // One-time migration of the pre-SQLite JSON stores (library.json,
    // playlists.json) into the database - see docs/ARCHITECTURE-REVIEW.md
    // Tier 4.1.
    //
    // Import-once, not dual-write. Tier 4.1 originally proposed writing both
    // for one release so a bad database could be recovered from the JSON, but
    // there are no released users and nothing depends on the old files (see
    // CLAUDE.md, "No Users Yet"), so keeping a second writable copy of the
    // library in sync would be a correctness burden protecting nobody. The
    // imported files are renamed aside rather than deleted, so a developer's
    // own library is recoverable by hand if this ever goes wrong.
    public static class JsonLibraryImport
    {
        // The shape playlists.json was written in. Lives here rather than on
        // PlaylistStore now that it is only ever read, never written - it
        // describes a legacy file format, not the current playlist store.
        internal sealed record PlaylistTrackRecord(Guid Id);

        internal sealed record PlaylistRecord(
            string Name,
            Guid Id,
            DateTimeOffset UpdatedAt,
            List<PlaylistTrackRecord>? Tracks);

        // Suffix applied to an imported file. Its presence is also what stops
        // a second import: the source file no longer exists under its old name.
        public const string ImportedSuffix = ".imported";

        // Runs before anything reads the database. A no-op once the JSON files
        // are gone, which is the steady state after the first launch.
        public static void RunIfNeeded(FlowerDb db, ILogger logger)
        {
            var libraryJson = Path.Combine(AppDataDirectory.Path, "library.json");
            var playlistsJson = Path.Combine(AppDataDirectory.Path, "playlists.json");

            if (!File.Exists(libraryJson) && !File.Exists(playlistsJson))
                return;

            try
            {
                List<Track> tracks = [];
                if (File.Exists(libraryJson))
                {
                    // A null here is NOT "no tracks" - AtomicJsonFile.Read
                    // catches an unreadable/corrupt file, quarantines it and
                    // returns null rather than throwing. Treating that as an
                    // empty library would import nothing, rename the file
                    // aside as though it had succeeded, and leave the user
                    // with a wiped library and a rescan that resets every play
                    // count to zero - the exact total-loss case the JSON
                    // crash-safety work existed to prevent.
                    tracks = AtomicJsonFile.Read(libraryJson, FlowerJsonContext.Default.TrackList, logger)
                             ?? throw new InvalidDataException(
                                 $"{libraryJson} could not be read; refusing to import an empty library over it");
                }

                // Imported together, in one pass, because playlist membership
                // is stored as track ids and can only resolve against the
                // tracks being imported alongside it.
                new TrackRepository(db).ReplaceAll(tracks);
                logger.LogInformation("Imported {Count} tracks from {Path} into SQLite", tracks.Count, libraryJson);

                if (File.Exists(playlistsJson))
                {
                    var playlists = ReadPlaylists(playlistsJson, tracks, logger);
                    new PlaylistRepository(db).Save(playlists);
                    logger.LogInformation("Imported {Count} playlists from {Path} into SQLite", playlists.Count, playlistsJson);
                }

                RenameAside(libraryJson, logger);
                RenameAside(playlistsJson, logger);
            }
            catch (Exception ex)
            {
                // Left in place deliberately on failure: the JSON is still the
                // only copy of the user's library at this point, so renaming it
                // aside after a partial import would be the one way to actually
                // lose it. A retry happens on next launch.
                logger.LogError(ex, "Failed to import the JSON library into SQLite; leaving the JSON files in place");
            }
        }

        private static List<Playlist> ReadPlaylists(string path, IReadOnlyList<Track> tracks, ILogger logger)
        {
            var records = AtomicJsonFile.Read(path, FlowerJsonContext.Default.PlaylistRecordList, logger) ?? [];

            var byId = new Dictionary<Guid, Track>(tracks.Count);
            foreach (var track in tracks)
                byId.TryAdd(track.Id, track);

            var playlists = new List<Playlist>(records.Count);
            foreach (var record in records)
            {
                var resolved = new List<Track>();
                foreach (var entry in record.Tracks ?? [])
                {
                    if (byId.TryGetValue(entry.Id, out var track))
                        resolved.Add(track);
                }

                playlists.Add(new Playlist(record.Id, record.Name, resolved, record.UpdatedAt));
            }

            return playlists;
        }

        private static void RenameAside(string path, ILogger logger)
        {
            if (!File.Exists(path))
                return;

            var destination = path + ImportedSuffix;
            File.Delete(destination);
            File.Move(path, destination);
            logger.LogInformation("Renamed {Path} to {Destination} after import", path, destination);
        }
    }
}
