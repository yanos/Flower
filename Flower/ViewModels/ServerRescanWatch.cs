using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Flower.Importer;
using Flower.Services;

namespace Flower.ViewModels;

// A server's rescan, followed into this app's status bar by the administrator
// who started it - from the Rescan button, or by saving a changed folder list.
// The scan runs on the server, minutes of it over a NAS share, and the settings
// screen that started it has usually closed by then (saving closes it), so the
// status bar is the one place left that can say how far it has got.
//
// Polls GET /api/admin/library while the server says it is rescanning, and
// stops at the first answer that says it is not, or that does not come.
public sealed class ServerRescanWatch(
    Func<CancellationToken, Task<AdminLibraryStatusDto>> getStatus,
    Func<string?, IBusyScope>? beginBusyScope,
    string what,
    TimeSpan? pollInterval = null,
    ILogger? logger = null)
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);

    private readonly TimeSpan _pollInterval = pollInterval ?? DefaultPollInterval;
    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private int _following;

    // Returns once the scan has ended. A second call while one is following
    // does nothing: it is the same scan, the server runs one at a time.
    //
    // Safe to call when no scan was started: the indicator goes up only once
    // the server says one is running, so a save that changed nothing a scan
    // depends on costs one request and shows nothing.
    public async Task FollowAsync(CancellationToken ct = default)
    {
        if (beginBusyScope == null || Interlocked.Exchange(ref _following, 1) == 1)
            return;

        IBusyScope? busy = null;
        try
        {
            IProgress<SongProgress>? counting = null;
            while (true)
            {
                var status = await getStatus(ct);
                if (!status.Rescanning)
                    return;
                busy ??= beginBusyScope($"{what}…");
                counting ??= busy.CountingSongs(what);
                if (status is { SongsScanned: { } done, SongsToScan: { } total })
                    counting.Report(new SongProgress(done, total));
                await Task.Delay(_pollInterval, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // The scan goes on without us; only the count in the status bar
            // is lost, and the server's own log still has it.
            _logger.LogDebug(ex, "Stopped following the server's rescan");
        }
        finally
        {
            busy?.Dispose();
            Interlocked.Exchange(ref _following, 0);
        }
    }
}
