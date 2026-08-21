using Flower.Models;

namespace Flower.Server.Services;

// Owns "is a rescan running right now", so the admin API can start one and the
// browser can poll it, and so two operators clicking Rescan at the same moment
// do not put two importers over the same folders.
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

    public DateTimeOffset? LastCompletedAt { get; private set; }
    public string? LastError { get; private set; }
    public int TrackCount => library.Tracks.Count;

    // Returns false when one was already in flight - the caller reports that as
    // "already running" rather than as an error, since the outcome the user wants
    // (a scan is happening) is true either way.
    public bool TryStart()
    {
        lock (_lock)
        {
            if (_running is { IsCompleted: false })
                return false;

            _running = Task.Run(RunAsync);
            return true;
        }
    }

    private async Task RunAsync()
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<LibraryImportService>().RescanAsync();
            LastError = null;
        }
        catch (Exception ex)
        {
            // Swallowed rather than rethrown into an unobserved background task:
            // the operator asked for this from a browser, so the failure belongs
            // in the status the browser is already polling, not only in the log.
            LastError = ex.Message;
            logger.LogError(ex, "Admin-triggered library rescan failed");
        }
        finally
        {
            LastCompletedAt = DateTimeOffset.UtcNow;
        }
    }
}
