using System;
using System.Collections.Generic;

using Microsoft.Data.Sqlite;

using Flower.Models;
using Flower.Services;

namespace Flower.Persistence.Sql
{
    // Reading and writing playlists. Like the JSON store this replaces,
    // playlists hold track *references* only - membership is stored as track
    // ids and resolved against the library on load, never as duplicated track
    // metadata.
    public sealed class PlaylistRepository(FlowerDb db) : IPlaylistStore
    {
        public List<Playlist> Load(IReadOnlyList<Track> libraryTracks)
        {
            var byId = new Dictionary<Guid, Track>(libraryTracks.Count);
            foreach (var track in libraryTracks)
                byId.TryAdd(track.Id, track);

            using var connection = db.Open();

            var playlists = new List<PlaylistRow>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT id, name, updated_at, comment, is_public, created_at, rules FROM playlists;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    playlists.Add(new PlaylistRow(
                        EntityId.FromKey(reader.GetString(0)),
                        reader.GetString(1),
                        new DateTimeOffset(reader.GetInt64(2), TimeSpan.Zero),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        reader.GetInt64(4) != 0,
                        new DateTimeOffset(reader.GetInt64(5), TimeSpan.Zero),
                        // Null for every ordinary playlist, which is all of
                        // them until a smart one is created. An unreadable blob
                        // also reads as null - see SmartPlaylistRulesJson.Read.
                        SmartPlaylistRulesJson.Read(reader.IsDBNull(6) ? null : reader.GetString(6))));
                }
            }

            var membership = new Dictionary<Guid, List<Track>>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT playlist_id, track_id FROM playlist_tracks ORDER BY playlist_id, position;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var playlistId = EntityId.FromKey(reader.GetString(0));

                    // An entry whose id doesn't resolve is dropped - by then the
                    // track really is gone from the library. Same rule the JSON
                    // store applied, and the same one Flower.Server's
                    // SubsonicMapper applies to its own playlist entries.
                    if (!byId.TryGetValue(EntityId.FromKey(reader.GetString(1)), out var track))
                        continue;

                    if (!membership.TryGetValue(playlistId, out var tracks))
                        membership[playlistId] = tracks = [];

                    tracks.Add(track);
                }
            }

            var result = new List<Playlist>(playlists.Count);
            foreach (var row in playlists)
            {
                // A smart playlist loads with its last materialized contents,
                // exactly like an ordinary one - the rules are re-evaluated by
                // the recomputation pass, not by the load, so the app has a
                // populated sidebar before any of that runs.
                result.Add(new Playlist(
                    row.Id,
                    row.Name,
                    membership.TryGetValue(row.Id, out var tracks) ? tracks : [],
                    row.UpdatedAt,
                    row.Comment,
                    row.IsPublic,
                    row.CreatedAt,
                    row.Rules));
            }

            return result;
        }

        private readonly record struct PlaylistRow(
            Guid Id,
            string Name,
            DateTimeOffset UpdatedAt,
            string? Comment,
            bool IsPublic,
            DateTimeOffset CreatedAt,
            SmartPlaylistRules? Rules);

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
                    INSERT INTO playlists (id, name, updated_at, comment, is_public, created_at, rules)
                    VALUES ($id, $name, $updated_at, $comment, $is_public, $created_at, $rules)
                    ON CONFLICT (id) DO UPDATE SET
                        name = excluded.name,
                        updated_at = excluded.updated_at,
                        comment = excluded.comment,
                        is_public = excluded.is_public,
                        rules = excluded.rules;
                    -- created_at is not in the DO UPDATE: it is set once, when
                    -- the row is first written, and Playlist has no way to
                    -- change it afterwards.
                    --
                    -- comment and is_public are, now that Playlist carries
                    -- them. They used to be insert-only for exactly the
                    -- opposite reason: with no field to load them into, a
                    -- client save would have written back a default and
                    -- silently reset whatever the server had set.
                    """;
                var upsertId = upsert.Parameters.Add("$id", SqliteType.Text);
                var upsertName = upsert.Parameters.Add("$name", SqliteType.Text);
                var upsertUpdatedAt = upsert.Parameters.Add("$updated_at", SqliteType.Integer);
                var upsertComment = upsert.Parameters.Add("$comment", SqliteType.Text);
                var upsertIsPublic = upsert.Parameters.Add("$is_public", SqliteType.Integer);
                var upsertCreatedAt = upsert.Parameters.Add("$created_at", SqliteType.Integer);
                var upsertRules = upsert.Parameters.Add("$rules", SqliteType.Text);

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

                    var id = playlist.Id.ToKey();

                    upsertId.Value = id;
                    upsertName.Value = playlist.Name;
                    upsertUpdatedAt.Value = playlist.UpdatedAt.UtcTicks;
                    upsertComment.Value = (object?)playlist.Comment ?? DBNull.Value;
                    upsertIsPublic.Value = playlist.IsPublic ? 1 : 0;
                    upsertCreatedAt.Value = playlist.CreatedAt.UtcTicks;
                    // In the DO UPDATE, unlike created_at: converting a smart
                    // playlist back to an ordinary one is clearing the rules,
                    // and that has to be able to reach the row.
                    upsertRules.Value = playlist.Rules is { } rules
                        ? SmartPlaylistRulesJson.Write(rules)
                        : (object)DBNull.Value;
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
                        insertTrackId.Value = track.Id.ToKey();
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
                    if (!keep.Contains(EntityId.FromKey(id)))
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
