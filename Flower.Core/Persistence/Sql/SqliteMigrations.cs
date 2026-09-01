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
                return;

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
