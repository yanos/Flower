using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;
using Flower.Persistence.Sql;

using Microsoft.Data.Sqlite;

namespace Flower.SemanticAudio;

// What the analyzer has heard of a song, without the clips themselves.
public sealed record AnalysedSong(
    Guid TrackId,
    string Model,
    string FileStamp,
    TimeSpan Duration,
    int WindowCount,
    DateTimeOffset AnalysedAt,
    string? Failure,
    IReadOnlySet<int> Windows)
{
    public bool Failed => Failure != null;
}

// semantic_audio_songs and semantic_audio_clips (docs/VIBE-PLAYLIST-PLAN.md,
// "Storage"; the tables are SqliteMigrations' step 14). Every clip ever
// analysed is kept until its song goes stale, leaves the library, or the
// master switch is turned off - the clip setting only chooses among them.
public sealed class SemanticAudioRepository(FlowerDb db)
{
    public Dictionary<Guid, AnalysedSong> LoadSongs()
    {
        using var connection = db.Open();

        var windows = new Dictionary<Guid, HashSet<int>>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT track_id, window_index FROM semantic_audio_clips;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = EntityId.FromKey(reader.GetString(0));
                if (!windows.TryGetValue(id, out var set))
                    windows[id] = set = [];
                set.Add(reader.GetInt32(1));
            }
        }

        var songs = new Dictionary<Guid, AnalysedSong>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT track_id, model, file_stamp, duration_ticks, window_count, analysed_at, failure FROM semantic_audio_songs;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = EntityId.FromKey(reader.GetString(0));
                songs[id] = new AnalysedSong(
                    id,
                    reader.GetString(1),
                    reader.GetString(2),
                    TimeSpan.FromTicks(reader.GetInt64(3)),
                    reader.GetInt32(4),
                    new DateTimeOffset(reader.GetInt64(5), TimeSpan.Zero),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    windows.TryGetValue(id, out var set) ? set : new HashSet<int>());
            }
        }

        return songs;
    }

    // Writes one song's row and the clips just analysed, in one transaction,
    // so a restart loses at most the song in progress. `replace` drops the
    // clips already stored first: the song went stale, and they describe audio
    // or a model that is gone.
    public void SaveSong(AnalysedSong song, IReadOnlyDictionary<int, float[]> newClips, bool replace)
    {
        using var connection = db.Open();
        using var transaction = connection.BeginTransaction();
        var key = song.TrackId.ToKey();

        if (replace)
        {
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM semantic_audio_clips WHERE track_id = $id;";
            delete.Parameters.AddWithValue("$id", key);
            delete.ExecuteNonQuery();
        }

        using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO semantic_audio_songs (track_id, model, file_stamp, duration_ticks, window_count, analysed_at, failure)
                VALUES ($id, $model, $stamp, $duration, $windows, $at, $failure)
                ON CONFLICT (track_id) DO UPDATE SET
                    model = excluded.model, file_stamp = excluded.file_stamp, duration_ticks = excluded.duration_ticks,
                    window_count = excluded.window_count, analysed_at = excluded.analysed_at, failure = excluded.failure;
                """;
            upsert.Parameters.AddWithValue("$id", key);
            upsert.Parameters.AddWithValue("$model", song.Model);
            upsert.Parameters.AddWithValue("$stamp", song.FileStamp);
            upsert.Parameters.AddWithValue("$duration", song.Duration.Ticks);
            upsert.Parameters.AddWithValue("$windows", song.WindowCount);
            upsert.Parameters.AddWithValue("$at", song.AnalysedAt.UtcTicks);
            upsert.Parameters.AddWithValue("$failure", (object?)song.Failure ?? DBNull.Value);
            upsert.ExecuteNonQuery();
        }

        if (newClips.Count > 0)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT OR REPLACE INTO semantic_audio_clips (track_id, window_index, embedding) VALUES ($id, $window, $embedding);";
            var id = insert.Parameters.Add("$id", SqliteType.Text);
            var window = insert.Parameters.Add("$window", SqliteType.Integer);
            var embedding = insert.Parameters.Add("$embedding", SqliteType.Blob);
            foreach (var (index, vector) in newClips)
            {
                id.Value = key;
                window.Value = index;
                embedding.Value = Embedding.ToStored(vector);
                insert.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    // Every stored clip, one song at a time: what the index is built from.
    public void ForEachSongClips(Action<Guid, IReadOnlyDictionary<int, float[]>> visit)
    {
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT track_id, window_index, embedding FROM semantic_audio_clips ORDER BY track_id;";
        using var reader = command.ExecuteReader();

        Guid? current = null;
        var clips = new Dictionary<int, float[]>();
        while (reader.Read())
        {
            var id = EntityId.FromKey(reader.GetString(0));
            if (current != id && current is { } previous)
            {
                visit(previous, clips);
                clips = new Dictionary<int, float[]>();
            }

            current = id;
            clips[reader.GetInt32(1)] = Embedding.FromStored((byte[])reader.GetValue(2));
        }

        if (current is { } last)
            visit(last, clips);
    }

    // The clips of a few songs, for reranking a search's candidates.
    public Dictionary<Guid, Dictionary<int, float[]>> LoadClips(IReadOnlyCollection<Guid> trackIds)
    {
        var result = new Dictionary<Guid, Dictionary<int, float[]>>(trackIds.Count);
        if (trackIds.Count == 0)
            return result;

        using var connection = db.Open();
        foreach (var batch in trackIds.Chunk(500))
        {
            using var command = connection.CreateCommand();
            var names = new string[batch.Length];
            for (var i = 0; i < batch.Length; i++)
            {
                names[i] = "$t" + i;
                command.Parameters.AddWithValue(names[i], batch[i].ToKey());
            }

            command.CommandText = $"SELECT track_id, window_index, embedding FROM semantic_audio_clips WHERE track_id IN ({string.Join(", ", names)});";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = EntityId.FromKey(reader.GetString(0));
                if (!result.TryGetValue(id, out var clips))
                    result[id] = clips = [];
                clips[reader.GetInt32(1)] = Embedding.FromStored((byte[])reader.GetValue(2));
            }
        }

        return result;
    }

    public void Delete(IReadOnlyCollection<Guid> trackIds)
    {
        if (trackIds.Count == 0)
            return;

        using var connection = db.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM semantic_audio_songs WHERE track_id = $id;";
        var id = command.Parameters.Add("$id", SqliteType.Text);
        foreach (var trackId in trackIds)
        {
            id.Value = trackId.ToKey();
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    // The master switch going off.
    public void DeleteAll()
    {
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM semantic_audio_clips; DELETE FROM semantic_audio_songs;";
        command.ExecuteNonQuery();
    }

    // The songs the analyzer couldn't hear, with why, for the settings page's
    // "12 songs couldn't be analysed".
    public List<(Guid TrackId, string Reason)> LoadFailures()
    {
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT track_id, failure FROM semantic_audio_songs WHERE failure IS NOT NULL;";
        using var reader = command.ExecuteReader();
        var failures = new List<(Guid, string)>();
        while (reader.Read())
            failures.Add((EntityId.FromKey(reader.GetString(0)), reader.GetString(1)));
        return failures;
    }

    public long ClipCount()
    {
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM semantic_audio_clips;";
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
