using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Flower.Models;

namespace Flower.Persistence
{
    public class LibraryStore
    {
        private readonly ILogger<LibraryStore> _logger;

        // PlaylistControlViewModel fires off SaveAsync from multiple,
        // independently-triggered call sites (Play, the EndReached handler)
        // with no ordering guarantee between them, so overlapping writes to
        // the same library.json are expected, not a bug to fix upstream.
        // AtomicJsonFile opens its temp file with FileShare.None, so two
        // overlapping writes would collide - silently on Unix, loudly on
        // Windows (IOException: file in use). Serialize here so neither
        // happens, and so the serialize itself happens in write order (see
        // SaveAsync).
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public LibraryStore(ILogger<LibraryStore> logger)
        {
            _logger = logger;
        }

        public static string StorePath => Path.Combine(AppDataDirectory.Path, "library.json");

        public List<Track> Load() =>
            AtomicJsonFile.Read(StorePath, FlowerJsonContext.Default.TrackList, _logger) ?? new List<Track>();

        public Task<List<Track>> LoadAsync() => Task.FromResult(Load());

        public async Task SaveAsync(IEnumerable<Track> tracks)
        {
            var path = StorePath;

            await _writeLock.WaitAsync();
            try
            {
                // Serialized inside the lock, not before taking it. Every caller
                // passes the same live Library.Tracks, but they're fire-and-forget
                // from independently-triggered sites (Play, EndReached), so a
                // snapshot taken before queueing could sit behind the lock while a
                // newer save serializes and lands first - then overwrite it with
                // the older state. Serializing here makes "last to acquire the
                // lock" and "last to write" the same ordering.
                await AtomicJsonFile.WriteAsync(path, tracks, FlowerJsonContext.Default.TrackEnumerable);
            }
            catch (DirectoryNotFoundException ex)
            {
                // The app-data directory disappeared out from under a queued,
                // unawaited save (in practice: test teardown deleting its temp
                // PlatformDataDirectory while a fire-and-forget SaveAsync was
                // still queued behind _writeLock). This runs off an async-void
                // event handler (PlaylistControlViewModel's EndReached
                // subscription), so letting this throw crashes the whole
                // process instead of failing one test/save - nothing left to
                // persist into is not worth that.
                _logger.LogWarning(ex, "Library directory disappeared while saving to {Path}; skipping this save", path);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        // Synchronous counterpart for the Window.Closing handler, where the
        // process may exit before an async save completes - see AppSettingsStore.Save
        // and ColumnManager.Flush for the same pattern. Without this, quitting
        // shortly after a track naturally ends (PlaylistControlViewModel.EndReached
        // increments PlayCount and kicks off a fire-and-forget SaveAsync) can exit
        // before that write lands, so the increment is silently lost on next launch.
        public void Save(IEnumerable<Track> tracks)
        {
            var path = StorePath;

            _writeLock.Wait();
            try
            {
                AtomicJsonFile.Write(path, tracks, FlowerJsonContext.Default.TrackEnumerable);
            }
            catch (DirectoryNotFoundException ex)
            {
                _logger.LogWarning(ex, "Library directory disappeared while saving to {Path}; skipping this save", path);
            }
            finally
            {
                _writeLock.Release();
            }
        }
    }
}
