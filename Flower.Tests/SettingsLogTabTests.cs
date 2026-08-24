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
        public SettingsCapabilities Capabilities { get; } = new() { Log = true, TrustedDevices = true };

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

        public Task<IReadOnlyList<InMemoryLogEntry>> LoadLogAsync(int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<InMemoryLogEntry>>(ServerLog);

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
        public Task RenameDeviceAsync(TrustedPeerRow device, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> IssuePairingCodeAsync(bool grantsAdmin, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<SubsonicCredentialRow>> LoadSubsonicCredentialsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SubsonicCredentialRow>>([]);

        public Task<SubsonicCredentialRow> IssueSubsonicCredentialAsync(string label, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RevokeSubsonicCredentialAsync(SubsonicCredentialRow credential, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RescanAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task RebuildDatabaseAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private readonly FakeBackend _backend = new();

    // No AppSettings/AppSettingsStore: the viewer's preferences are not what
    // these test, and a real store here would write to the developer's own
    // settings.json.
    private SettingsViewModel _panel = null!;

    private static TrustedPeerRow Device(string fingerprint, string alias) =>
        new() { Fingerprint = fingerprint, Alias = alias, ApprovedAt = DateTimeOffset.UtcNow };

    private static List<InMemoryLogEntry> Lines(params string[] messages) =>
        messages.Select(m => new InMemoryLogEntry(DateTimeOffset.UtcNow, "Information", null, m, null)).ToList();

    private async Task<SettingsViewModel> MakeAsync(params TrustedPeerRow[] devices)
    {
        _backend.Devices.AddRange(devices);
        _panel = new SettingsViewModel(_backend);
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
}
