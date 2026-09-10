using System;
using System.Collections.Generic;

using Microsoft.Data.Sqlite;

namespace Flower.Persistence.Sql
{
    // Schema versioning via PRAGMA user_version - SQLite's own built-in
    // four-byte slot in the database header, so there is no bookkeeping table
    // to create before the first migration can run.
    //
    // This replaces what EF Core's migrations would have given for free, and
    // is deliberately the whole of it: an ordered list of scripts, applied in
    // sequence, each inside a transaction with the version bump. There is no
    // down-migration and no model-diffing - a schema change is a new entry
    // appended to Scripts, written by hand.
    //
    // Note what this fixes on the server side: Flower.Server used to call
    // EnsureCreatedAsync() with no migrations at all, so any schema change
    // silently wiped a self-hoster's database (ARCHITECTURE-REVIEW Tier 2.5).
    //
    // The default is still to fold a schema change straight into V1 rather than
    // append a step: with no released users there is nothing to migrate *from*,
    // and the honest upgrade path for a stale local flower.db is to delete it
    // and rescan. Append a script only when that delete-and-rescan would lose
    // something a rescan cannot reproduce - play counts, starred flags,
    // playlists - which is what V2 is doing here.
    //
    // With one exception, which cost a running server: that default holds only
    // for *adding* a column. Folding in a rename, a drop or a retype leaves an
    // existing database holding a shape the code no longer asks for, and the
    // next read throws - see Schema.V7, which is that mistake corrected.
    //
    // Folding in an *addition* used to be described here as harmless, on the
    // grounds that it leaves a database merely missing something. It is not:
    // the next query naming the column throws exactly like a rename does, just
    // later and more quietly, and user_version says the database is current so
    // nothing offers to fix it. That cost a phone its entire library - see
    // ReconcileFoldedInColumns, which now repairs it automatically so that
    // getting this wrong is no longer something to get wrong.
    public static class SqliteMigrations
    {
        // Index + 1 is the schema version a script brings the database to, so
        // order is significant and entries are only ever appended.
        private sealed record Migration(string Sql, Func<SqliteConnection, bool>? IsAlreadyApplied = null);

        private static readonly IReadOnlyList<Migration> Scripts =
        [
            new Migration(Schema.V1),
            new Migration(Schema.V2),
            new Migration(Schema.V3),
            new Migration(Schema.V4),
            new Migration(Schema.V5, connection => HasColumn(connection, "tracks", "encoder_profile")),
            new Migration(Schema.V6),
            new Migration(Schema.V7, connection => HasColumn(connection, "tracks", "origin_album_art_id")),
        ];

        public static int LatestVersion => Scripts.Count;

        public static void Apply(FlowerDb db)
        {
            using var connection = db.Open();
            Apply(connection);
        }

        public static void Apply(SqliteConnection connection)
        {
            var current = ReadVersion(connection);
            if (current >= Scripts.Count)
            {
                // Still reconcile: a database at the current version is exactly
                // the one that folding an addition into V1 leaves broken, and
                // the early return is what made that invisible.
                ReconcileFoldedInColumns(connection);
                return;
            }

            for (var version = current; version < Scripts.Count; version++)
            {
                using var transaction = connection.BeginTransaction();

                using (var script = connection.CreateCommand())
                {
                    script.Transaction = transaction;
                    var migration = Scripts[version];
                    if (!migration.IsAlreadyApplied?.Invoke(connection) ?? true)
                    {
                        script.CommandText = migration.Sql;
                        script.ExecuteNonQuery();
                    }
                }

                using (var bump = connection.CreateCommand())
                {
                    bump.Transaction = transaction;
                    // user_version takes no parameter binding - it is a pragma,
                    // not a statement. The value is an int from a loop counter,
                    // never user input, so the interpolation is not a injection
                    // surface.
                    bump.CommandText = $"PRAGMA user_version = {version + 1};";
                    bump.ExecuteNonQuery();
                }

                transaction.Commit();
            }

            ReconcileFoldedInColumns(connection);
        }

        // Adds any column that V1 has grown since this database was created.
        //
        // This exists because "fold an addition into V1" is a trap that has now
        // been sprung twice. A folded-in column reaches a *fresh* database and
        // no existing one, and user_version cannot tell: the database is at the
        // latest version, the runner has nothing to do, and the first query
        // naming the new column throws "no such column". On the server that was
        // origin_album_art_id (see Schema.V7). On a phone it was album_artist,
        // and the symptom was worse for being quieter - LibraryStore caught the
        // exception, logged it, and started with an empty library, while every
        // save failed the same way. A library that is simply *not there* looks
        // like a sync problem, and the sync it sends you to look at is fine.
        //
        // So rather than asking everyone to remember the rule, the runner makes
        // the rule unnecessary for the case it actually applies to. A fresh
        // in-memory database built from the same scripts is the reference; any
        // column it has that this one does not is added here.
        //
        // Only additions, deliberately. A rename, a drop or a retype still has
        // to be an appended script, because the reference cannot tell a renamed
        // column from a new one beside an abandoned old one - the difference is
        // intent, and intent has to be written down. That is the rule Schema.V7
        // states, and it is unchanged; what changes is that forgetting it for an
        // *addition* is no longer fatal.
        private static void ReconcileFoldedInColumns(SqliteConnection connection)
        {
            using var reference = new SqliteConnection("Data Source=:memory:");
            reference.Open();
            foreach (var migration in Scripts)
            {
                using var script = reference.CreateCommand();
                if (migration.IsAlreadyApplied?.Invoke(reference) == true)
                    continue;
                script.CommandText = migration.Sql;
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
                    // put in the rows that already exist. V1 always gives one
                    // (see album_artist's DEFAULT ''), so this is a guard
                    // against a future schema edit rather than a live case -
                    // and it throws, because the alternative is the silent
                    // half-repaired database this whole method exists to stop.
                    if (column.NotNull && column.Default == null)
                    {
                        throw new InvalidOperationException(
                            $"Cannot add {table}.{column.Name} to an existing database: it is NOT NULL with no default. "
                            + "Append it as a migration script that backfills a value instead of folding it into V1.");
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
            // Same reason HasColumn interpolates: SQLite binds values, not
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

        private static bool HasColumn(SqliteConnection connection, string table, string column)
        {
            using var command = connection.CreateCommand();
            // Table names are fixed at the migration call site; SQLite only
            // permits bindings for values, not identifiers.
            command.CommandText = $"PRAGMA table_info({table});";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}
