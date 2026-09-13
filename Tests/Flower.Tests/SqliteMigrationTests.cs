using System;
using System.Collections.Generic;
using System.IO;

using Microsoft.Data.Sqlite;

using Flower.Persistence.Sql;

using Xunit;

namespace Flower.Tests;

// The migration runner, pinned by the one case that got past it into a running
// server.
//
// origin_album_art_hash was renamed to origin_album_art_id by editing Schema.V1
// in place, which is where every schema change before it had been made. That is
// right for an added column and wrong for a renamed one, and the difference only
// shows on a database that already exists: it is stamped at the latest version,
// so V1 never runs again, so it kept the old column name while every query
// started asking for the new one. The server threw on its first read - "SQLite
// Error 1: no such column: origin_album_art_id" - before it finished starting.
//
// What these hold is that both doors work: an old database is renamed rather
// than thrown at, and a new one, which V1 already creates under the new name, is
// not asked to rename a column it does not have.
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

    // A database as it stood before the rename: every migration up to the one
    // under test, then the column put back under its old name and the version
    // stamp left where a released build would have left it. Reconstructed this
    // way rather than by keeping a copy of the old V1 text, so it cannot drift
    // from what the runner actually produces.
    private SqliteConnection OpenPreRenameDatabase(string name)
    {
        var connection = OpenNew(name);
        SqliteMigrations.Apply(connection);
        Execute(connection, "ALTER TABLE tracks RENAME COLUMN origin_album_art_id TO origin_album_art_hash;");
        Execute(connection, "PRAGMA user_version = 6;");
        return connection;
    }

    [Fact]
    public void A_fresh_database_is_created_at_the_latest_version()
    {
        using var connection = OpenNew("fresh.db");

        SqliteMigrations.Apply(connection);

        Assert.Equal(SqliteMigrations.LatestVersion, SqliteMigrations.ReadVersion(connection));
        Assert.Contains("origin_album_art_id", ColumnsOf(connection, "tracks"));
        Assert.DoesNotContain("origin_album_art_hash", ColumnsOf(connection, "tracks"));
    }

    // The other half of the same mistake, and the one that got further: a
    // column added by editing V1 in place reaches a fresh database and no
    // existing one, while user_version says there is nothing to do. On a phone
    // that was album_artist - LibraryStore caught the "no such column", logged
    // it, and started with an empty library, and every save after it failed the
    // same way. An empty library reads as a sync fault, so the search starts in
    // the wrong place entirely.
    //
    // Simulated by dropping the column back out of an up-to-date database,
    // which is exactly the shape the phone was in: latest version, missing a
    // column V1 grew later.
    [Fact]
    public void A_column_folded_into_V1_reaches_a_database_that_already_existed()
    {
        using var connection = OpenNew("folded.db");
        SqliteMigrations.Apply(connection);
        Execute(connection, "ALTER TABLE tracks DROP COLUMN album_artist;");
        Assert.DoesNotContain("album_artist", ColumnsOf(connection, "tracks"));

        SqliteMigrations.Apply(connection);

        Assert.Contains("album_artist", ColumnsOf(connection, "tracks"));
        Assert.Equal(SqliteMigrations.LatestVersion, SqliteMigrations.ReadVersion(connection));
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

    // The failure itself. Without V7 this database keeps origin_album_art_hash
    // forever and TrackRepository.LoadAll throws on its first SELECT.
    [Fact]
    public void A_database_from_before_the_rename_is_carried_over_to_the_new_column_name()
    {
        using var connection = OpenPreRenameDatabase("stale.db");
        Assert.Contains("origin_album_art_hash", ColumnsOf(connection, "tracks"));

        SqliteMigrations.Apply(connection);

        Assert.Contains("origin_album_art_id", ColumnsOf(connection, "tracks"));
        Assert.DoesNotContain("origin_album_art_hash", ColumnsOf(connection, "tracks"));
        Assert.Equal(SqliteMigrations.LatestVersion, SqliteMigrations.ReadVersion(connection));
    }

    // A rename preserves what is in the column - which is the whole reason this
    // is a migration step and not an instruction to delete the database. On a
    // server that database is the library.
    [Fact]
    public void The_rename_keeps_the_rows_it_finds()
    {
        using var connection = OpenPreRenameDatabase("rows.db");
        Execute(
            connection,
            """
            INSERT INTO tracks (id, title, date_added, origin_album_art_hash, play_count)
            VALUES ('t1', 'Kind of Blue', 0, 'al-42', 7);
            """);

        SqliteMigrations.Apply(connection);

        using var read = connection.CreateCommand();
        read.CommandText = "SELECT origin_album_art_id, play_count FROM tracks WHERE id = 't1';";
        using var reader = read.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("al-42", reader.GetString(0));
        Assert.Equal(7, reader.GetInt32(1));
    }

    // Applying twice is what every startup after the first one does.
    [Fact]
    public void Applying_again_changes_nothing()
    {
        using var connection = OpenPreRenameDatabase("twice.db");

        SqliteMigrations.Apply(connection);
        var afterFirst = ColumnsOf(connection, "tracks");
        SqliteMigrations.Apply(connection);

        Assert.Equal(afterFirst, ColumnsOf(connection, "tracks"));
        Assert.Equal(SqliteMigrations.LatestVersion, SqliteMigrations.ReadVersion(connection));
    }
}
