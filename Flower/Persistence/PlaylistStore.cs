using System;
using System.Collections.Generic;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Flower.Models;
using Flower.Persistence.Sql;

namespace Flower.Persistence
{
    // Playlists, backed by SQLite (see Flower.Core's Persistence/Sql/) rather
    // than playlists.json - see docs/ARCHITECTURE-REVIEW.md Tier 4.1. Track
    // membership is still stored as ids and resolved against the library on
    // load; that part did not change, it just moved from a JSON array to a
    // playlist_tracks table with a real position column.
    //
    // Read-only, like LibraryStore: writing a playlist set is Library's own
    // (see its IPlaylistStore), which is what makes an in-place rename or
    // drag-reorder persist without any call site remembering to ask. What is
    // left here is the client-specific policy on the read - an unreadable
    // playlist table degrades to "no playlists" and lets the app start.
    public class PlaylistStore
    {
        private readonly ILogger<PlaylistStore> _logger;
        private readonly PlaylistRepository _playlists;

        // Convenience overload for the many call sites (mostly tests) that don't
        // care about log output - production code always goes through the other
        // constructor instead (see App.axaml.cs).
        public PlaylistStore() : this(NullLogger<PlaylistStore>.Instance) { }

        public PlaylistStore(ILogger<PlaylistStore> logger) : this(logger, FlowerDb.OpenDefault())
        {
        }

        public PlaylistStore(ILogger<PlaylistStore> logger, FlowerDb db)
        {
            _logger = logger;
            _playlists = new PlaylistRepository(db);
        }

        public static string StorePath => FlowerDb.DefaultPath;

        public List<Playlist> Load(IReadOnlyList<Track> libraryTracks)
        {
            try
            {
                return _playlists.Load(libraryTracks);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load playlists from {Path}; starting with no playlists", StorePath);
                return [];
            }
        }
    }
}
