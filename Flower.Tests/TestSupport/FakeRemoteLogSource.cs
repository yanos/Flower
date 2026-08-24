using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Flower.Logging;
using Flower.Services;

namespace Flower.Tests.TestSupport;

// Stands in for the paired server's admin API in LogViewModel's tests - see
// IRemoteLogSource, which exists so that these can be written without an HTTP
// stack. Everything is answered synchronously from dictionaries: the window's
// own logic is what is under test, not the transport.
public sealed class FakeRemoteLogSource : IRemoteLogSource
{
    public string OwnFingerprint { get; set; } = "fp-me";

    // Null is the "nothing to ask" answer the real one gives when there is no
    // paired server, it is unreachable, or this device is not an admin of it.
    public List<RemoteDevice>? Devices { get; set; } = [];

    public List<InMemoryLogEntry>? ServerLog { get; set; } = [];

    // A fingerprint absent from here has never pushed - the NoSnapshot case.
    public Dictionary<string, List<InMemoryLogEntry>> DeviceLogs { get; } = new();

    // Held non-null to keep a fetch pending, so a test can move the selection
    // while one is in flight and see which answer wins.
    public TaskCompletionSource? Gate { get; set; }

    public async Task<IReadOnlyList<RemoteDevice>?> ListDevicesAsync()
    {
        await WaitForGateAsync();
        return Devices;
    }

    public async Task<RemoteLogResult> GetServerLogAsync(int limit)
    {
        await WaitForGateAsync();
        return ServerLog is { } lines ? RemoteLogResult.Ok(lines) : RemoteLogResult.Unavailable;
    }

    public async Task<RemoteLogResult> GetDeviceLogAsync(string fingerprint, int limit)
    {
        await WaitForGateAsync();
        return DeviceLogs.TryGetValue(fingerprint, out var lines)
            ? RemoteLogResult.Ok(lines)
            : RemoteLogResult.NoSnapshot;
    }

    private Task WaitForGateAsync() => Gate?.Task ?? Task.CompletedTask;

    public static List<InMemoryLogEntry> Lines(params string[] messages) =>
        messages.Select(m => new InMemoryLogEntry(DateTimeOffset.UtcNow, "Information", null, m, null)).ToList();
}
