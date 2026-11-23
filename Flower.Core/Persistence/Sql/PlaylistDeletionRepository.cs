using System;
using System.Collections.Generic;

using Microsoft.Data.Sqlite;

using Flower.Models;
using Flower.Services;

namespace Flower.Persistence.Sql
{
    // A server's record of the playlists deleted from it, per listener - see
    // IPlaylistDeletionLog. In flower.db, so a backup carries the record as it
    // stood when the backup was taken.
    public sealed class PlaylistDeletionRepository(FlowerDb db) : IPlaylistDeletionLog
    {
        public IReadOnlySet<Guid> DeletedFor(string listener)
        {
            using var connection = db.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT playlist_id FROM deleted_playlists WHERE listener = $listener;";
            command.Parameters.AddWithValue("$listener", listener);

            var ids = new HashSet<Guid>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                ids.Add(EntityId.FromKey(reader.GetString(0)));
            return ids;
        }

        public void Record(string listener, IReadOnlyCollection<Guid> playlistIds)
        {
            if (playlistIds.Count > 0)
                Write(listener, playlistIds, """
                    INSERT INTO deleted_playlists (listener, playlist_id, deleted_at)
                    VALUES ($listener, $id, $now)
                    ON CONFLICT (listener, playlist_id) DO UPDATE SET deleted_at = excluded.deleted_at;
                    """);
        }

        public void Forget(string listener, IReadOnlyCollection<Guid> playlistIds)
        {
            if (playlistIds.Count > 0)
                Write(listener, playlistIds, "DELETE FROM deleted_playlists WHERE listener = $listener AND playlist_id = $id;");
        }

        private void Write(string listener, IReadOnlyCollection<Guid> playlistIds, string sql)
        {
            using var connection = db.Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("$listener", listener);
            if (sql.Contains("$now", StringComparison.Ordinal))
                command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.UtcTicks);
            var id = command.Parameters.Add("$id", SqliteType.Text);
            foreach (var playlistId in playlistIds)
            {
                id.Value = playlistId.ToKey();
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }
}
