using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;

using AvaloniaEdit;

using Flower.Logging;
using Flower.Persistence;
using Flower.ViewModels;
using Flower.Views;

using Serilog.Events;

using Xunit;

namespace Flower.Tests;

// The Logs tab of a *server's* settings - the one screen that shows everybody's
// log: the server's own, and the last snapshot each device on its roster pushed
// to it (see ClientLogStore on the receiving side). A client's own Log window
// deliberately shows only itself, so this is where the rows that used to be in
// that window's sidebar went - and, since it is the same LogViewerViewModel
// behind both, they arrive with the filtering and level picker the flat
// 500-line TextBox here never had.
public class SettingsLogTabTests
{
    // Answers the log calls from dictionaries, with an optional gate so a test
    // can hold one fetch open while the selection moves. Everything else on
    // ISettingsBackend belongs to tabs these tests never touch.
    private sealed class FakeBackend : ISettingsBackend
    {
        // Settable so one test can pose as the app's own settings screen, which
        // is the other half of the remembered-tab pair.
        public SettingsCapabilities Capabilities { get; set; } = new() { Log = true, TrustedDevices = true };

        public List<TrustedPeerRow> Devices { get; } = [];
        public List<InMemoryLogEntry> ServerLog { get; set; } = [];

        // A fingerprint absent from here has never pushed - the null case.
        public Dictionary<string, List<InMemoryLogEntry>> DeviceLogs { get; } = new();

        // Holds the fetch for exactly one fingerprint open, so a test can let a
        // later selection finish first and then release the earlier one - the
        // only ordering in which "the answer for the row we left" can actually
        // race the row we are on.
        public string? GatedFingerprint { get; set; }
        public TaskCompletionSource Gate { get; } = new();

        // Set to have the next server-log read fail, the way a dropped request
        // on a two-second poll does.
        public bool ServerLogFails { get; set; }

        // Sequenced by position, which is all InMemoryLogStore's own numbering
        // amounts to for a log that is only ever appended to: everything past
        // afterSequence, and the index of the last line as the cursor to come
        // back with.
        public Task<LogSlice> LoadLogAsync(int limit, long afterSequence, CancellationToken ct = default)
        {
            if (ServerLogFails)
                throw new InvalidOperationException("the server went away");

            var entries = ServerLog.Skip((int)(afterSequence + 1)).ToList();
            return Task.FromResult(new LogSlice(ServerLog.Count - 1, entries));
        }

        public async Task<IReadOnlyList<InMemoryLogEntry>?> LoadDeviceLogAsync(string fingerprint, int limit, CancellationToken ct = default)
        {
            if (fingerprint == GatedFingerprint)
                await Gate.Task;
            return DeviceLogs.TryGetValue(fingerprint, out var lines) ? lines : null;
        }

        public Task<IReadOnlyList<TrustedPeerRow>> LoadDevicesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TrustedPeerRow>>(Devices);

        public Task<IReadOnlyList<DeniedPeerRow>> LoadDeniedDevicesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DeniedPeerRow>>([]);

        public Task<SettingsSnapshot> LoadAsync(CancellationToken ct = default) => Task.FromResult(new SettingsSnapshot());
        public Task<string?> SaveAsync(SettingsDraft draft, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public int CountSongsUnder(string folder) => -1;
        public Task ForgetDeviceAsync(TrustedPeerRow device, CancellationToken ct = default) => Task.CompletedTask;
        public Task ForgetDenialAsync(DeniedPeerRow device, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> IssuePairingCodeAsync(bool grantsAdmin, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<SubsonicCredentialRow>> LoadSubsonicCredentialsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SubsonicCredentialRow>>([]);

        public Task<SubsonicCredentialRow> IssueSubsonicCredentialAsync(string label, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RevokeSubsonicCredentialAsync(SubsonicCredentialRow credential, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RescanAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task RebuildDatabaseAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private readonly FakeBackend _backend = new();

    // Settings but no store: where the screen was left is read and written
    // through this, and the tests that care assert against it directly - a real
    // store here would write to the developer's own settings.json.
    private readonly AppSettings _appSettings = new();

    private SettingsViewModel _panel = null!;

    private static TrustedPeerRow Device(string fingerprint, string alias) =>
        new() { Fingerprint = fingerprint, Alias = alias, ApprovedAt = DateTimeOffset.UtcNow };

    private static List<InMemoryLogEntry> Lines(params string[] messages) =>
        messages.Select(m => new InMemoryLogEntry(DateTimeOffset.UtcNow, "Information", null, m, null)).ToList();

    private async Task<SettingsViewModel> MakeAsync(params TrustedPeerRow[] devices)
    {
        _backend.Devices.AddRange(devices);
        _panel = new SettingsViewModel(_backend, _appSettings);
        // Loading the roster is also what fills the Logs tab's list and lands
        // the selection on the server.
        await _panel.RefreshDevicesAsync();
        await WaitForAsync(() => _panel.LogViewer.DisplayLines.Count > 0);
        return _panel;
    }

    // Selecting a row starts a fetch that is not awaited from the setter (the
    // View has nothing to await it with), so tests wait for its effect.
    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition() && Environment.TickCount64 < deadline)
            await Task.Delay(5);
        Assert.True(condition(), "the expected log fetch never landed");
    }

    // Gives an already-released fetch every chance to land, asserting nothing -
    // for "this must NOT happen".
    private static async Task DrainAsync(int milliseconds)
    {
        var deadline = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < deadline)
            await Task.Delay(5);
    }

    private async Task SelectAsync(string? fingerprint)
    {
        _panel.SelectedLogSource = _panel.LogSources.First(s => s.Fingerprint == fingerprint);
        await WaitForAsync(() => !_panel.LogViewer.DisplayLines.SequenceEqual(new[] { "(loading...)" }));
    }

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

    // A device that has never pushed gets an explicit sentence, not a blank
    // pane indistinguishable from "connected but silent".
    [Fact]
    public async Task A_device_with_no_snapshot_yet_says_so()
    {
        var panel = await MakeAsync(Device("fp-1", "A Phone"));

        await SelectAsync("fp-1");

        var line = Assert.Single(panel.LogViewer.DisplayLines);
        Assert.Contains("no log snapshot", line);
        Assert.Contains("A Phone", line);
    }

    // Distinct from the sentence above: a device that pushed an empty log has
    // been heard from, and reading one as the other sends the reader looking in
    // the wrong place.
    [Fact]
    public async Task A_device_that_pushed_an_empty_log_is_not_confused_with_one_that_never_pushed()
    {
        _backend.DeviceLogs["fp-1"] = [];
        var panel = await MakeAsync(Device("fp-1", "A Phone"));

        await SelectAsync("fp-1");

        Assert.DoesNotContain("no log snapshot", Assert.Single(panel.LogViewer.DisplayLines));
    }

    [Fact]
    public async Task An_empty_server_log_says_so_rather_than_looking_broken()
    {
        var panel = await MakeAsync();

        Assert.Contains("logged nothing yet", Assert.Single(panel.LogViewer.DisplayLines));
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

    // The point of putting the app's viewer here rather than a flat tail: the
    // level picker and the filter box work on a server's log exactly as they do
    // on a local one.
    [Fact]
    public async Task The_minimum_level_and_filter_apply_to_a_servers_log_too()
    {
        _backend.ServerLog =
        [
            new InMemoryLogEntry(DateTimeOffset.UtcNow, "Debug", null, "a debug line", null),
            new InMemoryLogEntry(DateTimeOffset.UtcNow, "Warning", null, "a warning line", null),
        ];
        var panel = await MakeAsync();

        panel.LogViewer.MinimumLevel = LogEventLevel.Warning;
        Assert.Contains("a warning line", Assert.Single(panel.LogViewer.DisplayLines));

        panel.LogViewer.MinimumLevel = LogEventLevel.Verbose;
        panel.LogViewer.FilterText = "debug";
        Assert.Contains("a debug line", Assert.Single(panel.LogViewer.DisplayLines));
    }

    // Loads the real XAML for the viewer control the Logs tab hosts, and pins
    // the behaviour that only shows up once it is on screen: it paints what the
    // ViewModel already holds when it attaches. The tab is realized long after
    // the log was fetched, so a control that waited for the next event would sit
    // blank until something changed.
    [AvaloniaFact]
    public async Task The_viewer_paints_the_lines_that_were_loaded_before_it_came_on_screen()
    {
        _backend.ServerLog = Lines("a line loaded before the pane existed");
        var panel = await MakeAsync();

        var viewer = new LogViewer { DataContext = panel.LogViewer };
        var window = new Window { Content = viewer, Width = 800, Height = 600 };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var editor = viewer.GetVisualDescendants().OfType<TextEditor>().Single();
        Assert.Contains("a line loaded before the pane existed", editor.Text);

        window.Close();
    }

    // Everything below is the tab following a live log rather than waiting to be
    // asked. SettingsPanel ticks FollowLogAsync on a timer while the Logs tab is
    // the one on screen; these call the tick directly, since waiting out real
    // two-second intervals would be testing DispatcherTimer instead.

    // AvaloniaFact for the Dispatcher alone: the viewer coalesces appended
    // entries on a background dispatch (see LogViewerViewModel.Append), so
    // without one to run there is nothing to coalesce them onto.
    [AvaloniaFact]
    public async Task A_poll_appends_only_what_has_been_logged_since()
    {
        _backend.ServerLog = Lines("first");
        var panel = await MakeAsync();

        _backend.ServerLog.AddRange(Lines("second"));
        await panel.FollowLogAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, panel.LogViewer.DisplayLines.Count);
        Assert.Contains("first", panel.LogViewer.DisplayLines[0]);
        Assert.Contains("second", panel.LogViewer.DisplayLines[1]);
    }

    // The reader may be scrolled somewhere in the middle of a long log. A tick
    // that finds nothing must not repaint the document, which is what would drag
    // them back to the bottom - see LogViewer's LinesReset handler.
    [Fact]
    public async Task A_poll_that_finds_nothing_leaves_the_document_alone()
    {
        _backend.ServerLog = Lines("first");
        var panel = await MakeAsync();

        var resets = 0;
        panel.LogViewer.LinesReset += (_, _) => resets++;
        await panel.FollowLogAsync();

        Assert.Equal(0, resets);
        Assert.Single(panel.LogViewer.DisplayLines);
    }

    // The first line to arrive after "(the server has logged nothing yet)" has
    // to replace that sentence, not land underneath it.
    [Fact]
    public async Task The_first_line_after_an_empty_log_replaces_the_placeholder()
    {
        var panel = await MakeAsync();
        Assert.Contains("logged nothing yet", Assert.Single(panel.LogViewer.DisplayLines));

        _backend.ServerLog = Lines("the first thing that happened");
        await panel.FollowLogAsync();

        Assert.Contains("the first thing that happened", Assert.Single(panel.LogViewer.DisplayLines));
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

    // Reopening the settings screen has to come back to what was being read,
    // not to the top of the list: the reader closed it because they were done
    // for the moment, not because they were finished with that device.

    [Fact]
    public async Task The_log_comes_back_to_the_device_that_was_being_read()
    {
        _backend.DeviceLogs["fp-2"] = Lines("from the second phone");
        var panel = await MakeAsync(Device("fp-1", "One"), Device("fp-2", "Two"));
        await SelectAsync("fp-2");

        // A second screen over the same settings - the same thing reopening the
        // panel does, since it is built fresh each time.
        var reopened = new SettingsViewModel(_backend, _appSettings);
        await reopened.RefreshDevicesAsync();

        Assert.Equal("fp-2", reopened.SelectedLogSource?.Fingerprint);
    }

    // Remembered by fingerprint rather than by position, so the row that now
    // sits where a forgotten device used to does not inherit its place.
    [Fact]
    public async Task A_remembered_device_that_is_gone_falls_back_to_the_server()
    {
        var panel = await MakeAsync(Device("fp-1", "One"), Device("fp-2", "Two"));
        await SelectAsync("fp-2");

        _backend.Devices.RemoveAll(d => d.Fingerprint == "fp-2");
        var reopened = new SettingsViewModel(_backend, _appSettings);
        await reopened.RefreshDevicesAsync();

        Assert.Null(reopened.SelectedLogSource?.Fingerprint);
    }

    // The list is cleared and rebuilt on every roster load, and a bound ListBox
    // pushes null through the selection while that happens. Remembering that
    // null would throw away the choice a moment before it is restored.
    [Fact]
    public async Task Clearing_the_selection_does_not_forget_the_remembered_one()
    {
        var panel = await MakeAsync(Device("fp-1", "One"));
        await SelectAsync("fp-1");

        panel.SelectedLogSource = null;
        await panel.RefreshDevicesAsync();

        Assert.Equal("fp-1", panel.SelectedLogSource?.Fingerprint);
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
        await panel.RefreshDevicesAsync();

        Assert.Equal(0, resets);
        Assert.Equal("fp-1", panel.SelectedLogSource?.Fingerprint);
    }

    // A server's screen and the app's own are different screens with different
    // tabs - one key for both would land somebody on "Logs" in a screen that
    // has no Logs tab.
    [Fact]
    public void A_servers_tab_and_this_devices_tab_are_remembered_separately()
    {
        var server = new SettingsViewModel(_backend, _appSettings);
        server.RememberedTab = "LogsTab";

        var own = new SettingsViewModel(
            new FakeBackend { Capabilities = new SettingsCapabilities { PairedServerPicker = true } },
            _appSettings);

        Assert.Equal("", own.RememberedTab);
        own.RememberedTab = "LibraryTab";
        Assert.Equal("LogsTab", server.RememberedTab);
    }

    // The panel end of it: the remembered name has to survive the trip through
    // the real XAML, whose TabItems are what those names name.
    [AvaloniaFact]
    public void The_panel_opens_on_the_tab_it_was_left_on()
    {
        _appSettings.ServerSettingsTab = "NetworkTab";
        var settings = new SettingsViewModel(
            new FakeBackend { Capabilities = new SettingsCapabilities { Log = true, ServerNetwork = true } },
            _appSettings);

        var panel = new SettingsPanel(settings);
        var window = new Window { Content = panel, Width = 900, Height = 700 };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var tabs = panel.GetVisualDescendants().OfType<TabControl>().First();
        Assert.Equal("NetworkTab", (tabs.SelectedItem as TabItem)?.Name);

        window.Close();
    }

    // A tab this screen does not have is ignored rather than corrected: the same
    // person administers a server from a browser and their own app on the
    // desktop, and neither should be able to strand the other.
    [AvaloniaFact]
    public void A_remembered_tab_the_screen_does_not_have_is_ignored()
    {
        _appSettings.ServerSettingsTab = "NetworkTab";
        var settings = new SettingsViewModel(
            new FakeBackend { Capabilities = new SettingsCapabilities() }, _appSettings);

        var panel = new SettingsPanel(settings);
        var window = new Window { Content = panel, Width = 900, Height = 700 };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var tabs = panel.GetVisualDescendants().OfType<TabControl>().First();
        Assert.Equal("GeneralTab", (tabs.SelectedItem as TabItem)?.Name);

        window.Close();
    }
}
