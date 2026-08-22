using System.Collections.Concurrent;

using Flower.Models;
using Flower.Services;

namespace Flower.Server.Services;

// Applies the play reports a browser tab posts to POST /api/flower/v1/plays -
// see OriginPlayReporter for why a tab reports events rather than totals.
//
// A DI singleton rather than statics on the endpoint class, for the same
// reason LibraryManifestCache is one: the ids it remembers belong to one
// Library's history, and a static would be shared between the several hosts a
// test run boots in one process.
public sealed class PlayReportService(Library library, ILogger<PlayReportService> logger)
{
    // How long an applied event id is remembered. Long enough to cover a
    // retry - the reporter re-sends a failed batch with the next play, which
    // in the worst case is however long the listener leaves the tab paused -
    // and short enough that the set cannot grow without bound on a server
    // that has been up for weeks. An id that falls out is not "safe to reuse":
    // nothing reuses one, this only bounds how long a retry stays free.
    private static readonly TimeSpan RetainFor = TimeSpan.FromHours(6);

    // Deliberately not NonceReplayGuard, despite the identical shape. That one
    // is a security control tied to SignatureVerifier's timestamp window and
    // must keep its own short retention; this is a correctness control for
    // non-idempotent increments and needs a far longer one. Sharing the
    // instance would have made either window wrong for the other.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _applied = new();

    // The number of events that changed something, for the caller to log. An
    // event naming a track this server does not have, and one already applied,
    // are both fine and both simply do not count.
    public int Apply(PlayReportDto report, DateTimeOffset now)
    {
        Prune(now);

        var applied = 0;
        foreach (var play in report.Plays)
        {
            var change = default(TrackStatsChange);
            if (play.Started)
                change |= TrackStatsChange.Started;
            if (play.Completed)
                change |= TrackStatsChange.Finished;

            // Neither half set says nothing happened. Dropped before the id is
            // recorded, so it cannot burn an id that a real event might reuse.
            if (change == default)
                continue;

            if (!_applied.TryAdd(play.EventId, now))
            {
                logger.LogDebug("Ignoring play event {EventId}, already applied", play.EventId);
                continue;
            }

            if (library.RecordPlay(play.TrackId, change))
            {
                applied++;
            }
            else
            {
                logger.LogDebug(
                    "Ignoring a play of {TrackId}: this server has no such track", play.TrackId);
            }
        }

        return applied;
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var (id, appliedAt) in _applied)
        {
            if (now - appliedAt > RetainFor)
                _applied.TryRemove(id, out _);
        }
    }
}
