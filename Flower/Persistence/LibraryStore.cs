using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
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
        // File.WriteAllTextAsync opens without FileShare, so two overlapping
        // writes collide - silently on Unix (one write's bytes win, harmless
        // since both are serializing the same up-to-date Library.Tracks),
        // loudly on Windows (IOException: file in use). Serialize here so
        // Windows doesn't throw; still race-free content-wise since all
        // concurrent callers exist to persist the same tracks list.
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public LibraryStore(ILogger<LibraryStore> logger)
        {
            _logger = logger;
        }

        public static string StorePath => Path.Combine(AppDataDirectory.Path, "library.json");

        public List<Track> Load()
        {
            var path = StorePath;
            if (!File.Exists(path))
                return new List<Track>();

            try
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize(json, FlowerJsonContext.Default.TrackList) ?? new List<Track>();
            }
            catch (Exception ex)
            {
                // Corrupt/unreadable library.json would otherwise silently look
                // like "empty library" with no clue why - this is exactly the
                // kind of thing you need in a bug report.
                _logger.LogWarning(ex, "Failed to load library from {Path}; starting with an empty library", path);
                return new List<Track>();
            }
        }

        public async Task<List<Track>> LoadAsync()
        {
            var path = StorePath;
            if (!File.Exists(path))
                return new List<Track>();

            try
            {
                var json = await File.ReadAllTextAsync(path);
                return JsonSerializer.Deserialize(json, FlowerJsonContext.Default.TrackList) ?? new List<Track>();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load library from {Path}; starting with an empty library", path);
                return new List<Track>();
            }
        }

        public async Task SaveAsync(IEnumerable<Track> tracks)
        {
            var path = StorePath;
            var json = JsonSerializer.Serialize(tracks, FlowerJsonContext.Default.TrackEnumerable);

            await _writeLock.WaitAsync();
            try
            {
                // CreateDirectory right before the write, not up front - this
                // is a fire-and-forget call from multiple call sites
                // (PlaylistControlViewModel.Play/EndReached) with no bound on
                // how long it sits queued behind _writeLock, so recreating
                // early would just widen the window for whatever deleted the
                // directory (only ever test teardown in practice - see the
                // catch below) to delete it again before the write lands.
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, json);
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
            var json = JsonSerializer.Serialize(tracks, FlowerJsonContext.Default.TrackEnumerable);

            _writeLock.Wait();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, json);
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
