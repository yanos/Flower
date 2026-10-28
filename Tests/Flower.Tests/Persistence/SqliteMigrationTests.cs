using System;
using System.Collections.Generic;
using System.IO;

using Microsoft.Data.Sqlite;

using Flower.Persistence.Sql;

using Xunit;

namespace Flower.Tests.Persistence;

// The schema runner: a fresh database is built from the baseline, a database
// that is current but short of a column folded in since gets the column, and a
// database from before the baseline is refused rather than half-read.
public class SqliteMigrationTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "flower-migrations-" + Guid.NewGuid().ToString("N"));

    public SqliteMigrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A pooled handle outliving the test is not what is under test.
        }
    }

    private SqliteConnection OpenNew(string name)
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, name)}");
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static List<string> ColumnsOf(SqliteConnection connection, string table)
    {
        var columns = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            columns.Add(reader.GetString(1));

        return columns;
    }

    [Fact]
    public void A_fresh_database_is_created_at_the_latest_version()
    {
        using var connection = OpenNew("fresh.db");

        SqliteMigrations.Apply(connection);

        Assert.Equal(SqliteMigrations.LatestVersion, SqliteMigrations.ReadVersion(connection));
        Assert.Contains("origin_album_art_id", ColumnsOf(connection, "tracks"));
        Assert.Contains("listener", ColumnsOf(connection, "playlists"));
        Assert.NotEmpty(ColumnsOf(connection, "removed_tracks"));
        Assert.NotEmpty(ColumnsOf(connection, "excluded_paths"));
    }

    // A column added by editing the baseline in place reaches a fresh database and no
    // existing one, while user_version says there is nothing to do. On a phone
    // that was album_artist - LibraryStore caught the "no such column", logged
    // it, and started with an empty library, and every save after it failed the
    // same way. An empty library reads as a sync fault, so the search starts in
    // the wrong place entirely.
    //
    // Simulated by dropping the column back out of an up-to-date database,
    // which is exactly the shape the phone was in: latest version, missing a
    // column the baseline grew later.
    [Fact]
    public void A_column_folded_into_the_baseline_reaches_a_database_that_already_existed()
    {
        using var connection = OpenNew("folded.db");
        SqliteMigrations.Apply(connection);
        Execute(connection, "ALTER TABLE tracks DROP COLUMN album_artist;");
        Assert.DoesNotContain("album_artist", ColumnsOf(connection, "tracks"));

        SqliteMigrations.Apply(connection);

        Assert.Contains("album_artist", ColumnsOf(connection, "tracks"));
        Assert.Equal(SqliteMigrations.LatestVersion, SqliteMigrations.ReadVersion(connection));
    }

    // Every table, not only tracks.
    [Fact]
    public void Columns_folded_into_any_table_reach_a_database_that_already_existed()
    {
        using var connection = OpenNew("other-tables.db");
        SqliteMigrations.Apply(connection);
        Execute(connection, "ALTER TABLE removed_tracks DROP COLUMN deliberate;");
        Execute(connection, "ALTER TABLE tracks DROP COLUMN moved_from_path;");
        Execute(connection, "ALTER TABLE tracks DROP COLUMN origin_file_stamp;");
        Execute(connection, "ALTER TABLE tracks DROP COLUMN tags_edited_at;");
        Execute(connection, "ALTER TABLE tracks DROP COLUMN art_edited_at;");
        Execute(connection, "ALTER TABLE tracks DROP COLUMN file_replaced_at;");

        SqliteMigrations.Apply(connection);

        Assert.Contains("tags_edited_at", ColumnsOf(connection, "tracks"));
        Assert.Contains("art_edited_at", ColumnsOf(connection, "tracks"));
        Assert.Contains("file_replaced_at", ColumnsOf(connection, "tracks"));
        Assert.Contains("deliberate", ColumnsOf(connection, "removed_tracks"));
        Assert.Contains("moved_from_path", ColumnsOf(connection, "tracks"));
        Assert.Contains("origin_file_stamp", ColumnsOf(connection, "tracks"));
    }

    // And it has to survive the round trip that matters: the repaired column is
    // NOT NULL DEFAULT '', so an ALTER that got the default wrong would add it
    // and then fail on the first insert - which is the same silent-empty-library
    // failure one step further along.
    [Fact]
    public void A_repaired_database_can_be_written_to_again()
    {
        using var connection = OpenNew("repaired.db");
        SqliteMigrations.Apply(connection);
        Execute(connection, "ALTER TABLE tracks DROP COLUMN album_artist;");

        SqliteMigrations.Apply(connection);

        Execute(connection, "INSERT INTO tracks (id, path, title, date_added) VALUES ('t1', '/music/a.mp3', 'A', 0);");
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT album_artist FROM tracks WHERE id = 't1';";
        Assert.Equal(string.Empty, read.ExecuteScalar());
    }

    // Step 12. A database the baseline built at version 11 keyed excluded_paths
    // ignoring case, so on a filesystem where "Song.flac" and "song.flac" are
    // two files, removing one excluded both. The rows it held are kept.
    [Fact]
    public void Excluded_paths_stop_ignoring_case_and_keep_what_they_held()
    {
        using var connection = OpenNew("nocase.db");
        SqliteMigrations.Apply(connection);
        Execute(connection, """
            DROP TABLE excluded_paths;
            CREATE TABLE excluded_paths (path TEXT NOT NULL PRIMARY KEY COLLATE NOCASE, excluded_at INTEGER NOT NULL);
            INSERT INTO excluded_paths (path, excluded_at) VALUES ('/music/Song.flac', 5);
            ALTER TABLE tracks ADD COLUMN starred_at INTEGER;
            PRAGMA user_version = 11;
            """);

        SqliteMigrations.Apply(connection);

        Execute(connection, "INSERT INTO excluded_paths (path, excluded_at) VALUES ('/music/song.flac', 6);");
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM excluded_paths WHERE path = '/music/Song.flac' AND excluded_at = 5;";
        Assert.Equal(1L, count.ExecuteScalar());
        count.CommandText = "SELECT COUNT(*) FROM excluded_paths;";
        Assert.Equal(2L, count.ExecuteScalar());
    }

    // Step 13. Track.StarredAt went with the "Date Starred" smart playlist
    // field it fed; the star itself stays.
    [Fact]
    public void When_a_track_was_starred_is_dropped_and_the_star_kept()
    {
        using var connection = OpenNew("starred-at.db");
        SqliteMigrations.Apply(connection);
        Execute(connection, """
            ALTER TABLE tracks ADD COLUMN starred_at INTEGER;
            INSERT INTO tracks (id, path, title, date_added, starred, starred_at) VALUES ('t1', '/music/a.mp3', 'A', 0, 1, 7);
            PRAGMA user_version = 12;
            """);

        SqliteMigrations.Apply(connection);

        Assert.DoesNotContain("starred_at", ColumnsOf(connection, "tracks"));
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT starred FROM tracks WHERE id = 't1';";
        Assert.Equal(1L, read.ExecuteScalar());
    }

    // Applying twice is what every startup after the first one does.
    [Fact]
    public void Applying_again_changes_nothing()
    {
        using var connection = OpenNew("twice.db");

        SqliteMigrations.Apply(connection);
        var afterFirst = ColumnsOf(connection, "tracks");
        SqliteMigrations.Apply(connection);

        Assert.Equal(afterFirst, ColumnsOf(connection, "tracks"));
        Assert.Equal(SqliteMigrations.LatestVersion, SqliteMigrations.ReadVersion(connection));
    }

    // Nothing migrates from before the baseline. Refused outright, because
    // reading such a database as current is the "no such column" failure the
    // reconciling pass exists to prevent, arriving by another door.
    [Fact]
    public void A_database_from_before_the_baseline_is_refused()
    {
        using var connection = OpenNew("old.db");
        Execute(connection, "CREATE TABLE tracks (id TEXT NOT NULL PRIMARY KEY);");
        Execute(connection, "PRAGMA user_version = 6;");

        var refused = Assert.Throws<ObsoleteSchemaException>(() => SqliteMigrations.Apply(connection));

        Assert.Equal(6, refused.Version);
    }

    // ...and FlowerDb sets it aside for a fresh one, the way it does an
    // unreadable file, rather than letting the refusal take startup down.
    [Fact]
    public void FlowerDb_sets_a_database_from_before_the_baseline_aside()
    {
        var path = Path.Combine(_directory, "flower.db");
        using (var old = new SqliteConnection($"Data Source={path}"))
        {
            old.Open();
            Execute(old, "CREATE TABLE tracks (id TEXT NOT NULL PRIMARY KEY);");
            Execute(old, "PRAGMA user_version = 6;");
        }
        SqliteConnection.ClearAllPools();

        var db = new FlowerDb(path);

        Assert.True(File.Exists(FlowerDb.CorruptPath(path)));
        using var connection = db.Open();
        Assert.Equal(SqliteMigrations.LatestVersion, SqliteMigrations.ReadVersion(connection));
        Assert.Contains("album_artist", ColumnsOf(connection, "tracks"));
    }
}
