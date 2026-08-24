using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Flower.Models;
using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests;

// The second half of docs/ARCHITECTURE-REVIEW.md §5.6: the sync *trigger*
// side - role gating, the once-per-session first-contact dedup, the
// IsSyncing edge tracking behind the spinner, and ForceSyncNow bypassing
// both the dedup and the debounce.
//
// No fake LibrarySyncService/PlaylistSyncService is needed, and none exists
// (both are concrete classes taken by MainViewModel's constructor). Peers
// here point at a closed loopback port, so the real sync services run for
// real and fail fast with a connection refusal. That is exactly the shape
// these tests want: what is under test is the tracking around a sync, not
// the sync itself, and a failing sync still increments, decrements and
// reports every edge a successful one does.
[Collection("PlatformDataDirectory")]
public class MainViewModelSyncTriggerTests : PinnedDataDirectory
{
    // The real cooldown is 5s, which is far too slow to wait out per test.
    //
    // Shortened only around the ScheduleContentSync call itself, never while a
    // MainViewModel is being constructed. That matters: PeerSyncCoordinator's
    // constructor starts a periodic _logPushTimer at this same interval, so a
    // MainViewModel built while this is 150ms runs one for as long as it lives.
    // It is disposed at teardown now (see Make below), but keeping the
    // construction out of the shortened window keeps that window at one test
    // rather than the rest of the run. Doing that destabilized the whole suite - unrelated
    // [AvaloniaFact] tests failing in Avalonia's own session setup, a different
    // one each run, about 1 run in 3. DebouncedContentSyncAsync reads the value
    // when it is called, so scoping it this way still exercises the debounce.
    private readonly struct ShortCooldown : IDisposable
    {
        private readonly TimeSpan _previous;

        public ShortCooldown(TimeSpan value)
        {
            _previous = PeerSyncCoordinator.ContentSyncCooldown;
            PeerSyncCoordinator.ContentSyncCooldown = value;
        }

        public void Dispose() => PeerSyncCoordinator.ContentSyncCooldown = _previous;
    }

    private static ShortCooldown Cooldown(int milliseconds = 150) =>
        new(TimeSpan.FromMilliseconds(milliseconds));

    // A port nothing is listening on, so an outbound sync attempt is refused
    // immediately rather than waiting out a connect timeout.
    //
    // A fixed low port, deliberately not an ephemeral one obtained by binding
    // port 0 and releasing it: that hands back a port the OS is free to give to
    // another suite's listener moments later, at which point these syncs would
    // connect to it and that suite would see a connection it never expected.
    // Port 9 (discard) is never bound by anything here.
    private const int ClosedPort = 9;

    private static DiscoveredDevice Peer(
        string fingerprint, string alias = "Peer", string libraryToken = "") => new()
    {
        InstanceName = alias.ToLowerInvariant(),
        BaseUri     = NetworkDiscoveryService.HttpOrigin(new IPEndPoint(IPAddress.Loopback, ClosedPort)),
        Alias        = alias,
        Fingerprint  = fingerprint,
        LibraryToken = libraryToken,
    };

    // Owned by the fixture (not static any more) so the log-push timer inside
    // every one of these MainViewModels is stopped at teardown - see
    // MainViewModelHarness.Parts.
    private MainViewModel Make(AppSettings? settings = null) =>
        Own(MainViewModelHarness.Build(new Library(new List<Track>()), new MainPlaylist(new List<Track>()), settings)).Main;

    // A Client already paired to fp-server, with that server discovered.
    private MainViewModel PairedClient(out DiscoveredDevice server)
    {
        var vm = Make(new AppSettings
        {
            PairedServerFingerprint = "fp-server",
            PairedServerAlias       = "Server",
        });
        server = Peer("fp-server", "Server");
        vm.AddOrUpdateDeviceSidebarItem(server);
        return vm;
    }

    // Sleep + RunJobs, deliberately NOT Dispatcher.UIThread.MainLoop. The
    // headless session owns the dispatcher thread, so a test that parks it in
    // MainLoop holds up every [AvaloniaFact] queued behind it. Nothing here
    // needs a DispatcherTimer advanced (the debounce is a Task.Delay, and the
    // syncing edges are plain Dispatcher.Post), so draining the queue is
    // enough. See TestAppBuilder for the suite-wide flake this was once
    // suspected of causing - it was not the cause.
    private static void PumpUntil(Func<bool> condition, int timeoutMs = 10000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition() && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(10);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(condition(), "the expected sync state was never reached");
    }

    private static void SettleSyncs(MainViewModel vm) => PumpUntil(() => !vm.IsSyncing);

    // Pumps the dispatcher for a fixed span, asserting nothing - for the cases
    // that are about something *not* happening within a window.
    private static void Wait(int milliseconds)
    {
        var deadline = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < deadline)
        {
            Thread.Sleep(10);
            Dispatcher.UIThread.RunJobs();
        }
    }

    // ── Pairing gating ────────────────────────────────────────────────────────

    // This device syncs with its one paired server and nothing else, however
    // many other servers are on the network.
    [AvaloniaFact]
    public void A_client_does_not_sync_with_an_unpaired_peer()
    {
        var vm = PairedClient(out _);

        vm.TriggerSyncIfReady(Peer("fp-someone-else", "Someone Else"));

        Assert.False(vm.IsSyncing);
    }

    [AvaloniaFact]
    public void A_device_with_no_pairing_at_all_syncs_with_nobody()
    {
        var vm = Make(new AppSettings());

        vm.TriggerSyncIfReady(Peer("fp-server", "Server"));

        Assert.False(vm.IsSyncing);
    }

    // A peer whose /info handshake has not resolved yet has no identity to
    // gate on, so there is nothing to sync with.
    [AvaloniaFact]
    public void A_peer_with_no_resolved_fingerprint_is_not_synced_with()
    {
        var vm = PairedClient(out _);

        vm.TriggerSyncIfReady(Peer("", "Unresolved"));

        Assert.False(vm.IsSyncing);
    }

    // ── First-contact dedup ───────────────────────────────────────────────────

    // DeviceDiscovered re-fires for an already-known peer (the periodic poll
    // re-resolves everything on its own cadence), so first contact has to be
    // once per session, not once per event.
    [AvaloniaFact]
    public void First_contact_triggers_a_sync_and_a_second_one_does_not()
    {
        var vm = PairedClient(out var server);

        vm.TriggerSyncIfReady(server);
        Assert.True(vm.IsSyncing);
        SettleSyncs(vm);

        vm.TriggerSyncIfReady(server);

        Assert.False(vm.IsSyncing);
    }

    // ── IsSyncing edges ───────────────────────────────────────────────────────

    // A playlist sync and a library sync run concurrently per peer, and the
    // spinner should cover the whole overlapping span rather than flickering
    // between them - so the property only notifies on the 0-to-1 and 1-to-0
    // edges, not on every increment.
    [AvaloniaFact]
    public void IsSyncing_notifies_once_going_up_and_once_coming_down()
    {
        var vm = PairedClient(out var server);
        var edges = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsSyncing))
                edges++;
        };

        vm.TriggerSyncIfReady(server);
        SettleSyncs(vm);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, edges);
    }

    // The paired server's own sidebar row carries the same spinner, and must
    // not be left showing it once the sync is over.
    [AvaloniaFact]
    public void The_paired_server_row_stops_showing_its_syncing_state()
    {
        var vm = PairedClient(out var server);
        var row = vm.SidebarItems.Single(i => i.Kind == SidebarItemKind.Device);

        vm.TriggerSyncIfReady(server);
        SettleSyncs(vm);
        Wait(100); // let the falling-edge Post land

        Assert.False(row.IsSyncing);
    }

    // A row re-created or updated mid-sync must carry the in-flight state
    // forward rather than defaulting back to false - IsSyncing only fires on
    // its own edges, not whenever a sidebar row happens to change.
    [AvaloniaFact]
    public void A_row_updated_mid_sync_keeps_its_syncing_state()
    {
        var vm = PairedClient(out var server);
        vm.TriggerSyncIfReady(server);
        PumpUntil(() => vm.IsSyncing);

        vm.AddOrUpdateDeviceSidebarItem(server);

        Assert.True(vm.SidebarItems.Single(i => i.Kind == SidebarItemKind.Device).IsSyncing);
        SettleSyncs(vm);
    }

    // ── ForceSyncNow ──────────────────────────────────────────────────────────

    // The "Sync Now" button needs the paired server actually discovered - the
    // same condition the button's own enablement uses.
    [AvaloniaFact]
    public void ForceSyncNow_reports_a_server_it_cannot_find()
    {
        var vm = Make(new AppSettings
        {
            PairedServerFingerprint = "fp-server",
            PairedServerAlias       = "Server",
        });
        Assert.False(vm.CanForceSync);

        vm.ForceSyncNow();

        Assert.Equal("Server not currently found on the network", vm.LastForceSyncResult);
    }

    [AvaloniaFact]
    public void ForceSyncNow_does_nothing_at_all_when_not_paired()
    {
        var vm = Make(new AppSettings());

        vm.ForceSyncNow();

        Assert.Null(vm.LastForceSyncResult);
        Assert.False(vm.IsSyncing);
    }

    // ForceSyncNow's *reachable* path - the actual sync, the result strings,
    // the trust confirmation, the deliberate bypass of the initiator election -
    // lives in ForceSyncNowTests, which drives the real discovery pipeline
    // (a FakeMdnsBackend announcement plus a fake /info handshake) to make the
    // paired server genuinely reachable, since PairedServerReachability reads
    // NetworkDiscoveryService.KnownDevices and adding a device to the sidebar
    // does not put it there.

    // ── Debounced content sync ────────────────────────────────────────────────

    // Every call restarts the cooldown rather than queuing another, so a burst
    // of local edits settles into one sync instead of one per edit.
    [AvaloniaFact]
    public void ScheduleContentSync_does_not_sync_during_the_cooldown()
    {
        var vm = PairedClient(out _);
        using var cooldown = Cooldown();

        vm.ScheduleContentSync();
        Wait(50); // well inside the cooldown

        Assert.False(vm.IsSyncing);
    }

    // A sync against a refused connection starts and finishes between two pump
    // slices, so IsSyncing is not reliably observable by polling - the edge
    // notifications are. One sync run is exactly two of them (0-to-1, 1-to-0),
    // however many concurrent calls it fans out into.
    private static Func<int> CountSyncEdges(MainViewModel vm)
    {
        var edges = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsSyncing))
                Interlocked.Increment(ref edges);
        };
        return () => Volatile.Read(ref edges);
    }

    [AvaloniaFact]
    public void ScheduleContentSync_syncs_once_the_cooldown_elapses()
    {
        var vm = PairedClient(out _);
        using var cooldown = Cooldown();
        var edges = CountSyncEdges(vm);

        vm.ScheduleContentSync();
        PumpUntil(() => edges() >= 2, 5000);

        Assert.False(vm.IsSyncing);
    }

    // A restarted cooldown must not leave the superseded delay running and
    // fire twice.
    [AvaloniaFact]
    public void A_burst_of_scheduled_syncs_collapses_into_one()
    {
        var vm = PairedClient(out _);
        using var cooldown = Cooldown();
        var edges = CountSyncEdges(vm);

        for (var i = 0; i < 5; i++)
        {
            vm.ScheduleContentSync();
            Wait(40); // each call restarts the 150ms cooldown
        }

        PumpUntil(() => edges() >= 2, 5000);
        // Let every superseded cooldown that might still be running come due.
        Wait(600);

        // Exactly one sync run, not five: 2 edges, not 10.
        Assert.Equal(2, edges());
    }

    // An unpaired device has nowhere to send a local edit, so it schedules
    // nothing rather than fanning out to whatever it can see.
    [AvaloniaFact]
    public void ScheduleContentSync_while_unpaired_syncs_with_nobody()
    {
        var vm = Make(new AppSettings());
        vm.AddOrUpdateDeviceSidebarItem(Peer("fp-server", "A Server"));
        using var cooldown = Cooldown();

        vm.ScheduleContentSync();
        Wait(600);

        Assert.False(vm.IsSyncing);
    }

    // ── Catalog-change trigger ────────────────────────────────────────────────
    //
    // The peer advertises an opaque library token on /info; a change in it is
    // how a client notices a server-side edit without polling the manifest
    // (Tier 1.4). Runs *before* TriggerSyncIfReady on each discovery, so the
    // first observation of a peer must be told apart from a later change.

    [AvaloniaFact]
    public void The_first_observation_of_a_peers_catalog_token_does_not_sync()
    {
        var vm = PairedClient(out _);
        var edges = CountSyncEdges(vm);

        vm.TriggerSyncIfPeerCatalogChanged(Peer("fp-server", "Server", libraryToken: "tok-1"));
        Wait(300);

        Assert.Equal(0, edges());
    }

    [AvaloniaFact]
    public void An_unchanged_catalog_token_does_not_sync()
    {
        var vm = PairedClient(out _);
        vm.TriggerSyncIfPeerCatalogChanged(Peer("fp-server", "Server", libraryToken: "tok-1"));
        var edges = CountSyncEdges(vm);

        vm.TriggerSyncIfPeerCatalogChanged(Peer("fp-server", "Server", libraryToken: "tok-1"));
        Wait(300);

        Assert.Equal(0, edges());
    }

    [AvaloniaFact]
    public void A_changed_catalog_token_syncs()
    {
        var vm = PairedClient(out _);
        vm.TriggerSyncIfPeerCatalogChanged(Peer("fp-server", "Server", libraryToken: "tok-1"));
        var edges = CountSyncEdges(vm);

        vm.TriggerSyncIfPeerCatalogChanged(Peer("fp-server", "Server", libraryToken: "tok-2"));

        PumpUntil(() => edges() >= 2, 5000);
        Assert.False(vm.IsSyncing);
    }

    // Unlike the first-contact trigger, this one is not deduped per session -
    // every genuine change resyncs.
    [AvaloniaFact]
    public void Each_further_catalog_change_syncs_again()
    {
        var vm = PairedClient(out _);
        vm.TriggerSyncIfPeerCatalogChanged(Peer("fp-server", "Server", libraryToken: "tok-1"));
        var edges = CountSyncEdges(vm);

        vm.TriggerSyncIfPeerCatalogChanged(Peer("fp-server", "Server", libraryToken: "tok-2"));
        PumpUntil(() => edges() >= 2, 5000);
        vm.TriggerSyncIfPeerCatalogChanged(Peer("fp-server", "Server", libraryToken: "tok-3"));
        PumpUntil(() => edges() >= 4, 5000);

        Assert.False(vm.IsSyncing);
    }

    // A peer that reports no token at all (older code, or one that has not
    // resolved yet) is left to the other trigger paths.
    [AvaloniaFact]
    public void A_peer_advertising_no_catalog_token_is_ignored()
    {
        var vm = PairedClient(out _);
        var edges = CountSyncEdges(vm);

        vm.TriggerSyncIfPeerCatalogChanged(Peer("fp-server", "Server", libraryToken: ""));
        vm.TriggerSyncIfPeerCatalogChanged(Peer("fp-server", "Server", libraryToken: ""));
        Wait(300);

        Assert.Equal(0, edges());
    }

    // Role gating applies here exactly as it does to first contact.
    [AvaloniaFact]
    public void A_catalog_change_on_an_unpaired_peer_does_not_sync()
    {
        var vm = PairedClient(out _);
        var edges = CountSyncEdges(vm);

        vm.TriggerSyncIfPeerCatalogChanged(Peer("fp-other", "Other", libraryToken: "tok-1"));
        vm.TriggerSyncIfPeerCatalogChanged(Peer("fp-other", "Other", libraryToken: "tok-2"));
        Wait(300);

        Assert.Equal(0, edges());
    }
}
