using System;
using System.Collections.Generic;

using Microsoft.Data.Sqlite;

using Flower.Models;
using Flower.Services;

namespace Flower.Persistence.Sql
{
    // A server's playlist revisions, deletions included - see
    // PlaylistRevisions. In flower.db, so a backup carries them as they stood
    // when it was taken.
    public sealed class PlaylistRevisionRepository(FlowerDb db) : IPlaylistRevisionStore
    {
        public Dictionary<(string Listener, Guid Id), List<PlaylistRevision>> Load()
        {
            using var connection = db.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT listener, playlist_id, revision, deleted, version FROM playlist_revisions ORDER BY listener, playlist_id, revision;";

            var result = new Dictionary<(string, Guid), List<PlaylistRevision>>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var key = (reader.GetString(0), EntityId.FromKey(reader.GetString(1)));
                if (!result.TryGetValue(key, out var revisions))
                    result[key] = revisions = [];
                revisions.Add(new PlaylistRevision(
                    reader.GetInt64(2), reader.GetInt64(3) != 0, reader.IsDBNull(4) ? null : reader.GetString(4)));
            }

            return result;
        }

        public void Append(IReadOnlyList<(string Listener, Guid Id, PlaylistRevision Revision)> issued, int keep)
        {
            using var connection = db.Open();
            using var transaction = connection.BeginTransaction();

            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR REPLACE INTO playlist_revisions (listener, playlist_id, revision, deleted, version)
                VALUES ($listener, $id, $revision, $deleted, $version);
                """;
            var listener = insert.Parameters.Add("$listener", SqliteType.Text);
            var id = insert.Parameters.Add("$id", SqliteType.Text);
            var revision = insert.Parameters.Add("$revision", SqliteType.Integer);
            var deleted = insert.Parameters.Add("$deleted", SqliteType.Integer);
            var version = insert.Parameters.Add("$version", SqliteType.Text);

            using var trim = connection.CreateCommand();
            trim.Transaction = transaction;
            trim.CommandText = """
                DELETE FROM playlist_revisions
                WHERE listener = $listener AND playlist_id = $id AND revision NOT IN (
                    SELECT revision FROM playlist_revisions
                    WHERE listener = $listener AND playlist_id = $id
                    ORDER BY revision DESC LIMIT $keep);
                """;
            var trimListener = trim.Parameters.Add("$listener", SqliteType.Text);
            var trimId = trim.Parameters.Add("$id", SqliteType.Text);
            trim.Parameters.AddWithValue("$keep", keep);

            var touched = new HashSet<(string, Guid)>();
            foreach (var entry in issued)
            {
                listener.Value = entry.Listener;
                id.Value = entry.Id.ToKey();
                revision.Value = entry.Revision.Revision;
                deleted.Value = entry.Revision.Deleted ? 1 : 0;
                version.Value = (object?)entry.Revision.Version ?? DBNull.Value;
                insert.ExecuteNonQuery();
                touched.Add((entry.Listener, entry.Id));
            }

            foreach (var (touchedListener, touchedId) in touched)
            {
                trimListener.Value = touchedListener;
                trimId.Value = touchedId.ToKey();
                trim.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }
}
