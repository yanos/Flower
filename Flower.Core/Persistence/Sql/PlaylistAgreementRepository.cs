using System;
using System.Collections.Generic;
using System.Text.Json;

using Microsoft.Data.Sqlite;

using Flower.Models;
using Flower.Services;

namespace Flower.Persistence.Sql
{
    // What this device last agreed with a server about one playlist: the
    // server's revision and that version's content, or a null Copy for a
    // deletion it agreed to keep the playlist over ("Deleted on your server.
    // Keep it?"). "Did I change this?" is a comparison with Copy
    // (PlaylistVersion), so an edit and its undo are no change at all.
    public sealed record PlaylistAgreement(long Revision, PlaylistSyncPlaylistDto? Copy);

    // The agreed copies, per server and playlist, in flower.db beside the
    // playlists themselves - see docs/PLAYLIST-SYNC-PLAN.md, "The agreed copy".
    // A session saves them after installing what they describe, never before:
    // a crash between the two then leaves a playlist that already matches the
    // server against an older agreement, which the next session reads as the
    // same content and agrees on, instead of an agreement this device's
    // playlists do not match, which it would read as an edit to push.
    public sealed class PlaylistAgreementRepository(FlowerDb db)
    {
        public Dictionary<Guid, PlaylistAgreement> Load(string server)
        {
            using var connection = db.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT playlist_id, revision, copy FROM playlist_agreements WHERE server = $server;";
            command.Parameters.AddWithValue("$server", server);

            var result = new Dictionary<Guid, PlaylistAgreement>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                // An unreadable copy is no agreement: the next session reads
                // that playlist as one with no history in common, and agrees
                // if the content matches.
                PlaylistSyncPlaylistDto? copy = null;
                if (!reader.IsDBNull(2))
                {
                    try
                    {
                        copy = JsonSerializer.Deserialize(reader.GetString(2), PlaylistSyncJsonContext.Default.PlaylistSyncPlaylistDto);
                    }
                    catch (JsonException)
                    {
                        continue;
                    }
                    if (copy == null)
                        continue;
                }

                result[EntityId.FromKey(reader.GetString(0))] = new PlaylistAgreement(reader.GetInt64(1), copy);
            }

            return result;
        }

        // Writes the agreements named, and removes those named with null; the
        // rest stay as they were. One transaction.
        public void Save(string server, IReadOnlyDictionary<Guid, PlaylistAgreement?> changes)
        {
            if (changes.Count == 0)
                return;

            using var connection = db.Open();
            using var transaction = connection.BeginTransaction();

            using var upsert = connection.CreateCommand();
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT OR REPLACE INTO playlist_agreements (server, playlist_id, revision, copy)
                VALUES ($server, $id, $revision, $copy);
                """;
            upsert.Parameters.AddWithValue("$server", server);
            var upsertId = upsert.Parameters.Add("$id", SqliteType.Text);
            var revision = upsert.Parameters.Add("$revision", SqliteType.Integer);
            var copy = upsert.Parameters.Add("$copy", SqliteType.Text);

            using var remove = connection.CreateCommand();
            remove.Transaction = transaction;
            remove.CommandText = "DELETE FROM playlist_agreements WHERE server = $server AND playlist_id = $id;";
            remove.Parameters.AddWithValue("$server", server);
            var removeId = remove.Parameters.Add("$id", SqliteType.Text);

            foreach (var (id, agreement) in changes)
            {
                if (agreement == null)
                {
                    removeId.Value = id.ToKey();
                    remove.ExecuteNonQuery();
                    continue;
                }

                upsertId.Value = id.ToKey();
                revision.Value = agreement.Revision;
                copy.Value = agreement.Copy is { } held
                    ? JsonSerializer.Serialize(held, PlaylistSyncJsonContext.Default.PlaylistSyncPlaylistDto)
                    : DBNull.Value;
                upsert.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }
}
