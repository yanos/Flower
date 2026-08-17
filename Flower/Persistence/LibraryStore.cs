using System;
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

        // Coalescing state for ScheduleSave. _pendingTracks is the live
        // Library.Tracks reference every caller passes, so "the latest pending
        // save" and "the one already queued" are the same data - the flush just
        // needs to happen once, later, rather than once per event.
        private readonly object _pendingLock = new();
        private CancellationTokenSource? _pendingFlush;
        private IEnumerable<Track>? _pendingTracks;

        // Long enough to swallow the burst a single track change produces
        // (RecordPlayed on Play, IncrementPlayCount on EndReached), short
        // enough that a crash loses at most this much. MainWindow's Closing
        // handler calls Flush(), so a clean quit never waits it out.
        private static readonly TimeSpan FlushDelay = TimeSpan.FromSeconds(3);

        public LibraryStore(ILogger<LibraryStore> logger)
        {
            _logger = logger;
        }

        // Debounced counterpart to SaveAsync, for the play-count/LastPlayedAt
        // hot path. Playing a song used to write the entire library to disk
        // twice - once from Play, once from EndReached - and at the 16k-track
        // scale this app targets that is a multi-megabyte serialize per song
        // change for a payload whose only delta is one integer. Coalescing
        // behind a timer makes it one write per burst instead. See
        // docs/ARCHITECTURE-REVIEW.md Tier 1.1.
        public void ScheduleSave(IEnumerable<Track> tracks)
        {
            CancellationTokenSource cts;
            lock (_pendingLock)
            {
                _pendingTracks = tracks;
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
                return; // Superseded by a newer ScheduleSave, which owns the flush now.
            }

            IEnumerable<Track>? tracks;
            lock (_pendingLock)
            {
                if (token.IsCancellationRequested)
                    return;

                tracks = _pendingTracks;
                _pendingTracks = null;
            }

            if (tracks != null)
                await SaveAsync(tracks);
        }

        // Writes any coalesced save that hasn't fired yet, synchronously, for
        // the app-exit path (see MainWindow's Closing handler and Save below) -
        // without this, quitting inside the debounce window silently drops the
        // play count that triggered it, which is the exact data loss Tier 0
        // just finished closing off elsewhere.
        public void Flush()
        {
            IEnumerable<Track>? tracks;
            lock (_pendingLock)
            {
                tracks = DiscardPending();
            }

            if (tracks != null)
                Save(tracks);
        }

        // Cancels the outstanding debounce and hands back whatever it was going
        // to write (null if nothing was pending). Caller must hold _pendingLock.
        private IEnumerable<Track>? DiscardPending()
        {
            _pendingFlush?.Cancel();
            _pendingFlush?.Dispose();
            _pendingFlush = null;

            var tracks = _pendingTracks;
            _pendingTracks = null;
            return tracks;
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

            // An explicit synchronous save supersedes any coalesced one still
            // waiting: every ScheduleSave caller passes the same live
            // Library.Tracks this is about to write, so letting the debounce
            // fire afterwards would only rewrite identical bytes - and on the
            // app-exit path it would race process teardown to do it.
            lock (_pendingLock)
            {
                DiscardPending();
            }

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
