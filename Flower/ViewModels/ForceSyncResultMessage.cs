using Flower.Services;

namespace Flower.ViewModels;

// The outcome shown beside Sync Now, independent of discovery and sync execution.
internal static class ForceSyncResultMessage
{
    public static string Format(LibrarySyncResult result, string alias)
    {
        if (!result.Success)
            return DescribeFailure(result.Failure, alias);

        // Unchanged means the server answered 304 - its catalog is exactly
        // what was merged last time, so there is no fetched count to report.
        if (result.Unchanged)
            return $"Already up to date with {alias}";

        return result.AddedCount > 0
            ? $"Added {result.AddedCount} new track(s) from {alias}"
            : $"Already up to date with {alias} ({result.FetchedCount} track(s) checked)";
    }

    // What to tell the user, in terms of what they should do next. This used to
    // be one sentence for every failure - "could not reach X, check it's still
    // on the network and paired" - which was actively misleading for three of
    // the four cases: the server was reached, the connection icon beside this
    // line was correctly showing it as reachable, and the advice was to go and
    // check a network that was fine.
    //
    // Throttled is the one worth naming most precisely. The budget rolls over
    // by itself (see SyncEndpoints.BulkLimiter), so the whole of the required
    // action is to wait, and saying so stops the next instinct - unpair and
    // pair again - which spends more of the same budget.
    public static string DescribeFailure(SyncFailure failure, string alias) => failure switch
    {
        SyncFailure.Throttled =>
            $"{alias} is asking for fewer requests just now - it should catch up on its own in a minute",
        SyncFailure.NotTrusted =>
            $"{alias} refused this device - it may need pairing again",
        SyncFailure.Refused =>
            $"{alias} answered, but not with a catalog - see the log for what it said",
        _ =>
            $"Could not reach {alias} - check it's still on the network and paired",
    };
}
