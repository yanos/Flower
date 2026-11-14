using Flower.Views;

namespace Flower.Tests.ViewModels.Settings;

public class ServerRowTests
{
    // A row is a live object, updated in place, rather than a snapshot replaced
    // on every change - so the control bound to it is the same instance from one
    // Refresh to the next. ConnectionStatusIcon holds a short sync's spinner on
    // the instance that raised it, and a replaced row throws that holder away.
    // What that needs from the row itself is that the properties a sync moves
    // are settable and say so.
    [Fact]
    public void A_row_told_a_sync_started_reports_itself_busy()
    {
        var row = new ServerRow
        {
            Fingerprint = "fp-nas",
            Alias = "NAS",
            IsPaired = true,
            IsSyncing = false,
            IsTrustConfirmed = true,
            BlockedByAlias = null,
        };

        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        row.IsSyncing = true;

        Assert.True(row.IsBusy);
        Assert.Contains(nameof(ServerRow.IsBusy), changed);
    }
}
