using System;
using System.Collections.Generic;

using Microsoft.Data.Sqlite;

using Flower.Models;
using Flower.Services;

namespace Flower.Persistence.Sql;

// The songs skipped early in each Vibe Playlist's endless queue on this
// device (vibe_playlist_skips) - see VibeQueueSource.
public sealed class VibeSkipRepository(FlowerDb db) : IVibeSkipStore
{
    public IReadOnlySet<Guid> Load(Guid playlistId)
    {
        var skipped = new HashSet<Guid>();
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT track_id FROM vibe_playlist_skips WHERE playlist_id = $playlist_id;";
        command.Parameters.AddWithValue("$playlist_id", playlistId.ToKey());
        using var reader = command.ExecuteReader();
        while (reader.Read())
            skipped.Add(EntityId.FromKey(reader.GetString(0)));
        return skipped;
    }

    public void Record(Guid playlistId, Guid trackId)
    {
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO vibe_playlist_skips (playlist_id, track_id, skipped_at)
            VALUES ($playlist_id, $track_id, $skipped_at)
            ON CONFLICT (playlist_id, track_id) DO UPDATE SET skipped_at = excluded.skipped_at;
            """;
        command.Parameters.AddWithValue("$playlist_id", playlistId.ToKey());
        command.Parameters.AddWithValue("$track_id", trackId.ToKey());
        command.Parameters.AddWithValue("$skipped_at", DateTimeOffset.UtcNow.UtcTicks);
        command.ExecuteNonQuery();
    }

    // The skips of playlists this device no longer has.
    public void Prune(IEnumerable<Guid> playlists)
    {
        using var connection = db.Open();
        using var transaction = connection.BeginTransaction();
        using (var create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = "CREATE TEMP TABLE IF NOT EXISTS kept_playlists (id TEXT PRIMARY KEY); DELETE FROM kept_playlists;";
            create.ExecuteNonQuery();
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT OR IGNORE INTO kept_playlists (id) VALUES ($id);";
            var id = insert.Parameters.Add("$id", SqliteType.Text);
            foreach (var playlist in playlists)
            {
                id.Value = playlist.ToKey();
                insert.ExecuteNonQuery();
            }
        }

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM vibe_playlist_skips WHERE playlist_id NOT IN (SELECT id FROM kept_playlists);";
            delete.ExecuteNonQuery();
        }

        transaction.Commit();
    }
}
