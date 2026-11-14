using Flower.Logging;
using Flower.Persistence;
using Flower.ViewModels;

namespace Flower.Tests.TestSupport;

public abstract class SettingsLogTabHarness
{
    // Answers the log calls from dictionaries, with an optional gate so a test
    // can hold one fetch open while the selection moves. Everything else on
    // ISettingsBackend belongs to tabs these tests never touch.
    protected sealed class FakeBackend : ISettingsBackend
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

        // Holds every server-log read open until released, so a test can have a
        // reader-asked read and a poll in flight at once.
        public TaskCompletionSource? ServerLogGate { get; set; }

        // Sequenced by position, which is all InMemoryLogStore's own numbering
        // amounts to for a log that is only ever appended to: everything past
        // afterSequence, and the index of the last line as the cursor to come
        // back with.
        public Task<IReadOnlyList<RemovedFileRow>> LoadRemovedFilesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RemovedFileRow>>([]);

        public Task<string> RestoreRemovedFilesAsync(IReadOnlyList<string> paths, CancellationToken ct = default) =>
            Task.FromResult("");

        public Task<string> DeleteRemovedFilesAsync(IReadOnlyList<string> paths, CancellationToken ct = default) =>
            Task.FromResult("");

        public async Task<LogSlice> LoadLogAsync(int limit, long afterSequence, CancellationToken ct = default)
        {
            if (ServerLogGate is { } gate)
                await gate.Task;

            if (ServerLogFails)
                throw new InvalidOperationException("the server went away");

            var entries = ServerLog.Skip((int)(afterSequence + 1)).ToList();
            return new LogSlice(ServerLog.Count - 1, entries);
        }

        public async Task<IReadOnlyList<InMemoryLogEntry>?> LoadDeviceLogAsync(string fingerprint, int limit, CancellationToken ct = default)
        {
            if (fingerprint == GatedFingerprint)
                await Gate.Task;
            return DeviceLogs.TryGetValue(fingerprint, out var lines) ? lines : null;
        }

        public Task<IReadOnlyList<TrustedPeerRow>> LoadDevicesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TrustedPeerRow>>(Devices);

        public Task<SettingsSnapshot> LoadAsync(CancellationToken ct = default) => Task.FromResult(new SettingsSnapshot());
        public Task<string?> SaveAsync(SettingsDraft draft, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public int CountSongsUnder(string folder) => -1;
        public Task ForgetDeviceAsync(TrustedPeerRow device, CancellationToken ct = default) => Task.CompletedTask;
        public Task<(string Code, string Invite)> IssuePairingCodeAsync(bool grantsAdmin, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RescanAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    protected readonly FakeBackend _backend = new();

    // Settings but no store: where the screen was left is read and written
    // through this, and the tests that care assert against it directly - a real
    // store here would write to the developer's own settings.json.
    protected readonly AppSettings _appSettings = new();

    protected SettingsViewModel _panel = null!;

    protected static TrustedPeerRow Device(string fingerprint, string alias, bool hasLog = true) =>
        new() { Fingerprint = fingerprint, Alias = alias, ApprovedAt = DateTimeOffset.UtcNow, HasLog = hasLog };

    protected static List<InMemoryLogEntry> Lines(params string[] messages) =>
        messages.Select(m => new InMemoryLogEntry(DateTimeOffset.UtcNow, "Information", null, m, null)).ToList();

    protected async Task<SettingsViewModel> MakeAsync(params TrustedPeerRow[] devices)
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
    protected static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition() && Environment.TickCount64 < deadline)
            await Task.Delay(5);
        Assert.True(condition(), "the expected log fetch never landed");
    }

    // Gives an already-released fetch every chance to land, asserting nothing -
    // for "this must NOT happen".
    protected static async Task DrainAsync(int milliseconds)
    {
        var deadline = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < deadline)
            await Task.Delay(5);
    }

    protected async Task SelectAsync(string? fingerprint)
    {
        _panel.SelectedLogSource = _panel.LogSources.First(s => s.Fingerprint == fingerprint);
        await WaitForAsync(() => !_panel.LogViewer.DisplayLines.SequenceEqual(new[] { "(loading...)" }));
    }
}
