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
    // There is exactly one script today, and that is the point: with no
    // released users there is nothing to migrate *from*, so every schema change
    // so far has been folded straight into V1 rather than appended as a step
    // that only ever runs against a developer's own scratch database. The
    // runner exists for the first change made after a release, not before it -
    // and until then the honest upgrade path for a stale local flower.db is to
    // delete it and rescan.
    public static class SqliteMigrations
    {
        // Index + 1 is the schema version a script brings the database to, so
        // order is significant and entries are only ever appended.
        private static readonly IReadOnlyList<string> Scripts =
        [
            Schema.V1,
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
                    script.CommandText = Scripts[version];
                    script.ExecuteNonQuery();
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
    }
}
