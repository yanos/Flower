using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

using Flower.Models;
using Flower.Persistence.Sql;

namespace Flower.Persistence
{
    // The library's persistence, backed by SQLite (see Flower.Core's
    // Persistence/Sql/) rather than a single library.json document - see
    // docs/ARCHITECTURE-REVIEW.md Tier 4.1.
    //
    // What that changes, and why the class kept its shape: the whole-library
    // save is still here, because a rescan or a sync merge genuinely does
    // rewrite the whole track list, and it is now an upsert inside one
    // transaction instead of an ~18 MB serialize. What is no longer here is
    // the debounced whole-library write behind a play count: ScheduleStatsSave
    // updates one row, so the coalescing exists to batch a burst of single-row
    // writes rather than to avoid rewriting the library twice per song.
    public class LibraryStore
    {
        private readonly ILogger<LibraryStore> _logger;
        private readonly TrackRepository _tracks;

        // Saves are triggered from independent sites with no ordering between
        // them (a rescan completing, a download finishing, a sync merge), so
        // overlapping writes are expected. SQLite would serialize them itself
        // - that is what FlowerDb's busy timeout is for - but taking the lock
        // here means the *snapshot* is also taken in write order, so a save
        // that starts earlier can't overwrite a newer one's state.
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        private readonly object _pendingLock = new();
        private CancellationTokenSource? _pendingFlush;

        // Keyed by Track, whose identity is its Id - so repeated plays of one
        // track inside a window collapse to a single row update, and the value
        // is the live Track whose counters the flush reads at flush time.
        private readonly Dictionary<Track, byte> _pendingStats = [];

        // Long enough to swallow the burst a single track change produces
        // (RecordPlayed on Play, IncrementPlayCount on EndReached), short
        // enough that a crash loses at most this much. MainWindow's Closing
        // handler calls Flush(), so a clean quit never waits it out.
        private static readonly TimeSpan FlushDelay = TimeSpan.FromSeconds(3);

        public LibraryStore(ILogger<LibraryStore> logger) : this(logger, FlowerDb.OpenDefault())
        {
        }

        public LibraryStore(ILogger<LibraryStore> logger, FlowerDb db)
        {
            _logger = logger;
            _tracks = new TrackRepository(db);
        }

        // The app-data directory the database lives in - MainViewModel uses
        // this to open the containing folder.
        public static string StorePath => FlowerDb.DefaultPath;

        public List<Track> Load()
        {
            try
            {
                return _tracks.LoadAll();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load the library from {Path}; starting with an empty library", StorePath);
                return [];
            }
        }

        public Task<List<Track>> LoadAsync() => Task.FromResult(Load());

        // Coalesced single-row stats write, for the play-count/LastPlayedAt hot
        // path. Takes the affected track (both Library.IncrementPlayCount and
        // Library.RecordPlayed return it) rather than the whole library,
        // because that is now all the database needs to be told: playing a song
        // used to re-serialize and rewrite every track on disk twice, once from
        // Play and once from EndReached, for a payload whose only delta was one
        // integer.
        public void ScheduleStatsSave(Track track)
        {
            CancellationTokenSource cts;
            lock (_pendingLock)
            {
                _pendingStats[track] = 0;
                _pendingFlush?.Cancel();
                _pendingFlush?.Dispose();
                _pendingFlush = cts = new CancellationTokenSource();
            }

            _ = FlushAfterDelayAsync(cts.Token);
        }

        private async Task FlushAfterDelayAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(FlushDelay, token);
            }
            catch (TaskCanceledException)
            {
                return; // Superseded by a newer ScheduleStatsSave, which owns the flush now.
            }

            List<Track> pending;
            lock (_pendingLock)
            {
                if (token.IsCancellationRequested)
                    return;

                pending = DiscardPending();
            }

            WriteStats(pending);
        }

        // Writes any coalesced stats that haven't been flushed yet,
        // synchronously, for the app-exit path (see MainWindow's Closing
        // handler) - without this, quitting inside the debounce window
        // silently drops the play count that triggered it.
        public void Flush()
        {
            List<Track> pending;
            lock (_pendingLock)
            {
                pending = DiscardPending();
            }

            WriteStats(pending);
        }

        // Cancels the outstanding debounce and hands back whatever it was going
        // to write. Caller must hold _pendingLock.
        private List<Track> DiscardPending()
        {
            _pendingFlush?.Cancel();
            _pendingFlush?.Dispose();
            _pendingFlush = null;

            var pending = new List<Track>(_pendingStats.Keys);
            _pendingStats.Clear();
            return pending;
        }

        private void WriteStats(List<Track> tracks)
        {
            if (tracks.Count == 0)
                return;

            _writeLock.Wait();
            try
            {
                foreach (var track in tracks)
                    _tracks.UpdateStats(track);
            }
            catch (Exception ex) when (ex is SqliteException or DirectoryNotFoundException)
            {
                _logger.LogWarning(ex, "Could not write track stats to {Path}; skipping this save", StorePath);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        // A single track mutated in place other than by a stats bump - a
        // placeholder's Path being set after a download, or a tag edit.
        public async Task SaveTrackAsync(Track track)
        {
            await _writeLock.WaitAsync();
            try
            {
                _tracks.Upsert(track);
            }
            catch (Exception ex) when (ex is SqliteException or DirectoryNotFoundException)
            {
                _logger.LogWarning(ex, "Could not write track to {Path}; skipping this save", StorePath);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        // The whole-library write: a rescan, an iTunes import, or a sync merge.
        public async Task SaveAsync(IEnumerable<Track> tracks)
        {
            await _writeLock.WaitAsync();
            try
            {
                // Enumerated inside the lock, not before taking it, for the
                // same reason the JSON store serialized inside it: callers pass
                // the live Library.Tracks from independently-triggered sites,
                // so a snapshot taken before queueing could sit behind the lock
                // while a newer save lands first, then overwrite it.
                _tracks.ReplaceAll(tracks);
            }
            catch (Exception ex) when (ex is SqliteException or DirectoryNotFoundException)
            {
                // The app-data directory disappeared out from under a queued,
                // unawaited save (in practice: test teardown deleting its temp
                // PlatformDataDirectory while a fire-and-forget SaveAsync was
                // still queued behind _writeLock). Some of these run off async
                // void event handlers, so letting this throw crashes the whole
                // process instead of failing one save.
                _logger.LogWarning(ex, "Could not save the library to {Path}; skipping this save", StorePath);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        // Synchronous counterpart for the Window.Closing handler, where the
        // process may exit before an async save completes.
        public void Save(IEnumerable<Track> tracks)
        {
            // An explicit whole-library save supersedes any coalesced stats
            // write still waiting: it writes those same tracks' counters
            // anyway, and on the app-exit path the debounce would otherwise
            // race process teardown.
            lock (_pendingLock)
            {
                DiscardPending();
            }

            _writeLock.Wait();
            try
            {
                _tracks.ReplaceAll(tracks);
            }
            catch (Exception ex) when (ex is SqliteException or DirectoryNotFoundException)
            {
                _logger.LogWarning(ex, "Could not save the library to {Path}; skipping this save", StorePath);
            }
            finally
            {
                _writeLock.Release();
            }
        }
    }
}
