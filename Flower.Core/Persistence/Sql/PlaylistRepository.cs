using System;
using System.Collections.Generic;

using Microsoft.Data.Sqlite;

using Flower.Models;

namespace Flower.Persistence.Sql
{
    // Reading and writing playlists. Like the JSON store this replaces,
    // playlists hold track *references* only - membership is stored as track
    // ids and resolved against the library on load, never as duplicated track
    // metadata.
    public sealed class PlaylistRepository(FlowerDb db)
    {
        public List<Playlist> Load(IReadOnlyList<Track> libraryTracks)
        {
            var byId = new Dictionary<Guid, Track>(libraryTracks.Count);
            foreach (var track in libraryTracks)
                byId.TryAdd(track.Id, track);

            using var connection = db.Open();

            var playlists = new List<(Guid Id, string Name, DateTimeOffset UpdatedAt)>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT id, name, updated_at FROM playlists;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    playlists.Add((
                        Guid.Parse(reader.GetString(0)),
                        reader.GetString(1),
                        new DateTimeOffset(reader.GetInt64(2), TimeSpan.Zero)));
                }
            }

            var membership = new Dictionary<Guid, List<Track>>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT playlist_id, track_id FROM playlist_tracks ORDER BY playlist_id, position;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var playlistId = Guid.Parse(reader.GetString(0));

                    // An entry whose id doesn't resolve is dropped - by then the
                    // track really is gone from the library. Same rule the JSON
                    // store applied, and the same one Flower.Server's
                    // SubsonicMapper applies to its own playlist entries.
                    if (!byId.TryGetValue(Guid.Parse(reader.GetString(1)), out var track))
                        continue;

                    if (!membership.TryGetValue(playlistId, out var tracks))
                        membership[playlistId] = tracks = [];

                    tracks.Add(track);
                }
            }

            var result = new List<Playlist>(playlists.Count);
            foreach (var (id, name, updatedAt) in playlists)
            {
                result.Add(new Playlist(
                    id,
                    name,
                    membership.TryGetValue(id, out var tracks) ? tracks : [],
                    updatedAt));
            }

            return result;
        }

        // Replaces the stored playlist set with the one given, in a single
        // transaction. Membership is rewritten wholesale per playlist rather
        // than diffed: a playlist is a short ordered list, and any edit can
        // change every position after it, so a diff would be more code to
        // produce the same rows.
        public void Save(IEnumerable<Playlist> playlists)
        {
            using var connection = db.Open();
            using var transaction = connection.BeginTransaction();

            var seen = new HashSet<Guid>();

            using (var upsert = connection.CreateCommand())
            using (var clear = connection.CreateCommand())
            using (var insert = connection.CreateCommand())
            {
                upsert.Transaction = transaction;
                upsert.CommandText = """
                    INSERT INTO playlists (id, name, updated_at)
                    VALUES ($id, $name, $updated_at)
                    ON CONFLICT (id) DO UPDATE SET
                        name = excluded.name,
                        updated_at = excluded.updated_at;
                    """;
                var upsertId = upsert.Parameters.Add("$id", SqliteType.Text);
                var upsertName = upsert.Parameters.Add("$name", SqliteType.Text);
                var upsertUpdatedAt = upsert.Parameters.Add("$updated_at", SqliteType.Integer);

                clear.Transaction = transaction;
                clear.CommandText = "DELETE FROM playlist_tracks WHERE playlist_id = $playlist_id;";
                var clearId = clear.Parameters.Add("$playlist_id", SqliteType.Text);

                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO playlist_tracks (playlist_id, position, track_id)
                    VALUES ($playlist_id, $position, $track_id);
                    """;
                var insertPlaylistId = insert.Parameters.Add("$playlist_id", SqliteType.Text);
                var insertPosition = insert.Parameters.Add("$position", SqliteType.Integer);
                var insertTrackId = insert.Parameters.Add("$track_id", SqliteType.Text);

                foreach (var playlist in playlists)
                {
                    if (!seen.Add(playlist.Id))
                        continue;

                    var id = playlist.Id.ToString("N");

                    upsertId.Value = id;
                    upsertName.Value = playlist.Name;
                    upsertUpdatedAt.Value = playlist.UpdatedAt.UtcTicks;
                    upsert.ExecuteNonQuery();

                    clearId.Value = id;
                    clear.ExecuteNonQuery();

                    // Every entry is written, including not-yet-downloaded ones
                    // (see SYNC-PLAN.md Phase 3). An earlier version of the JSON
                    // store filtered on Path != null here and silently dropped
                    // any synced track the moment the playlist was saved.
                    var position = 0;
                    foreach (var track in playlist.Tracks)
                    {
                        insertPlaylistId.Value = id;
                        insertPosition.Value = position++;
                        insertTrackId.Value = track.Id.ToString("N");
                        insert.ExecuteNonQuery();
                    }
                }
            }

            DeletePlaylistsNotIn(connection, transaction, seen);
            transaction.Commit();
        }

        private static void DeletePlaylistsNotIn(SqliteConnection connection, SqliteTransaction transaction, HashSet<Guid> keep)
        {
            var doomed = new List<string>();

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT id FROM playlists;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetString(0);
                    if (!keep.Contains(Guid.Parse(id)))
                        doomed.Add(id);
                }
            }

            if (doomed.Count == 0)
                return;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                // playlist_tracks cascades - see Schema.V1's foreign key.
                command.CommandText = "DELETE FROM playlists WHERE id = $id;";
                var parameter = command.Parameters.Add("$id", SqliteType.Text);
                foreach (var id in doomed)
                {
                    parameter.Value = id;
                    command.ExecuteNonQuery();
                }
            }
        }
    }
}
