using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using Flower.Logging;
using Flower.Persistence;

namespace Flower.Services;

// The Log window's IRemoteLogSource: reads the paired server's admin API on
// behalf of the rows that are not "This Device".
//
// Being paired is not the same as being an *administrator* of that server.
// This deliberately does not pre-check which it is, even though the handshake
// now reports it (DiscoveredDevice.WeAreAdmin): unlike a button, which is
// offered or not before anything is asked, every row here has to make the call
// regardless, so a refusal is already an answer this code handles. A 403 comes
// back as Unavailable like any other reason there is nothing to show.
public sealed class PairedServerAdminAccess(
    PairedServerReachability reachability, DeviceIdentity identity, DeviceSigningKey signingKey) : IRemoteLogSource
{
    // Longer than the sync services' own timeouts: an admin call is made
    // because a human clicked something and is watching, and a log fetch is the
    // largest response on that surface.
    private static readonly HttpClient Http = PeerHttpClient.Create(TimeSpan.FromSeconds(15));

    public string OwnFingerprint => identity.Fingerprint;

    private ServerAdminClient? TryCreate() =>
        reachability.PairedServerDevice is { } server
            ? new ServerAdminClient(Http, server.BaseUri,
                ServerAdminClient.SignWith(new SignedDeviceCredentials(identity, signingKey)))
            : null;

    public async Task<IReadOnlyList<RemoteDevice>?> ListDevicesAsync()
    {
        if (TryCreate() is not { } admin)
            return null;

        try
        {
            var devices = await admin.GetDevicesAsync();
            return devices.Select(d => new RemoteDevice(d.Fingerprint, d.Alias)).ToList();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public Task<RemoteLogResult> GetServerLogAsync(int limit) =>
        FetchAsync(admin => admin.GetLogAsync(limit));

    public Task<RemoteLogResult> GetDeviceLogAsync(string fingerprint, int limit) =>
        FetchAsync(async admin => (await admin.GetDeviceLogAsync(fingerprint, limit)).Entries);

    private async Task<RemoteLogResult> FetchAsync(Func<ServerAdminClient, Task<List<AdminLogEntryDto>>> fetch)
    {
        if (TryCreate() is not { } admin)
            return RemoteLogResult.Unavailable;

        try
        {
            var lines = await fetch(admin);
            return RemoteLogResult.Ok(lines
                .Select(e => new InMemoryLogEntry(e.Timestamp, e.Level, e.SourceContext, e.Message, e.Exception))
                .ToList());
        }
        catch (ServerAdminException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            return RemoteLogResult.NoSnapshot;
        }
        catch (Exception)
        {
            return RemoteLogResult.Unavailable;
        }
    }
}
