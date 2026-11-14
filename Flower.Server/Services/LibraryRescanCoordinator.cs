using System.Diagnostics;

using Flower.Importer;
using Flower.Models;

namespace Flower.Server.Services;

// Owns "is a rescan running right now", so the admin API can start one and the
// browser can poll it, and so two operators clicking Rescan at the same moment
// do not put two importers over the same folders.
//
// The startup scan goes through here too, which is what makes that guard true
// of the one scan most likely to still be running when somebody presses
// Rescan: a 16k-track NAS library takes minutes, and the server is now
// listening for the whole of it (see Program.cs).
//
// A rescan runs on its own DI scope rather than the request's: LibraryImportService
// is scoped (it takes the request-scoped importer logger), and the request that
// started the scan is answered long before a 16k-track scan of a NAS share
// finishes - so resolving it from the request scope would hand the background task
// a service whose scope is already disposed.
public sealed class LibraryRescanCoordinator(
    IServiceScopeFactory scopeFactory, Library library, ILogger<LibraryRescanCoordinator> logger)
{
    private readonly object _lock = new();
    private Task? _running;

    public bool IsRunning
    {
        get
        {
            lock (_lock)
                return _running is { IsCompleted: false };
        }
    }

    // The scan in flight, or a finished task when there is none - for a caller
    // that has to wait for one rather than poll.
    public Task Running
    {
        get
        {
            lock (_lock)
                return _running ?? Task.CompletedTask;
        }
    }

    // As a scan starts, and Completed as it ends: Vibe Playlists' analysis
    // waits out every scan (SemanticAudioEngine.Pause), since a scan rewrites
    // the catalog its plan is made from and both want the disk.
    public event Action? Started;

    // After every scan, startup's included, whether or not it succeeded - the
    // folder watcher re-reads the folder list here, since a scan is what every
    // change to that list ends in (the settings endpoint starts one, and the
    // startup scan may adopt Music.app's folder).
    public event Action? Completed;

    // How far the scan in flight has got: songs read of all it found. Null
    // while none runs, and while one is still listing the folders - the admin
    // API's answer to how long a NAS scan has left (GET /api/admin/library).
    public SongProgress? Progress
    {
        get
        {
            lock (_lock)
                return _progress;
        }
    }

    private SongProgress? _progress;

    // The log's version of the same: a line once the folders are listed, then
    // one every this often, so an owner tailing a minutes-long scan sees it
    // moving rather than a server gone quiet.
    private static readonly TimeSpan ProgressLogInterval = TimeSpan.FromSeconds(30);

    public DateTimeOffset? LastCompletedAt { get; private set; }
    public string? LastError { get; private set; }
    public int TrackCount => library.Tracks.Count;

    // Returns false when one was already in flight - the caller reports that as
    // "already running" rather than as an error, since the outcome the user wants
    // (a scan is happening) is true either way.
    //
    // onCompleted is for the startup scan, which unlike an admin-triggered one
    // has something waiting on it (see Program.cs: the smart-playlist refresher
    // wants the real catalog before its opening pass). A caller that passes it
    // and gets false back was not the one that started the scan, so it is also
    // not the one whose callback will run.
    public bool TryStart(Action? onCompleted = null)
    {
        lock (_lock)
        {
            if (_running is { IsCompleted: false })
                return false;

            // Raised before the scan exists, so a listener never hears its
            // Completed first. The previous scan's Completed has already been
            // raised: it runs before that scan's task ends, and until then
            // this method refuses to start another.
            try
            {
                Started?.Invoke();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Pre-rescan step failed");
            }

            _running = Task.Run(() => RunAsync(onCompleted));
            return true;
        }
    }

    private async Task RunAsync(Action? onCompleted)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<LibraryImportService>()
                .RescanAsync(progress: new ScanProgress(this, logger));
            LastError = null;
        }
        catch (Exception ex)
        {
            // Swallowed rather than rethrown into an unobserved background task:
            // the operator asked for this from a browser, so the failure belongs
            // in the status the browser is already polling, not only in the log.
            LastError = ex.Message;
            logger.LogError(ex, "Library rescan failed");
        }
        finally
        {
            lock (_lock)
                _progress = null;
            LastCompletedAt = DateTimeOffset.UtcNow;

            // In the finally, so a scan that threw still releases what was
            // waiting on it: the stored catalog is there either way, and a
            // server whose scan failed should still have smart playlists over
            // it. Guarded for the same reason the scan itself is - this runs on
            // a background task with nobody to observe what it throws.
            try
            {
                onCompleted?.Invoke();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Post-rescan startup step failed");
            }
            try
            {
                Completed?.Invoke();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Post-rescan step failed");
            }
        }
    }

    // Reported on the scanning thread after every file, so the cost per report
    // is a lock and, every thirty seconds, a log line.
    private sealed class ScanProgress(LibraryRescanCoordinator owner, ILogger logger) : IProgress<SongProgress>
    {
        private readonly Stopwatch _sinceLogged = Stopwatch.StartNew();

        public void Report(SongProgress value)
        {
            lock (owner._lock)
                owner._progress = value;

            if (value.Done == 0)
            {
                logger.LogInformation("Rescan found {Total} song file(s) to read", value.Total);
                _sinceLogged.Restart();
            }
            else if (value.Done < value.Total && _sinceLogged.Elapsed >= ProgressLogInterval)
            {
                logger.LogInformation("Rescan has read {Done} of {Total} song file(s)", value.Done, value.Total);
                _sinceLogged.Restart();
            }
        }
    }
}
