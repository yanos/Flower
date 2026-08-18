using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

namespace Flower.Persistence
{
    // Crash-safe read/write for every JSON file this app owns.
    //
    // Before this existed, every store wrote straight over its live file with
    // File.WriteAllText. A crash, forced quit, or power loss part-way through
    // left a truncated file, and every Load() in this codebase catches a
    // deserialization failure and returns "empty" - which for library.json
    // specifically meant the startup rescan then rebuilt the library from disk
    // with DateAdded defaulted to now and every PlayCount back to 0. Play
    // counts, first-seen dates, LastPlayedAt and RemotePlayCounts exist
    // nowhere else, so that was silent, permanent data loss from a single
    // badly-timed crash - and on a real 16k-track library, library.json is
    // ~18MB, rewritten on every track start and every track end, so the write
    // window is neither small nor rare. DeviceKeyStore had the same shape with
    // a different consequence: a corrupt load regenerates the signing keypair,
    // permanently breaking trust with every peer that had this device paired.
    //
    // Write is temp-file + fsync + File.Replace, which also hands us a
    // previous-good copy for free (Replace's backup argument). Read falls back
    // to that copy, and quarantines an unreadable file to *.corrupt instead of
    // deleting the evidence.
    public static class AtomicJsonFile
    {
        // Previous good contents, written by File.Replace on every successful
        // save. One generation only - enough to survive a torn write, not a
        // version history.
        public static string BackupPath(string path) => path + ".bak";

        // Where an unreadable file is moved so the next save can start clean
        // without destroying whatever a bug report might need.
        public static string CorruptPath(string path) => path + ".corrupt";

        private static string TempPath(string path) => path + ".tmp";

        // One writer at a time per file, process-wide.
        //
        // Every store already has its own SemaphoreSlim around its saves, but
        // those are per *instance* while the file is per *path* - two stores
        // built over the same path (two DI containers in one process; any store
        // constructed directly rather than resolved, which several of them offer
        // a parameterless constructor for) serialize against nothing, and both
        // land on the same fixed .tmp name. That is not hypothetical: it is what
        // made CompositionRootTests fail intermittently with "the process cannot
        // access settings.json.tmp because it is being used by another process",
        // when a previous container's fire-and-forget SaveAsync was still in
        // flight as the next test's container resolved AppSettings (whose Load
        // writes, see AppSettingsStore.Load's Apple-Music-folder branch).
        //
        // Keyed on the full path so a relative and an absolute spelling of the
        // same file still share a lock. Never evicted - one semaphore per JSON
        // file this app owns is a handful of objects for the process lifetime.
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> WriteLocks = new(StringComparer.Ordinal);

        private static SemaphoreSlim WriteLockFor(string path) =>
            WriteLocks.GetOrAdd(Path.GetFullPath(path), _ => new SemaphoreSlim(1, 1));

        public static void Write<T>(string path, T value, JsonTypeInfo<T> typeInfo, bool ownerOnly = false)
        {
            var writeLock = WriteLockFor(path);
            writeLock.Wait();
            try
            {
                WriteCore(path, value, typeInfo, ownerOnly);
            }
            finally
            {
                writeLock.Release();
            }
        }

        private static void WriteCore<T>(string path, T value, JsonTypeInfo<T> typeInfo, bool ownerOnly)
        {
            var temp = PrepareWrite(path);
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                RestrictToOwner(temp, ownerOnly);
                JsonSerializer.Serialize(stream, value, typeInfo);
                // flushToDisk: the whole point is surviving a crash between
                // here and the swap below - a write sitting in the OS page
                // cache would not.
                stream.Flush(flushToDisk: true);
            }

            Commit(temp, path);
            RestrictToOwner(path, ownerOnly);
            RestrictToOwner(BackupPath(path), ownerOnly);
        }

        public static async Task WriteAsync<T>(string path, T value, JsonTypeInfo<T> typeInfo, bool ownerOnly = false)
        {
            var writeLock = WriteLockFor(path);
            await writeLock.WaitAsync();
            try
            {
                await WriteAsyncCore(path, value, typeInfo, ownerOnly);
            }
            finally
            {
                writeLock.Release();
            }
        }

        private static async Task WriteAsyncCore<T>(string path, T value, JsonTypeInfo<T> typeInfo, bool ownerOnly)
        {
            var temp = PrepareWrite(path);
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                RestrictToOwner(temp, ownerOnly);
                await JsonSerializer.SerializeAsync(stream, value, typeInfo);
                stream.Flush(flushToDisk: true);
            }

            Commit(temp, path);
            RestrictToOwner(path, ownerOnly);
            RestrictToOwner(BackupPath(path), ownerOnly);
        }

        // 0600 for files holding secrets (DeviceKeyStore's private key). Set on
        // the temp file *before* any bytes are serialized into it, so the key
        // material is never world-readable even briefly, and re-applied after
        // Commit because File.Replace preserves the *target's* old mode, not
        // the temp file's. No-op on Windows, where the file inherits the
        // per-user profile directory's ACL and SetUnixFileMode throws.
        private static void RestrictToOwner(string path, bool ownerOnly)
        {
            if (!ownerOnly || OperatingSystem.IsWindows() || !File.Exists(path))
                return;

            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception)
            {
                // Best-effort: a filesystem without POSIX modes (an Android
                // external-storage/FAT mount, a network share) is not a reason
                // to fail the save - the file's contents are still correct.
            }
        }

        // Returns default(T) only when there is genuinely nothing to read (no
        // file yet - the first-launch case). A file that exists but can't be
        // read is escalated: the caller still gets default(T), but the bad file
        // is quarantined and the failure is logged at Error, not Debug, since
        // for library.json it means the user is about to see an empty library.
        public static T? Read<T>(string path, JsonTypeInfo<T> typeInfo, ILogger? logger = null)
        {
            if (!File.Exists(path))
                return TryReadBackupOnly(path, typeInfo, logger);

            if (TryDeserialize(path, typeInfo, out var value))
                return value;

            logger?.LogError("Could not read {Path}; quarantining it to {CorruptPath} and trying the previous good copy", path, CorruptPath(path));
            Quarantine(path, logger);

            var backup = BackupPath(path);
            if (File.Exists(backup) && TryDeserialize(backup, typeInfo, out var recovered))
            {
                logger?.LogWarning("Recovered {Path} from {BackupPath}", path, backup);
                // Put the recovered contents back where they belong, so the
                // next reader doesn't have to repeat this and a save that
                // never comes (user quits immediately) doesn't lose it.
                TryRestore(backup, path, logger);
                return recovered;
            }

            logger?.LogError("No usable backup for {Path} - starting from defaults", path);
            return default;
        }

        // A missing primary with a surviving .bak shouldn't happen (File.Replace
        // writes the backup and the target together), but an external deletion
        // or a half-finished manual restore can produce it, and recovering is
        // free.
        private static T? TryReadBackupOnly<T>(string path, JsonTypeInfo<T> typeInfo, ILogger? logger)
        {
            var backup = BackupPath(path);
            if (!File.Exists(backup) || !TryDeserialize(backup, typeInfo, out var recovered))
                return default;

            logger?.LogWarning("{Path} is missing; recovered it from {BackupPath}", path, backup);
            TryRestore(backup, path, logger);
            return recovered;
        }

        private static bool TryDeserialize<T>(string path, JsonTypeInfo<T> typeInfo, out T? value)
        {
            try
            {
                using var stream = File.OpenRead(path);
                value = JsonSerializer.Deserialize(stream, typeInfo);
                return value != null;
            }
            catch (Exception)
            {
                value = default;
                return false;
            }
        }

        private static string PrepareWrite(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return TempPath(path);
        }

        private static void Commit(string temp, string path)
        {
            if (!File.Exists(path))
            {
                File.Move(temp, path);
                return;
            }

            try
            {
                // ignoreMetadataErrors: we care that the bytes swap atomically,
                // not that ACLs/attributes survive - a metadata copy failure is
                // not worth failing the save over.
                File.Replace(temp, path, BackupPath(path), ignoreMetadataErrors: true);
            }
            catch (Exception ex) when (ex is PlatformNotSupportedException or IOException or UnauthorizedAccessException)
            {
                // Some filesystems (notably a few Android external-storage
                // mounts) don't implement the replace-with-backup primitive.
                // Copy-then-move still gives an atomic swap of the target; only
                // the backup step degrades from atomic to best-effort.
                TryCopy(path, BackupPath(path));
                File.Move(temp, path, overwrite: true);
            }
        }

        private static void TryCopy(string source, string destination)
        {
            try
            {
                File.Copy(source, destination, overwrite: true);
            }
            catch (IOException)
            {
                // Best-effort: losing the backup is not a reason to fail the save.
            }
        }

        private static void Quarantine(string path, ILogger? logger)
        {
            try
            {
                File.Move(path, CorruptPath(path), overwrite: true);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Could not quarantine unreadable {Path}", path);
            }
        }

        private static void TryRestore(string backup, string path, ILogger? logger)
        {
            try
            {
                File.Copy(backup, path, overwrite: true);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Recovered {Path} in memory but could not write it back from {BackupPath}", path, backup);
            }
        }
    }
}
