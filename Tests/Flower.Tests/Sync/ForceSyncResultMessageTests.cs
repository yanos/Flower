using Flower.Services;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests.Sync;

// Result wording can be checked without discovery, a dispatcher or an app shell.
public class ForceSyncResultMessageTests
{
    [Fact]
    public void A_sync_that_fetched_new_tracks_reports_how_many()
    {
        var result = new LibrarySyncResult(Success: true, FetchedCount: 40, AddedCount: 3);

        var message = ForceSyncResultMessage.Format(result, "Living Room");

        Assert.Equal("Added 3 new track(s) from Living Room", message);
    }

    // A 304 from the server: its catalog is exactly what was merged last time,
    // so there is no fetched count worth reporting.
    [Fact]
    public void A_server_that_answered_unchanged_reports_no_counts_at_all()
    {
        var result = new LibrarySyncResult(Success: true, FetchedCount: 0, AddedCount: 0, Unchanged: true);

        var message = ForceSyncResultMessage.Format(result, "Living Room");

        Assert.Equal("Already up to date with Living Room", message);
    }

    [Fact]
    public void A_sync_that_checked_tracks_but_added_none_says_how_many_it_checked()
    {
        var result = new LibrarySyncResult(Success: true, FetchedCount: 40, AddedCount: 0);

        var message = ForceSyncResultMessage.Format(result, "Living Room");

        Assert.Equal("Already up to date with Living Room (40 track(s) checked)", message);
    }

    // Distinguish a peer that stopped answering from one that answered with
    // an unusable catalog. The legacy bare failure still means unreachable.
    [Theory]
    [InlineData(SyncFailure.None, "Could not reach Living Room - check it's still on the network and paired")]
    [InlineData(SyncFailure.Unreachable, "Could not reach Living Room - check it's still on the network and paired")]
    [InlineData(SyncFailure.Refused, "Living Room answered, but not with a catalog - see the log for what it said")]
    public void A_failed_sync_explains_what_to_do_next(SyncFailure failure, string expected)
    {
        var result = new LibrarySyncResult(Success: false, FetchedCount: 0, AddedCount: 0, Failure: failure);

        var message = ForceSyncResultMessage.Format(result, "Living Room");

        Assert.Equal(expected, message);
    }

    // A server that answered, promptly, with 429. The old message sent the user
    // to go and check a network that was working, next to a connection icon
    // that was correctly showing the server as reachable - and the natural next
    // move, unpair and pair again, spends more of the very budget that ran out.
    // See SyncEndpoints.BulkLimiter: sixty bulk requests a minute, and a
    // handful of app restarts while debugging is enough to exhaust it.
    [Fact]
    public void A_throttled_sync_says_to_wait_rather_than_to_check_the_network()
    {
        var result = new LibrarySyncResult(
            Success: false, FetchedCount: 0, AddedCount: 0, Failure: SyncFailure.Throttled);

        var message = ForceSyncResultMessage.Format(result, "Living Room");

        Assert.Equal("Living Room is asking for fewer requests just now - it should catch up on its own in a minute",
            message);
    }

    // Reached and refused. The action is to pair again, which is the one case
    // where the old message's "and paired" half was pointing somewhere useful -
    // buried in a sentence that was mostly about the network.
    [Fact]
    public void A_refused_sync_says_the_pairing_is_what_is_wrong()
    {
        var result = new LibrarySyncResult(
            Success: false, FetchedCount: 0, AddedCount: 0, Failure: SyncFailure.NotTrusted);

        var message = ForceSyncResultMessage.Format(result, "Living Room");

        Assert.Equal("Living Room refused this device - it may need pairing again",
            message);
    }
}
