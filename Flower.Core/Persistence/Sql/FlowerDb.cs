using System;
using System.Data;
using System.IO;

using Microsoft.Data.Sqlite;

namespace Flower.Persistence.Sql
{
    // Owns how a connection to Flower's database is opened, for both the
    // client (Flower/Flower.iOS/Flower.Android) and, once it is ported off EF
    // Core, Flower.Server - see docs/ARCHITECTURE-REVIEW.md Tier 4.1. Every
    // repository goes through Open(); nothing else constructs a
    // SqliteConnection, so the pragmas below cannot be silently skipped by a
    // new call site.
    //
    // Deliberately raw Microsoft.Data.Sqlite rather than EF Core, which Tier
    // 4.1 originally specified. EF Core cannot run on iOS: Apple forbids JIT,
    // so .NET-for-iOS always AOT-compiles, and EF refuses outright with
    // "Model building is not supported when publishing with NativeAOT". Its
    // two documented escape hatches were both tried against this exact model
    // and neither works - a compiled model (dotnet ef dbcontext optimize)
    // clears that error but then never completes on device, and
    // <UseInterpreter>true</UseInterpreter> restores dynamic-code support
    // (RuntimeFeature.IsDynamicCodeSupported went true) only to fail with
    // InvalidProgramException before a DbContext is even constructed. Raw
    // SQL has no model builder to fail: it is a string and bound parameters.
    public sealed class FlowerDb
    {
        private readonly string _path;

        // Held open for the lifetime of the process so an in-memory database
        // (":memory:", used by tests) isn't destroyed the moment the last
        // connection to it closes - SQLite tears one down with its final
        // connection. For a file database this is just a warm connection.
        private readonly SqliteConnection? _keepAlive;

        public FlowerDb(string path)
        {
            _path = path;

            if (IsSharedInMemory(path))
            {
                _keepAlive = new SqliteConnection(ConnectionString);
                _keepAlive.Open();
            }
            else
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
            }

            // Migrating here, rather than leaving it to whoever composes the
            // app, is what makes a FlowerDb usable the moment it is
            // constructed. The alternative - a separate Apply() call at
            // startup - means any store built outside the DI container (which
            // several offer a parameterless constructor for, and every test
            // does) silently talks to a database with no tables in it. The
            // cost is one PRAGMA user_version read per instance, and the
            // instance is a singleton.
            try
            {
                SqliteMigrations.Apply(this);
            }
            catch (SqliteException ex) when (IsUnreadable(ex))
            {
                // A file that isn't a database (or is corrupt past opening)
                // must not take the process down: this runs on the startup
                // path, from the DI factory, before there is any UI to report
                // an error through. Quarantined rather than deleted, exactly
                // as AtomicJsonFile does for the JSON stores - the data is
                // already unreadable, but it is preserved for a bug report
                // instead of being silently overwritten - and a fresh, empty
                // database takes its place. The library rebuilds from the next
                // rescan; what is genuinely lost is play counts and DateAdded,
                // which is the same exposure the JSON quarantine path has.
                Quarantine(path);
                SqliteMigrations.Apply(this);
            }
        }

        // Where an unreadable database is moved aside to, mirroring
        // AtomicJsonFile.CorruptPath's convention for the JSON stores.
        public static string CorruptPath(string path) => path + ".corrupt";

        private static bool IsUnreadable(SqliteException ex) =>
            // SQLITE_NOTADB (26) - the file exists but is not a database, the
            // shape a truncated or partially-overwritten file takes.
            // SQLITE_CORRUPT (11) - it is one, but its pages are damaged.
            ex.SqliteErrorCode is 26 or 11;

        private static void Quarantine(string path)
        {
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            {
                if (!File.Exists(file))
                    continue;

                var destination = CorruptPath(file);
                File.Delete(destination);
                File.Move(file, destination);
            }
        }

        public static string DefaultPath => Path.Combine(AppDataDirectory.Path, "flower.db");

        public static FlowerDb OpenDefault() => new(DefaultPath);

        private static bool IsSharedInMemory(string path) =>
            path.Contains(":memory:", StringComparison.Ordinal) || path.Contains("Mode=Memory", StringComparison.OrdinalIgnoreCase);

        private string ConnectionString => new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            // Microsoft.Data.Sqlite's busy-timeout knob, in seconds. Without
            // it a writer holding the lock makes a concurrent writer fail
            // immediately with SQLITE_BUSY rather than wait - and this app
            // genuinely writes from several places at once (a play-count bump
            // off a LibVLC callback thread while a background rescan upserts).
            DefaultTimeout = 30,
        }.ToString();

        public SqliteConnection Open()
        {
            var connection = new SqliteConnection(ConnectionString);
            connection.Open();

            using var pragma = connection.CreateCommand();
            // WAL lets readers proceed during a write, which is the whole
            // point here: the UI reads the library while a rescan writes it.
            // Persistent, so it only actually takes effect on first use, but
            // setting it per-connection is harmless and covers a fresh file.
            // NORMAL synchronous is the standard WAL pairing - durable across
            // a process crash, which is what this needs to protect against;
            // only an OS/power loss can lose the last commit.
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";
            pragma.ExecuteNonQuery();

            return connection;
        }
    }
}
