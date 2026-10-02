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

    // And how many one device may have remembered at once, oldest dropped
    // first. Time alone did not bound it: a paired device choosing fresh ids
    // could add a report's worth every request for six hours. Ten reporters'
    // full backlogs, far past a tab's real traffic of a few events a track -
    // and a device that runs past it only loses the retry-safety of its own
    // oldest plays.
    internal const int MaxRememberedPerDevice = PlayReportDto.MaxEvents * 10;

    // Deliberately not NonceReplayGuard, despite the similar shape. That one
    // is a security control tied to SignatureVerifier's timestamp window and
    // must keep its own short retention; this is a correctness control for
    // non-idempotent increments and needs a far longer one. Sharing the
    // instance would have made either window wrong for the other.
    //
    // Per device, so one device's ids can neither crowd out nor pre-empt
    // another's.
    private readonly Dictionary<string, Remembered> _applied = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    private sealed class Remembered
    {
        public readonly Dictionary<string, DateTimeOffset> At = new(StringComparer.Ordinal);
        public readonly Queue<string> Order = new();
    }

    // The number of events that changed something, for the caller to log. An
    // event naming a track this server does not have, and one already applied,
    // are both fine and both simply do not count.
    //
    // deviceFingerprint is the one the signature proved. An admin device's
    // plays are the owner's and land in this library's own PlayCount and
    // LastPlayed, as every tab's used to; anyone else's are filed under the
    // device that made them (Library.RecordPlayFor).
    public int Apply(PlayReportDto report, string deviceFingerprint, bool callerIsAdmin, DateTimeOffset now)
    {
        var applied = 0;
        foreach (var play in report.Plays)
        {
            var change = default(TrackChange);
            if (play.Started)
                change |= TrackChange.PlayStarted;
            if (play.Completed)
                change |= TrackChange.PlayFinished;

            // Neither half set says nothing happened. Dropped before the id is
            // recorded, so it cannot burn an id that a real event might reuse.
            if (change == default)
                continue;

            if (!TryRemember(deviceFingerprint, play.EventId, now))
            {
                logger.LogDebug("Ignoring play event {EventId}, already applied", play.EventId);
                continue;
            }

            var recorded = callerIsAdmin
                ? library.RecordPlay(play.TrackId, change)
                : change.HasFlag(TrackChange.PlayFinished) && library.RecordPlayFor(play.TrackId, deviceFingerprint);

            if (recorded)
            {
                applied++;
            }
            else
            {
                logger.LogDebug(
                    "Nothing to record for play event {EventId} of {TrackId}: no such track here, or a start from a device that is not an admin",
                    play.EventId, play.TrackId);
            }
        }

        return applied;
    }

    private bool TryRemember(string fingerprint, string eventId, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (!_applied.TryGetValue(fingerprint, out var remembered))
                _applied[fingerprint] = remembered = new Remembered();

            // Oldest first in Order, so expiry and the size cap both trim from
            // the front.
            while (remembered.Order.TryPeek(out var oldest)
                   && (remembered.Order.Count >= MaxRememberedPerDevice || now - remembered.At[oldest] > RetainFor))
            {
                remembered.At.Remove(remembered.Order.Dequeue());
            }

            if (!remembered.At.TryAdd(eventId, now))
                return false;

            remembered.Order.Enqueue(eventId);
            return true;
        }
    }
}
