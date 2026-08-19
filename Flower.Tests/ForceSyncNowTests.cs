using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;

using Flower.Models;
using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests;

// ForceSyncNow's *reachable* path - the last uncovered corner of
// docs/ARCHITECTURE-REVIEW.md Tier 5.6. It was previously out of reach because
// it reads PairedServerReachability.PairedServerDevice, which is only ever
// populated from NetworkDiscoveryService.KnownDevices - i.e. by a real mDNS
// announcement plus a real /info handshake, neither of which adding a row to
// the sidebar produces.
//
// Both halves are now driven for real rather than worked around: the peer is
// discovered through the actual discovery pipeline (FakeMdnsBackend raising
// InstanceFound, a fake HttpMessageHandler answering /info), and only the two
// sync entry points themselves are stubbed - what is under test is the result
// reporting and trust confirmation around a sync, not the sync.
[Collection("PlatformDataDirectory")]
public class ForceSyncNowTests : PinnedDataDirectory
{
    private const string ServerFingerprint = "fp-server";
    private static readonly IPEndPoint ServerEndPoint = new(IPAddress.Parse("192.168.1.10"), 4533);

    private sealed class FakeInfoHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"alias":"Living Room","fingerprint":"{{ServerFingerprint}}","isServer":true,"trustsCaller":true}"""),
            });
    }

    // A Client paired with a Server that is actually on the network.
    private static MainViewModelHarness.Parts PairedWithADiscoveredServer()
    {
        using var parts = MainViewModelHarness.BuildParts(
            new Library(new List<Track>()),
            new MainPlaylist(new List<Track>()),
            new AppSettings
            {
                IsServer                = false,
                PairedServerFingerprint = ServerFingerprint,
                PairedServerAlias       = "Living Room",
            },
            stubSyncServices: true,
            discoveryHttpClient: new HttpClient(new FakeInfoHandler()));

        // Discovering a peer is itself a sync trigger (first contact - see
        // TriggerSyncIfReady), and that automatic sync would otherwise be the
        // one every assertion below reads. Fail it, wait for it to settle, and
        // clear the record, so what follows is only ever the forced sync.
        parts.StubLibrarySync!.Result = new LibrarySyncResult(Success: false, FetchedCount: 0, AddedCount: 0);
        parts.MdnsBackend.RaiseInstanceFound("living-room._flowersync._tcp.local", ServerEndPoint);
        Assert.True(
            SpinWait.SpinUntil(
                () => parts.NetworkDiscovery.KnownDevices.Any(d => d.Fingerprint == ServerFingerprint),
                TimeSpan.FromSeconds(5)),
            "the /info handshake never resolved the peer's fingerprint");

        // AppSettings is not observable, so reachability has to be nudged after
        // a pairing change - the same call PairWithServer makes.
        Assert.True(
            SpinWait.SpinUntil(
                () => parts.StubLibrarySync.SyncedWith.Count > 0 && !parts.Main.IsSyncing,
                TimeSpan.FromSeconds(5)),
            "the first-contact sync never ran or never finished");
        parts.StubLibrarySync.SyncedWith.Clear();
        parts.StubPlaylistSync!.SyncedWith.Clear();
        parts.StubLibrarySync.Result = new LibrarySyncResult(Success: true, FetchedCount: 0, AddedCount: 0);

        // AppSettings is not observable, so reachability has to be nudged after
        // a pairing change - the same call PairWithServer makes.
        parts.Reachability.Recompute();
        Assert.True(parts.Reachability.IsReachable);
        Assert.False(parts.Main.IsPairedServerTrustConfirmed);

        return parts;
    }

    [AvaloniaFact]
    public async Task A_sync_that_fetched_new_tracks_reports_how_many()
    {
        using var parts = PairedWithADiscoveredServer();
        parts.StubLibrarySync!.Result = new LibrarySyncResult(Success: true, FetchedCount: 40, AddedCount: 3);

        await parts.Main.Sync.ForceSyncNowAsync();

        Assert.Equal("Added 3 new track(s) from Living Room", parts.Main.LastForceSyncResult);
    }

    // A 304 from the server: its catalog is exactly what was merged last time,
    // so there is no fetched count worth reporting.
    [AvaloniaFact]
    public async Task A_server_that_answered_unchanged_reports_no_counts_at_all()
    {
        using var parts = PairedWithADiscoveredServer();
        parts.StubLibrarySync!.Result = new LibrarySyncResult(Success: true, FetchedCount: 0, AddedCount: 0, Unchanged: true);

        await parts.Main.Sync.ForceSyncNowAsync();

        Assert.Equal("Already up to date with Living Room", parts.Main.LastForceSyncResult);
    }

    [AvaloniaFact]
    public async Task A_sync_that_checked_tracks_but_added_none_says_how_many_it_checked()
    {
        using var parts = PairedWithADiscoveredServer();
        parts.StubLibrarySync!.Result = new LibrarySyncResult(Success: true, FetchedCount: 40, AddedCount: 0);

        await parts.Main.Sync.ForceSyncNowAsync();

        Assert.Equal("Already up to date with Living Room (40 track(s) checked)", parts.Main.LastForceSyncResult);
    }

    // Discovered but not actually answering - the peer went away between the
    // last /info poll and the button press.
    [AvaloniaFact]
    public async Task A_failed_sync_says_so_rather_than_claiming_to_be_up_to_date()
    {
        using var parts = PairedWithADiscoveredServer();
        parts.StubLibrarySync!.Result = new LibrarySyncResult(Success: false, FetchedCount: 0, AddedCount: 0);

        await parts.Main.Sync.ForceSyncNowAsync();

        Assert.Equal("Could not reach Living Room - check it's still on the network and paired",
            parts.Main.LastForceSyncResult);
    }

    // Force sync is the user saying "sync with this one, now": it deliberately
    // bypasses the initiator election every automatic playlist sync goes
    // through, so a Client can pull from a Server that would otherwise have
    // won the election.
    [AvaloniaFact]
    public async Task Forcing_a_sync_makes_this_device_the_initiator()
    {
        using var parts = PairedWithADiscoveredServer();

        await parts.Main.Sync.ForceSyncNowAsync();

        var (device, forceInitiator) = Assert.Single(parts.StubPlaylistSync!.SyncedWith);
        Assert.Equal(ServerFingerprint, device.Fingerprint);
        Assert.True(forceInitiator);
    }

    // A successful sync is proof the server still trusts us, which is what
    // clears the "awaiting approval" state the pairing flow leaves behind.
    [AvaloniaFact]
    public async Task A_successful_sync_confirms_the_server_still_trusts_us()
    {
        using var parts = PairedWithADiscoveredServer();

        await parts.Main.Sync.ForceSyncNowAsync();

        Assert.True(parts.Main.IsPairedServerTrustConfirmed);
    }

    [AvaloniaFact]
    public async Task A_failed_sync_does_not_confirm_trust()
    {
        using var parts = PairedWithADiscoveredServer();
        parts.StubLibrarySync!.Result = new LibrarySyncResult(Success: false, FetchedCount: 0, AddedCount: 0);

        await parts.Main.Sync.ForceSyncNowAsync();

        Assert.False(parts.Main.IsPairedServerTrustConfirmed);
    }

    // IsSyncing drives the sidebar spinner, and it has to be back off by the
    // time the awaited call returns whichever way the sync went.
    [AvaloniaFact]
    public async Task The_syncing_indicator_is_cleared_when_a_forced_sync_finishes()
    {
        using var parts = PairedWithADiscoveredServer();

        await parts.Main.Sync.ForceSyncNowAsync();

        Assert.False(parts.Main.IsSyncing);
    }
}
