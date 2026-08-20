using Microsoft.Data.Sqlite;

using Flower.Persistence.Sql;

namespace Flower.Server.Data;

// Playlist reads and writes for the Subsonic surface, over the same shared
// `playlists`/`playlist_tracks` tables the client uses.
//
// Separate from Flower.Core's PlaylistRepository rather than layered on it,
// because the two want different things out of the same rows: the client
// resolves membership into live Track object references it can hand the play
// queue, while the server only ever needs the ordered ids (and then looks up
// exactly those tracks). It also owns the three Subsonic-only columns
// Schema.V1 carries - comment, is_public, created_at - which the client's
// Playlist model has no field for.
//
// Membership is stored as ids and resolved on read, never as duplicated track
// metadata; an entry whose id no longer resolves is skipped rather than
// enforced by a foreign key, so a rescan dropping a deleted file does not have
// to cascade through every playlist that referenced it.
public sealed class PlaylistQueries(FlowerDb db)
{
    public List<PlaylistRow> All()
    {
        using var connection = db.Open();
        return ReadPlaylists(connection, id: null);
    }

    public PlaylistRow? Find(string id)
    {
        if (!Guid.TryParse(id, out var parsed))
            return null;

        using var connection = db.Open();
        return ReadPlaylists(connection, parsed.ToString("N")).FirstOrDefault();
    }

    public string Create(string name, IReadOnlyList<Guid> trackIds)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.UtcTicks;

        using var connection = db.Open();
        using var transaction = connection.BeginTransaction();

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO playlists (id, name, updated_at, created_at, is_public)
                VALUES ($id, $name, $now, $now, 0);
                """;
            insert.Parameters.AddWithValue("$id", id.ToString("N"));
            insert.Parameters.AddWithValue("$name", name);
            insert.Parameters.AddWithValue("$now", now);
            insert.ExecuteNonQuery();
        }

        WriteMembership(connection, transaction, id, trackIds);
        transaction.Commit();
        return id.ToString("N");
    }

    // Returns the current ordered membership, or null if no such playlist -
    // so the caller can apply Subsonic's index-based removals and appends
    // against it without a second round trip.
    public IReadOnlyList<Guid>? Membership(string id)
    {
        var playlist = Find(id);
        return playlist?.TrackIds;
    }

    public bool Update(
        string id,
        string? name,
        string? comment,
        bool? isPublic,
        IReadOnlyList<Guid> trackIds)
    {
        if (!Guid.TryParse(id, out var parsed))
            return false;

        using var connection = db.Open();
        using var transaction = connection.BeginTransaction();

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            // COALESCE rather than a dynamically-assembled SET list: Subsonic's
            // updatePlaylist sends only the attributes being changed, and an
            // absent one must leave the stored value alone.
            update.CommandText = """
                UPDATE playlists
                   SET name       = COALESCE($name, name),
                       comment    = COALESCE($comment, comment),
                       is_public  = COALESCE($is_public, is_public),
                       updated_at = $now
                 WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$name", (object?)name ?? DBNull.Value);
            update.Parameters.AddWithValue("$comment", (object?)comment ?? DBNull.Value);
            update.Parameters.AddWithValue("$is_public", isPublic is null ? DBNull.Value : isPublic.Value ? 1 : 0);
            update.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.UtcTicks);
            update.Parameters.AddWithValue("$id", parsed.ToString("N"));

            if (update.ExecuteNonQuery() == 0)
                return false;
        }

        WriteMembership(connection, transaction, parsed, trackIds);
        transaction.Commit();
        return true;
    }

    public bool Delete(string id)
    {
        if (!Guid.TryParse(id, out var parsed))
            return false;

        using var connection = db.Open();
        using var command = connection.CreateCommand();
        // playlist_tracks cascades - see Schema.V1's foreign key, and note that
        // FlowerDb.Open turns foreign keys on, which SQLite leaves off by
        // default per connection.
        command.CommandText = "DELETE FROM playlists WHERE id = $id;";
        command.Parameters.AddWithValue("$id", parsed.ToString("N"));
        return command.ExecuteNonQuery() > 0;
    }

    // Rewritten wholesale rather than diffed: a playlist is a short ordered
    // list and any edit can shift every position after it, so a diff would be
    // more code to produce the same rows. Same choice as PlaylistRepository.
    private static void WriteMembership(
        SqliteConnection connection, SqliteTransaction transaction, Guid playlistId, IReadOnlyList<Guid> trackIds)
    {
        var id = playlistId.ToString("N");

        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM playlist_tracks WHERE playlist_id = $playlist_id;";
            clear.Parameters.AddWithValue("$playlist_id", id);
            clear.ExecuteNonQuery();
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO playlist_tracks (playlist_id, position, track_id)
            VALUES ($playlist_id, $position, $track_id);
            """;
        var insertPlaylistId = insert.Parameters.Add("$playlist_id", SqliteType.Text);
        var insertPosition = insert.Parameters.Add("$position", SqliteType.Integer);
        var insertTrackId = insert.Parameters.Add("$track_id", SqliteType.Text);
        insert.Prepare();

        for (var position = 0; position < trackIds.Count; position++)
        {
            insertPlaylistId.Value = id;
            insertPosition.Value = position;
            insertTrackId.Value = trackIds[position].ToString("N");
            insert.ExecuteNonQuery();
        }
    }

    private static List<PlaylistRow> ReadPlaylists(SqliteConnection connection, string? id)
    {
        var rows = new List<PlaylistRow>();
        var membership = new Dictionary<Guid, List<Guid>>();

        using (var command = connection.CreateCommand())
        {
            var where = id is null ? "" : "WHERE id = $id";
            command.CommandText = $"SELECT id, name, comment, is_public FROM playlists {where};";
            if (id is not null)
                command.Parameters.AddWithValue("$id", id);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new PlaylistRow(
                    Id: reader.GetString(0),
                    Name: reader.GetString(1),
                    Comment: reader.IsDBNull(2) ? null : reader.GetString(2),
                    IsPublic: reader.GetInt64(3) != 0,
                    TrackIds: []));
            }
        }

        if (rows.Count == 0)
            return rows;

        using (var command = connection.CreateCommand())
        {
            var where = id is null ? "" : "WHERE playlist_id = $id";
            command.CommandText = $"SELECT playlist_id, track_id FROM playlist_tracks {where} ORDER BY playlist_id, position;";
            if (id is not null)
                command.Parameters.AddWithValue("$id", id);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var playlistId = Guid.Parse(reader.GetString(0));
                if (!membership.TryGetValue(playlistId, out var ids))
                    membership[playlistId] = ids = [];

                ids.Add(Guid.Parse(reader.GetString(1)));
            }
        }

        for (var i = 0; i < rows.Count; i++)
        {
            if (membership.TryGetValue(Guid.Parse(rows[i].Id), out var ids))
                rows[i] = rows[i] with { TrackIds = ids };
        }

        return rows;
    }
}

public sealed record PlaylistRow(
    string Id,
    string Name,
    string? Comment,
    bool IsPublic,
    IReadOnlyList<Guid> TrackIds);
