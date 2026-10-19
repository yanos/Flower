using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.Data.Sqlite;

namespace Flower.Persistence.Sql
{
    // Schema versioning via PRAGMA user_version - SQLite's own built-in
    // four-byte slot in the database header, so there is no bookkeeping table
    // to create before the first step can run.
    //
    // A fresh database is built from Schema.Baseline in one go and stamped at
    // BaselineVersion. Changes after that are either folded into the baseline
    // or appended to Steps, each applied once, in order, inside a transaction
    // with its version bump. There is no down-migration and no model-diffing.
    //
    // The baseline is every step there used to be, collapsed (October 2026):
    // with no released users (CLAUDE.md, "No Users Yet") there is nothing to
    // carry forward from older schemas, so a database stamped below the
    // baseline is not migrated - Apply refuses it with ObsoleteSchemaException
    // and FlowerDb sets it aside for a fresh one. A database already at the
    // baseline, which is every one that kept up, carries on untouched.
    //
    // Which change goes where: fold an *addition* into the baseline - the
    // reconciling pass below adds the column to databases that already exist.
    // Append a step for a rename, a drop, a retype or a new table, because
    // folding one of those in only changes what a *fresh* database is created
    // with, and an existing one keeps the old shape while every query asks for
    // the new one. That mistake took a running server down once
    // (origin_album_art_hash, renamed in place) and an addition folded in
    // before the reconciling pass existed cost a phone its whole library.
    public static class SqliteMigrations
    {
        // The version Schema.Baseline stands for. Eleven because that is how
        // many steps it replaced, so every database that ran all of them is
        // already current.
        public const int BaselineVersion = 11;

        // Steps after the baseline: index 0 brings a database to
        // BaselineVersion + 1, and so on. Only ever appended.
        private static readonly IReadOnlyList<string> Steps =
        [
            // 12: excluded_paths compared paths ignoring case, which on a
            // filesystem that respects it made "Song.flac" and "song.flac" one
            // row - removing one from the library excluded both. Library keys
            // them by PathRules now, and the table holds what it is given. A
            // collation cannot be altered in place, so the table is rebuilt; on
            // a database the baseline created without NOCASE this rebuilds an
            // identical one.
            """
            CREATE TABLE excluded_paths_exact (
                path        TEXT    NOT NULL PRIMARY KEY,
                excluded_at INTEGER NOT NULL
            );
            INSERT INTO excluded_paths_exact (path, excluded_at) SELECT path, excluded_at FROM excluded_paths;
            DROP TABLE excluded_paths;
            ALTER TABLE excluded_paths_exact RENAME TO excluded_paths;
            """,
        ];

        public static int LatestVersion => BaselineVersion + Steps.Count;

        public static void Apply(FlowerDb db)
        {
            using var connection = db.Open();
            Apply(connection);
        }

        public static void Apply(SqliteConnection connection)
        {
            var current = ReadVersion(connection);
            if (current == 0)
            {
                Run(connection, Schema.Baseline, BaselineVersion);
                current = BaselineVersion;
            }
            else if (current < BaselineVersion)
            {
                throw new ObsoleteSchemaException(current);
            }

            for (var version = current; version < LatestVersion; version++)
                Run(connection, Steps[version - BaselineVersion], version + 1);

            // Even when there was nothing to run: a database at the current
            // version is exactly the one a folded-in addition leaves short.
            ReconcileFoldedInColumns(connection);
        }

        private static void Run(SqliteConnection connection, string sql, int version)
        {
            using var transaction = connection.BeginTransaction();

            using (var script = connection.CreateCommand())
            {
                script.Transaction = transaction;
                script.CommandText = sql;
                script.ExecuteNonQuery();
            }

            using (var bump = connection.CreateCommand())
            {
                bump.Transaction = transaction;
                // user_version takes no parameter binding - it is a pragma,
                // not a statement. The value is a constant or a loop counter,
                // never user input.
                bump.CommandText = $"PRAGMA user_version = {version};";
                bump.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        // Adds any column the schema has grown since this database was created.
        //
        // A folded-in column reaches a *fresh* database and no existing one, and
        // user_version cannot tell: the database is current, there is nothing
        // to run, and the first query naming the column throws "no such
        // column". On a phone that was album_artist, and LibraryStore caught
        // the exception and started with an empty library - which looks like a
        // sync problem, and sends you to look at a sync that is fine.
        //
        // So the runner makes the rule unnecessary for the case it applies to.
        // A fresh in-memory database built from the same scripts is the
        // reference; any column it has that this one does not is added here.
        //
        // Only additions, deliberately. A rename, a drop or a retype still has
        // to be an appended step, because the reference cannot tell a renamed
        // column from a new one beside an abandoned old one - the difference is
        // intent, and intent has to be written down.
        private static void ReconcileFoldedInColumns(SqliteConnection connection)
        {
            using var reference = new SqliteConnection("Data Source=:memory:");
            reference.Open();
            foreach (var sql in Steps.Prepend(Schema.Baseline))
            {
                using var script = reference.CreateCommand();
                script.CommandText = sql;
                script.ExecuteNonQuery();
            }

            foreach (var table in TableNames(reference))
            {
                var live = new HashSet<string>(
                    ColumnsOf(connection, table).ConvertAll(c => c.Name),
                    StringComparer.OrdinalIgnoreCase);

                // A table the live database has never heard of is not a folded-in
                // column - it is a table an appended script was supposed to
                // create, and inventing it here would paper over that.
                if (live.Count == 0)
                    continue;

                foreach (var column in ColumnsOf(reference, table))
                {
                    if (live.Contains(column.Name))
                        continue;

                    // SQLite cannot add a NOT NULL column without a default to
                    // put in the rows that already exist. The baseline always gives one
                    // (see album_artist's DEFAULT ''), so this is a guard
                    // against a future schema edit rather than a live case -
                    // and it throws, because the alternative is the silent
                    // half-repaired database this whole method exists to stop.
                    if (column.NotNull && column.Default == null)
                    {
                        throw new InvalidOperationException(
                            $"Cannot add {table}.{column.Name} to an existing database: it is NOT NULL with no default. "
                            + "Append it as a step that backfills a value instead of folding it into the baseline.");
                    }

                    using var alter = connection.CreateCommand();
                    var nullability = column.NotNull ? " NOT NULL" : string.Empty;
                    var fallback = column.Default is { } value ? $" DEFAULT {value}" : string.Empty;
                    alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column.Name} {column.Type}{nullability}{fallback};";
                    alter.ExecuteNonQuery();
                }
            }
        }

        private sealed record ColumnInfo(string Name, string Type, bool NotNull, string? Default);

        private static List<string> TableNames(SqliteConnection connection)
        {
            var names = new List<string>();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                names.Add(reader.GetString(0));
            return names;
        }

        private static List<ColumnInfo> ColumnsOf(SqliteConnection connection, string table)
        {
            var columns = new List<ColumnInfo>();
            using var command = connection.CreateCommand();
            // Interpolated because SQLite binds values, not
            // identifiers, and these names come from sqlite_master rather than
            // from anything a user typed.
            command.CommandText = $"PRAGMA table_info({table});";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                columns.Add(new ColumnInfo(
                    Name: reader.GetString(1),
                    Type: reader.IsDBNull(2) ? "TEXT" : reader.GetString(2),
                    NotNull: reader.GetInt32(3) != 0,
                    Default: reader.IsDBNull(4) ? null : reader.GetString(4)));
            }

            return columns;
        }

        public static int ReadVersion(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            return Convert.ToInt32(command.ExecuteScalar());
        }
    }

    // A database stamped below SqliteMigrations.BaselineVersion: built by a
    // schema older than anything this code migrates from.
    public sealed class ObsoleteSchemaException(int version)
        : Exception($"Database schema version {version} predates the baseline ({SqliteMigrations.BaselineVersion}) and is not migrated.")
    {
        public int Version { get; } = version;
    }
}
