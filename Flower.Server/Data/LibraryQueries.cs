using Microsoft.Data.Sqlite;

using Flower.Models;
using Flower.Persistence.Sql;

namespace Flower.Server.Data;

// Every read and write the Subsonic surface makes against the shared `tracks`
// table, as raw SQL over Flower.Core's FlowerDb.
//
// This is what replaced FlowerDbContext/TrackEntity when the server moved onto
// the shared schema (ARCHITECTURE-REVIEW Tier 4.1). The split is deliberate:
// the *schema*, the migration runner and the row mapper are shared with the
// client (Flower.Core/Persistence/Sql/), because those are what must not
// diverge; the *queries* are not, because the server's are aggregate ones a
// client never issues - one page of albums grouped and ordered SQL-side rather
// than the whole library materialized. TrackRepository.LoadAll cannot serve
// those, and pretending otherwise would mean pulling 16k tracks into memory
// per browse request, which is exactly what Tier 1.3 removed.
//
// Columns and ReadTrack come from TrackRepository rather than being restated
// here, so a schema change lands in one place and both sides pick it up.
//
// Synchronous on purpose. Microsoft.Data.Sqlite's *Async methods are wrappers
// over the same blocking native calls - there is no async SQLite I/O to await -
// so an async signature here would only advertise a concurrency property this
// does not have.
public sealed class LibraryQueries(FlowerDb db)
{
    // The per-album scalars come from MIN() rather than "whichever row came
    // back first": for a well-formed album every track carries the same
    // album/album_artist anyway, so the value is identical, and where tracks
    // genuinely disagree (a per-track genre or year on a compilation) MIN is at
    // least deterministic, which an unordered SQL result's first row never was.
    private const string AlbumSummarySelect = """
        SELECT album_id,
               MIN(album)        AS album,
               MIN(album_artist) AS album_artist,
               MIN(artist_id)    AS artist_id,
               COUNT(*)          AS song_count,
               SUM(duration_ticks) AS total_ticks,
               MIN(year)         AS year,
               MIN(genre)        AS genre
          FROM tracks
        """;

    public List<AlbumSummary> AlbumSummaries(string type, int take, int offset)
    {
        // "newest" orders albums by their most recent date_added. Under EF this
        // was the one sort that had to fall back to aggregating in memory,
        // because the provider refuses MAX() over a DateTimeOffset - the column
        // was TEXT. Schema.V1 stores timestamps as INTEGER ticks precisely so
        // that ordering is an integer comparison, and the fallback is gone.
        var order = type switch
        {
            "newest" => "ORDER BY MAX(date_added) DESC",
            "alphabeticalByArtist" => "ORDER BY MIN(album_artist) COLLATE NOCASE",
            // ORDER BY RANDOM() server-side rather than shuffling in memory,
            // which would mean reading every album back just to discard most.
            "random" => "ORDER BY RANDOM()",
            _ => "ORDER BY MIN(album) COLLATE NOCASE",
        };

        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"{AlbumSummarySelect} GROUP BY album_id {order} LIMIT $take OFFSET $offset;";
        command.Parameters.AddWithValue("$take", take);
        command.Parameters.AddWithValue("$offset", offset);
        return ReadAlbumSummaries(command);
    }

    public List<AlbumSummary> SearchAlbums(string query, int limit)
    {
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            {AlbumSummarySelect}
             WHERE album IS NOT NULL AND album LIKE $pattern ESCAPE '\'
             GROUP BY album_id
             ORDER BY MIN(album) COLLATE NOCASE
             LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$pattern", Contains(query));
        command.Parameters.AddWithValue("$limit", limit);
        return ReadAlbumSummaries(command);
    }

    private static List<AlbumSummary> ReadAlbumSummaries(SqliteCommand command)
    {
        var summaries = new List<AlbumSummary>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            summaries.Add(new AlbumSummary(
                AlbumId: reader.GetString(0),
                Album: reader.IsDBNull(1) ? null : reader.GetString(1),
                AlbumArtist: reader.IsDBNull(2) ? null : reader.GetString(2),
                ArtistId: reader.IsDBNull(3) ? null : reader.GetString(3),
                SongCount: reader.GetInt32(4),
                TotalDuration: TimeSpan.FromTicks(reader.GetInt64(5)),
                Year: ParseYear(reader.IsDBNull(6) ? null : reader.GetString(6)),
                Genre: reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return summaries;
    }

    // One row per distinct (artist, album) pair, collapsed SQL-side, rather
    // than a projection of every track in the library. getArtists needs a count
    // of distinct albums per artist, so grouping the duplicates away first
    // means reading roughly one row per album (~1.4k at the target scale)
    // instead of one per track (~16k), and the album_id index gets used.
    // Counting the pairs per artist is then trivial in memory.
    public List<ArtistAlbumPair> ArtistAlbumPairs(string? matching = null)
    {
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        var where = matching is null ? "" : """WHERE album_artist LIKE $pattern ESCAPE '\'""";
        command.CommandText = $"""
            SELECT artist_id, album_id, MIN(album_artist)
              FROM tracks
              {where}
             GROUP BY artist_id, album_id;
            """;
        if (matching is not null)
            command.Parameters.AddWithValue("$pattern", Contains(matching));

        var pairs = new List<ArtistAlbumPair>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            pairs.Add(new ArtistAlbumPair(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return pairs;
    }

    public List<Track> TracksByArtist(string artistId) =>
        Query("WHERE artist_id = $value ORDER BY album COLLATE NOCASE, disc_number, track_number", artistId);

    public List<Track> TracksByAlbum(string albumId) =>
        Query("WHERE album_id = $value ORDER BY disc_number, track_number", albumId);

    public List<Track> SearchSongs(string query, int limit)
    {
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {TrackRepository.Columns} FROM tracks
             WHERE title IS NOT NULL AND title LIKE $pattern ESCAPE '\'
             LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$pattern", Contains(query));
        command.Parameters.AddWithValue("$limit", limit);
        return ReadTracks(command);
    }

    public Track? Find(string id)
    {
        // Guid-shaped ids only: the id column holds 32-char hex, and a caller
        // is free to send anything. Parsing first keeps a junk id a clean
        // "not found" instead of a query that can never match.
        if (!Guid.TryParse(id, out var parsed))
            return null;

        return Query("WHERE id = $value", parsed.ToString("N")).FirstOrDefault();
    }

    // Returns how many rows were affected, so a caller can tell "starred
    // nothing" (a bad id) from a real change.
    public int SetStarred(string column, string value, bool starred)
    {
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        // The column name is interpolated, not bound - SQLite cannot bind an
        // identifier. It is never user input: callers pass one of the three
        // constants below, chosen by which query parameter was present.
        command.CommandText = $"UPDATE tracks SET starred = $starred, starred_at = $starred_at WHERE {column} = $value;";
        command.Parameters.AddWithValue("$starred", starred ? 1 : 0);
        command.Parameters.AddWithValue("$starred_at", starred ? DateTimeOffset.UtcNow.UtcTicks : (object)DBNull.Value);
        command.Parameters.AddWithValue("$value", value);
        return command.ExecuteNonQuery();
    }

    public const string IdColumn = "id";
    public const string AlbumIdColumn = "album_id";
    public const string ArtistIdColumn = "artist_id";

    // A scrobble is one UPDATE of one row - the same single-row-write shape
    // Tier 4.1 gave the client's own play-count bump, rather than loading the
    // track, mutating it and writing all 45 columns back.
    public void IncrementPlayCount(string id)
    {
        if (!Guid.TryParse(id, out var parsed))
            return;

        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE tracks SET play_count = play_count + 1, last_played_at = $now WHERE id = $id;";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.UtcTicks);
        command.Parameters.AddWithValue("$id", parsed.ToString("N"));
        command.ExecuteNonQuery();
    }

    public Dictionary<Guid, Track> ByIds(IReadOnlyCollection<Guid> ids)
    {
        var byId = new Dictionary<Guid, Track>(ids.Count);
        if (ids.Count == 0)
            return byId;

        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {TrackRepository.Columns} FROM tracks WHERE id = $value;";
        var parameter = command.Parameters.Add("$value", SqliteType.Text);

        // One prepared lookup per id rather than an IN clause: SQLite cannot
        // bind a set, and a playlist can hold more ids than
        // SQLITE_MAX_VARIABLE_NUMBER allows literals for. Same reasoning as
        // TrackRepository.DeleteTracksNotIn.
        command.Prepare();
        foreach (var id in ids)
        {
            parameter.Value = id.ToString("N");
            foreach (var track in ReadTracks(command))
                byId[track.Id] = track;
        }

        return byId;
    }

    private List<Track> Query(string clause, string value)
    {
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {TrackRepository.Columns} FROM tracks {clause};";
        command.Parameters.AddWithValue("$value", value);
        return ReadTracks(command);
    }

    // Remote play counts (TrackRepository's second pass) are deliberately not
    // loaded: they exist for peer-to-peer sync between two Flower clients, and
    // nothing on the Subsonic surface reports them.
    private static List<Track> ReadTracks(SqliteCommand command)
    {
        var tracks = new List<Track>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            tracks.Add(TrackRepository.ReadTrack(reader));

        return tracks;
    }

    // LIKE's own wildcards in a user's query would otherwise be honoured: a
    // search for "50%" matched every title starting "50", and "_" matched any
    // character. Escaped here, with an explicit ESCAPE clause at each call
    // site - SQLite has no default escape character. The EF version had the
    // same hole via EF.Functions.Like.
    private static string Contains(string query) =>
        "%" + query.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    private static int? ParseYear(string? year) => int.TryParse(year, out var parsed) ? parsed : null;
}

// One album's worth of pre-aggregated columns, as computed by SQL rather than
// by grouping materialized tracks in memory.
public sealed record AlbumSummary(
    string AlbumId,
    string? Album,
    string? AlbumArtist,
    string? ArtistId,
    int SongCount,
    TimeSpan TotalDuration,
    int? Year,
    string? Genre);

public sealed record ArtistAlbumPair(string ArtistId, string AlbumId, string? Name);
