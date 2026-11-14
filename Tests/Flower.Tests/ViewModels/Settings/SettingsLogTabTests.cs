using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Flower.Logging;
using Flower.Persistence;
using Flower.ViewModels;

using Serilog.Events;

using Xunit;

namespace Flower.Tests.ViewModels.Settings;

// The Logs tab of a *server's* settings - the one screen that shows everybody's
// log: the server's own, and the last snapshot each device on its roster pushed
// to it (see ClientLogStore on the receiving side). A client's own Log window
// deliberately shows only itself, so this is where the rows that used to be in
// that window's sidebar went - and, since it is the same LogViewerViewModel
// behind both, they arrive with the filtering and level picker the flat
// 500-line TextBox here never had.
public class SettingsLogTabTests : Flower.Tests.TestSupport.SettingsLogTabHarness
{
    [Fact]
    public async Task The_server_is_listed_first_then_one_row_per_device()
    {
        var panel = await MakeAsync(Device("fp-1", "Alias1"), Device("fp-2", "Alias2"));

        Assert.Equal(3, panel.LogSources.Count);
        Assert.Null(panel.LogSources[0].Fingerprint);
        Assert.Equal("Alias1", panel.LogSources[1].Name);
        Assert.Equal("fp-2", panel.LogSources[2].Fingerprint);
        Assert.Same(panel.LogSources[0], panel.SelectedLogSource);
    }

    [Fact]
    public async Task The_server_row_shows_the_servers_own_log()
    {
        _backend.ServerLog = Lines("server line");
        var panel = await MakeAsync();

        Assert.Contains("server line", Assert.Single(panel.LogViewer.DisplayLines));
    }

    [Fact]
    public async Task Selecting_a_device_shows_its_pushed_snapshot()
    {
        _backend.DeviceLogs["fp-1"] = Lines("client line one", "client line two");
        var panel = await MakeAsync(Device("fp-1", "A Phone"));

        await SelectAsync("fp-1");

        Assert.Equal(2, panel.LogViewer.DisplayLines.Count);
        Assert.Contains("client line one", panel.LogViewer.DisplayLines[0]);
    }

    // A fetch for a row the reader has already moved away from must not paint
    // its lines under the new selection - the failure this guards against is
    // one device's log appearing under another device's name. fp-1's fetch is
    // held open until *after* fp-2's has already landed, which is the only
    // order in which the stale answer can overwrite the current one.
    [Fact]
    public async Task A_fetch_that_lands_after_the_selection_moved_is_discarded()
    {
        _backend.DeviceLogs["fp-1"] = Lines("belongs to fp-1");
        _backend.DeviceLogs["fp-2"] = Lines("belongs to fp-2");
        var panel = await MakeAsync(Device("fp-1", "One"), Device("fp-2", "Two"));

        _backend.GatedFingerprint = "fp-1";
        panel.SelectedLogSource = panel.LogSources.First(s => s.Fingerprint == "fp-1");
        await SelectAsync("fp-2");
        Assert.Contains("belongs to fp-2", Assert.Single(panel.LogViewer.DisplayLines));

        _backend.Gate.SetResult();
        await DrainAsync(150);

        Assert.Contains("belongs to fp-2", Assert.Single(panel.LogViewer.DisplayLines));
    }

    // A poll is a background read the reader never asked for, so a failed one is
    // silent: on a two-second timer it is far more likely to be one dropped
    // request than anything worth replacing a readable log with.
    [Fact]
    public async Task A_failed_poll_leaves_the_log_on_screen()
    {
        _backend.ServerLog = Lines("still worth reading");
        var panel = await MakeAsync();

        _backend.ServerLogFails = true;
        await panel.FollowLogAsync();

        Assert.Contains("still worth reading", Assert.Single(panel.LogViewer.DisplayLines));
    }

    // A device's log is not a delta - it is a merged history re-read whole - so
    // an unchanged one must not be repainted for the same reason as above.
    [Fact]
    public async Task Polling_a_device_repaints_only_when_its_snapshot_grew()
    {
        _backend.DeviceLogs["fp-1"] = Lines("pushed once");
        var panel = await MakeAsync(Device("fp-1", "A Phone"));
        await SelectAsync("fp-1");

        var resets = 0;
        panel.LogViewer.LinesReset += (_, _) => resets++;

        await panel.FollowLogAsync();
        Assert.Equal(0, resets);

        _backend.DeviceLogs["fp-1"].AddRange(Lines("pushed again"));
        await panel.FollowLogAsync();

        Assert.Equal(1, resets);
        Assert.Equal(2, panel.LogViewer.DisplayLines.Count);
    }

    // The screen is kept rather than rebuilt now (MainViewModel._serverSettings),
    // so its catch-up load runs against a roster that is usually identical. It
    // must leave the log where it is - repainting the document would drag a
    // reader who was scrolled up somewhere back to the bottom for nothing.
    [Fact]
    public async Task Coming_back_to_an_unchanged_roster_does_not_reload_the_log()
    {
        _backend.DeviceLogs["fp-1"] = Lines("still being read");
        var panel = await MakeAsync(Device("fp-1", "One"));
        await SelectAsync("fp-1");

        var resets = 0;
        panel.LogViewer.LinesReset += (_, _) => resets++;
        await panel.RefreshDevicesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, resets);
        Assert.Equal("fp-1", panel.SelectedLogSource?.Fingerprint);
    }
}
