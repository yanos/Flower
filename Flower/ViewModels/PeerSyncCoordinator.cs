using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Threading;

using Microsoft.Extensions.Logging;

using Flower.Logging;
using Flower.Models;
using Flower.Persistence;
using Flower.Services;

namespace Flower.ViewModels;

// What PeerSyncCoordinator needs from whoever owns the sidebar. Deliberately
// small: the coordinator drives sync, pairing and trust entirely on its own,
// and only needs someone to tell it which peers are currently listed (the same
// question the sidebar already answers) and to relay its state changes onward.
public interface IPeerSyncHost
{
    // Every currently-listed, fingerprint-resolved peer. Read fresh on each
    // resync rather than cached, since discovery adds and removes rows
    // continuously. Kept as a host question rather than read straight off
    // NetworkDiscoveryService.KnownDevices so this stays exactly the set the
    // user can actually see - a peer removed as a duplicate row is gone from
    // here but still known to discovery.
    IReadOnlyList<DiscoveredDevice> ListedPeers { get; }
}

// The P2P sync coordinator: when to sync, with whom, the pairing/trust
// handshake around it, and resolving a placeholder track back to the peer that
// holds it. Extracted from MainViewModel, where it was roughly 900 of that
// class's 2,700 lines and lived there only because the device sidebar rows did
// - see docs/ARCHITECTURE-REVIEW.md Tier 4.2.
//
// Raises PropertyChanged for the handful of values MainViewModel re-surfaces
// to XAML (IsSyncing, the paired-server pointer, reachability, force-sync
// state); MainViewModel forwards those onto its own bindable properties.
public sealed class PeerSyncCoordinator : ViewModelBase, IDisposable
{
    private readonly IPeerSyncHost _host;
    private readonly AppSettings _appSettings;
    private readonly AppSettingsStore? _appSettingsStore;
    private readonly DeviceIdentityStore? _deviceIdentityStore;
    private readonly PlaylistSyncService? _playlistSyncService;
    private readonly LibrarySyncService? _librarySyncService;
    private readonly LibraryDownloadService? _libraryDownloadService;
    private readonly PeerPairingService? _peerPairingService;
    private readonly PeerTrackResolver? _peerTrackResolver;
    private readonly NetworkDiscoveryService? _networkDiscovery;
    private readonly PairedServerReachability? _reachability;
    private readonly DeviceIdentity? _deviceIdentity;
    private readonly DeviceSigningKey? _signingKey;
    private readonly ILogger _logger;

    private readonly DispatcherTimer _logPushTimer;

    public PeerSyncCoordinator(
        IPeerSyncHost host,
        AppSettings appSettings,
        AppSettingsStore? appSettingsStore,
        DeviceIdentityStore? deviceIdentityStore,
        ILogger<PeerSyncCoordinator> logger,
        NetworkDiscoveryService? networkDiscovery = null,
        PairedServerReachability? reachability = null,
        PlaylistSyncService? playlistSyncService = null,
        LibrarySyncService? librarySyncService = null,
        LibraryDownloadService? libraryDownloadService = null,
        PeerPairingService? peerPairingService = null,
        PeerTrackResolver? peerTrackResolver = null,
        DeviceIdentity? deviceIdentity = null,
        DeviceSigningKey? signingKey = null)
    {
        _host                   = host;
        _appSettings            = appSettings;
        _appSettingsStore       = appSettingsStore;
        _deviceIdentityStore    = deviceIdentityStore;
        _logger                 = logger;
        _networkDiscovery       = networkDiscovery;
        _reachability           = reachability;
        _playlistSyncService    = playlistSyncService;
        _librarySyncService     = librarySyncService;
        _libraryDownloadService = libraryDownloadService;
        _peerPairingService     = peerPairingService;
        _peerTrackResolver      = peerTrackResolver;
        _deviceIdentity         = deviceIdentity;
        _signingKey             = signingKey;

        // Any new log line at all (playing a track, a setting changed, an
        // error, routine peer-polling chatter, ...) marks that there is
        // something new for a paired Server's Log window to pick up - the
        // timer below periodically checks that flag and syncs if it is set,
        // entirely independent of ScheduleContentSync's debounce.
        // A debounce cannot work here: NetworkDiscoveryService's own routine
        // ~5s polling chatter (and, previously, this sync path's own
        // completion logging) fires at essentially the same cadence as
        // ContentSyncCooldown, so a timer that resets on every log line
        // would perpetually restart itself and never actually go quiet long
        // enough to fire at all - confirmed in practice as "still no new log
        // appearing after 5s" when this was first wired straight into
        // ScheduleContentSync. A periodic tick fires on a fixed wall-clock
        // schedule no matter how much log activity happens in between, which
        // is what actually delivers "new lines within roughly 5s" reliably.
        // InMemoryLogStore.Instance is a process-wide singleton, so this is the
        // one subscription here that genuinely outlives this object if it is
        // never taken back - see Dispose and SubscriptionBag.
        _subscriptions.Add<EventHandler<InMemoryLogEntry>>((_, _) => _hasUnpushedLogActivity = true,
            h => InMemoryLogStore.Instance.EntryAdded += h, h => InMemoryLogStore.Instance.EntryAdded -= h);

        _logPushTimer = new DispatcherTimer { Interval = ContentSyncCooldown };
        _logPushTimer.Tick += (_, _) =>
        {
            if (!_hasUnpushedLogActivity || _activeSyncCount != 0)
                return;
            _hasUnpushedLogActivity = false;
            RunPendingDeviceSyncs();
        };
        _logPushTimer.Start();
    }

    // ── Sync tracking ─────────────────────────────────────────────────────

    // Non-zero while at least one PlaylistSyncService/LibrarySyncService call
    // is in flight (see RunTrackedSync) - both services' merges fire
    // Library.TracksUpdated/PlaylistsUpdated unconditionally, even when
    // nothing actually changed (e.g. every song a peer reports already exists
    // locally). Without this guard, the debounced resync below
    // (ScheduleContentSync) would treat a sync's own merge as "a local change
    // just happened" and schedule another sync, which would merge again and
    // reschedule again, forever - two devices perpetually re-triggering each
    // other.
    private int _activeSyncCount;

    // Whether one of our own syncs is currently merging - MainViewModel's
    // Library.TracksUpdated/PlaylistsUpdated handlers consult this to tell a
    // sync's own merge apart from a genuine local change.
    public bool IsMergingOwnSync => _activeSyncCount > 0;

    // Drives the "syncing" spinner next to the paired server's name (desktop's
    // ServerPickerView, mobile's SettingsView) and its sidebar device row.
    // Only notifies on the 0-to-1/1-to-0 edges, not every increment/decrement,
    // since a playlist sync and a library sync run concurrently per peer and
    // the spinner should stay up for the whole overlapping span, not flicker
    // between them.
    public bool IsSyncing => _activeSyncCount > 0;

    private void RunTrackedSync(Func<Task> syncCall)
    {
        // TriggerSyncIfReady/DebouncedContentSyncAsync can both run this from a
        // background thread (mDNS callback, debounce timer) - see CLAUDE.md's
        // Binding Notes on marshalling UI updates.
        if (Interlocked.Increment(ref _activeSyncCount) == 1)
            Dispatcher.UIThread.Post(NotifyIsSyncingChanged);
        _ = RunTrackedSyncAsync(syncCall);
    }

    private async Task RunTrackedSyncAsync(Func<Task> syncCall)
    {
        try
        {
            await syncCall();
        }
        finally
        {
            if (Interlocked.Decrement(ref _activeSyncCount) == 0)
                Dispatcher.UIThread.Post(NotifyIsSyncingChanged);
        }
    }

    private void NotifyIsSyncingChanged() => OnPropertyChanged(nameof(IsSyncing));

    // ── Content sync debounce ─────────────────────────────────────────────

    private CancellationTokenSource? _contentSyncCts;
    private bool _hasUnpushedLogActivity;

    // "A few seconds" per the user request - long enough that a burst of rapid
    // local edits (e.g. reordering a playlist track-by-track, or a rescan
    // finding many files) settles into one sync instead of one per edit, short
    // enough that a peer notices a real change reasonably promptly.
    // Not const/readonly so MainViewModelSyncTriggerTests can shorten it.
    // Waiting out the real 5s in a test is not just slow: those tests pump the
    // shared headless Dispatcher, and occupying it for seconds at a time
    // destabilizes every other [AvaloniaFact] in the suite.
    internal static TimeSpan ContentSyncCooldown = TimeSpan.FromSeconds(5);

    // Fingerprints of devices already sync'd (or currently syncing) this app
    // session, so DeviceDiscovered re-firing for the same peer (e.g. once with
    // the mDNS-name fallback alias, again once /info resolves) doesn't start a
    // second, overlapping sync session. Cleared per-device on DeviceLost so a
    // peer that drops off and comes back later gets a fresh sync. Concurrent
    // dictionary because discovery events aren't guaranteed to arrive on one
    // fixed thread.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _syncedDeviceFingerprints = new();

    public void ForgetSyncedDevice(string fingerprint) => _syncedDeviceFingerprints.TryRemove(fingerprint, out _);

    // Called whenever a genuine local change happens to this device's library
    // or playlists: a rescan or download completing (Library.TracksUpdated),
    // or a playlist being created/renamed/deleted/reordered/added-to (called
    // directly at each of those call sites - unlike TracksUpdated,
    // Library.PlaylistsUpdated only fires for a *sync's own* ReplacePlaylists
    // call, never for these ordinary local actions, per its own doc comment,
    // so there is no single event to hook for playlists the way there is for
    // tracks). Debounced: every call restarts the cooldown rather than
    // queuing another, so only the last change in a burst actually triggers
    // a sync. New log activity does NOT go through this path - see
    // _logPushTimer in the constructor for why a debounce cannot work for that.
    public void ScheduleContentSync()
    {
        _contentSyncCts?.Cancel();
        _contentSyncCts = new CancellationTokenSource();
        _ = DebouncedContentSyncAsync(_contentSyncCts.Token);
    }

    private async Task DebouncedContentSyncAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(ContentSyncCooldown, token);
        }
        catch (OperationCanceledException)
        {
            return; // A newer change restarted the cooldown - that call's own delay will fire instead.
        }

        RunPendingDeviceSyncs();
    }

    // Shared by the debounced path above (genuine library/playlist changes)
    // and _logPushTimer's independent periodic tick (new log activity) -
    // both ultimately just need "sync with whichever peer this Client is
    // paired to, right now."
    private void RunPendingDeviceSyncs()
    {
        // Every currently-known, fingerprint-resolved peer this device should
        // bulk-sync with per SyncRolePolicy - not gated by
        // _syncedDeviceFingerprints (that dedup is specifically for "don't
        // double-sync from DeviceDiscovered re-firing at first contact" - see
        // TriggerSyncIfReady - and is orthogonal to resyncing on a later
        // change). Collapses to at most one device (the Client's paired
        // Server) under role gating; empty for a Server, which never initiates.
        var isServer = _appSettings.IsServer;
        var pairedServerFingerprint = _appSettings.PairedServerFingerprint;
        var devices = _host.ListedPeers
            .Where(d => d.Fingerprint.Length > 0 &&
                        SyncRolePolicy.ShouldInitiateSync(isServer, pairedServerFingerprint, d.Fingerprint))
            .ToList();

        if (devices.Count == 0)
            return;

        foreach (var device in devices)
        {
            // forceInitiator: true - see TriggerSyncIfReady's identical
            // reasoning; every device here is already the Client's own
            // paired Server (ShouldInitiateSync above guarantees it).
            RunTrackedSync(() => _playlistSyncService?.SyncWithAsync(device, forceInitiator: true) ?? Task.CompletedTask);
            RunTrackedSync(() => SyncLibraryAndConfirmTrust(device));
        }

        // Logged after RunTrackedSync has already incremented _activeSyncCount
        // for every device above (that increment is synchronous - see
        // RunTrackedSync), not before, so this line itself does not get
        // treated as new log activity by _logPushTimer while _activeSyncCount
        // is still 0 - logging it earlier caused every completed sync to
        // immediately re-schedule another one, forever, from its own message.
        _logger.LogInformation("Content sync running with {Count} known device(s): {Devices}",
            devices.Count, string.Join(", ", devices.Select(d => d.Alias)));
    }

    // ── Pairing and trust ─────────────────────────────────────────────────

    // Raised whenever the paired-server pointer or its trust state changes, so
    // MainViewModel can re-raise its own dependent properties and re-sync the
    // pinned sidebar row.
    public event EventHandler? PairingChanged;

    private void NotifyPairingChanged() => PairingChanged?.Invoke(this, EventArgs.Empty);

    public string? PairedServerFingerprint => _appSettings.PairedServerFingerprint;
    public string? PairedServerAlias       => _appSettings.PairedServerAlias;

    // True once the currently-paired server has actually approved this device
    // (see AppSettings.PairedServerTrustConfirmed).
    public bool IsPairedServerTrustConfirmed =>
        !string.IsNullOrEmpty(PairedServerFingerprint) && _appSettings.PairedServerTrustConfirmed;

    // Paired but not yet approved - the request has been sent and is sitting
    // at the server's own approval popup.
    public bool IsPairedServerAwaitingApproval =>
        !string.IsNullOrEmpty(PairedServerFingerprint) && !IsPairedServerTrustConfirmed;

    // Every currently-discovered peer advertising Server mode - the pool
    // ServerPickerView picks a pairing from. Unrelated to trust: an
    // untrusted server can still appear here, it just won't actually sync
    // until it approves this device (see SyncHttpServer.AuthorizeAsync).
    public IEnumerable<DiscoveredDevice> AvailableServers =>
        _networkDiscovery?.KnownDevices.Where(d => d.IsServer) ?? Enumerable.Empty<DiscoveredDevice>();

    // What this device calls itself to peers (shown in the sidebar's Devices
    // section on the other end, and in the trust-gate approval prompt) - see
    // DeviceIdentity.Alias for why this has to be user-editable rather than
    // read from the OS. The same DeviceIdentity instance is shared with
    // SyncHttpServer/PlaylistSyncService/LibrarySyncService/LibraryDownloadService
    // (see App.axaml.cs), so mutating it here takes effect immediately - no
    // restart needed for a rename to reach the next peer that asks.
    public string DeviceAlias
    {
        get => _deviceIdentity?.Alias ?? "";
        set
        {
            var trimmed = value.Trim();
            if (_deviceIdentity == null || string.IsNullOrEmpty(trimmed) || _deviceIdentity.Alias == trimmed)
                return;
            _logger.LogInformation("Device renamed: {Old} -> {New}", _deviceIdentity.Alias, trimmed);
            _deviceIdentity.Alias = trimmed;
            _ = (_deviceIdentityStore?.SaveAsync(_deviceIdentity) ?? Task.CompletedTask);
        }
    }

    // Manual pairing (see decision: a Client picks its one server explicitly,
    // no automatic first-found pairing, and no popup offering it the moment
    // a Server is seen - the user has to go looking, via the sidebar's
    // device-detail "Ask to pair" button or ServerPickerView) - called from
    // either of those.
    public void PairWithServer(DiscoveredDevice device)
    {
        _appSettings.PairedServerFingerprint = device.Fingerprint;
        _appSettings.PairedServerAlias = device.Alias;
        _appSettings.PairedServerTrustConfirmed = false; // a fresh request - see ConfirmServerTrust
        _ = (_appSettingsStore?.SaveAsync(_appSettings) ?? Task.CompletedTask);

        NotifyPairingChanged();
        // device is necessarily still live right now (it came from
        // AvailableServers, itself sourced from KnownDevices), so
        // Recompute() picks it up immediately rather than waiting for the
        // next DeviceDiscovered re-fire to notice.
        _reachability?.Recompute();

        // The server doesn't trust us yet - that's the whole point of asking -
        // so a bulk sync attempt right now would just get a flat 403 (see
        // SyncHttpServer's top-of-file doc comment: a sync request is never
        // itself treated as a pairing attempt anymore). Explicitly request
        // pairing first and only start syncing once - if - a human on the
        // other end actually approves it.
        RunTrackedSync(() => RequestPairingThenSyncAsync(device));
    }

    // See PairWithServer. Runs under RunTrackedSync so the "syncing" spinner
    // covers the wait for the other device's user to tap Allow/Deny, not just
    // the sync that follows approval.
    private async Task RequestPairingThenSyncAsync(DiscoveredDevice device)
    {
        var approved = await (_peerPairingService?.RequestPairingAsync(device) ?? Task.FromResult(false));

        // The user may have unpaired, or paired with someone else, while this
        // was in flight (approval can take up to a minute) - don't act on a
        // stale result either way.
        if (_appSettings.PairedServerFingerprint != device.Fingerprint)
            return;

        if (!approved)
        {
            _logger.LogWarning("Pair request to {Alias} ({Fingerprint}) was denied or timed out", device.Alias, device.Fingerprint);
            Dispatcher.UIThread.Post(UnpairServer);
            return;
        }

        ConfirmServerTrust(device.Fingerprint);
        // Marks this as TriggerSyncIfReady's own "first contact" so a later
        // DeviceDiscovered re-fire for this same peer this session doesn't
        // redundantly sync again right on top of this.
        _syncedDeviceFingerprints.TryAdd(device.Fingerprint, 0);
        await (_playlistSyncService?.SyncWithAsync(device, forceInitiator: true) ?? Task.CompletedTask);
        await SyncLibraryAndConfirmTrust(device);
    }

    // Marks the paired server as having actually approved this device - see
    // AppSettings.PairedServerTrustConfirmed's own doc comment. Called directly
    // once RequestPairingThenSyncAsync's own pair-request comes back approved,
    // and again (a cheap no-op by then) after any later bulk sync attempt that
    // reaches the server and gets past its trust gate (a 403 surfaces as
    // LibrarySyncResult.Success == false, so a true here really does mean
    // approved, not just reachable) - belt-and-suspenders in case trust was
    // somehow confirmed one way but not the other. Ignores a result for anyone
    // other than the currently-paired fingerprint (e.g. a stale in-flight sync
    // completing just after an Unpair/re-pair to someone else).
    private void ConfirmServerTrust(string? fingerprint)
    {
        if (_appSettings.PairedServerTrustConfirmed)
            return;
        if (string.IsNullOrEmpty(fingerprint) || fingerprint != _appSettings.PairedServerFingerprint)
            return;
        _appSettings.PairedServerTrustConfirmed = true;
        _ = (_appSettingsStore?.SaveAsync(_appSettings) ?? Task.CompletedTask);
        Dispatcher.UIThread.Post(NotifyPairingChanged);
    }

    // Wraps LibrarySyncService.SyncWithAsync with the ConfirmServerTrust hook
    // above - used anywhere a bulk sync is kicked off via RunTrackedSync
    // (TriggerSyncIfReady, RunPendingDeviceSyncs), which otherwise discards
    // the LibrarySyncResult. ForceSyncNow already awaits its own result
    // directly and calls ConfirmServerTrust itself instead of going through
    // this.
    private async Task SyncLibraryAndConfirmTrust(DiscoveredDevice device)
    {
        var result = await (_librarySyncService?.SyncWithAsync(device) ?? Task.FromResult(new LibrarySyncResult(false, 0, 0)));
        if (result.Success)
            ConfirmServerTrust(device.Fingerprint);
    }

    // ServerPickerView's "Unpair" action - must be called before pairing
    // with a different server (switching requires an explicit unpair-first
    // step, not a direct one-click switch).
    public void UnpairServer()
    {
        _appSettings.PairedServerFingerprint = null;
        _appSettings.PairedServerAlias = null;
        _appSettings.PairedServerTrustConfirmed = false;
        _ = (_appSettingsStore?.SaveAsync(_appSettings) ?? Task.CompletedTask);
        NotifyPairingChanged();
        _reachability?.Recompute();
    }

    // Clears the pairing pointer without the save/notify UnpairServer does -
    // used by MainViewModel's IsServer setter, which is already saving and
    // notifying around its own flip to Server mode. Not syncing again with the
    // old paired server (a deliberate requirement, not an oversight) -
    // library/playlists themselves are untouched by that flip, this only
    // clears the now-stale pairing pointer.
    public void ClearPairingForServerMode()
    {
        _appSettings.PairedServerFingerprint = null;
        _appSettings.PairedServerAlias = null;
    }

    // A paired Server no longer trusting us surfaces here every time this
    // device is in any kind of contact with it - not just while actively
    // syncing. Called on every DeviceDiscovered fire (fresh discovery, or a
    // changed /info field on an already-known peer - see
    // NetworkDiscoveryService.ResolveAliasAsync). This is what actually
    // catches a revoke that happened while this device wasn't reachable: the
    // next time the two are back in mDNS contact, the very first /info resolve
    // already carries the current answer, no separate "were we there for the
    // revoke" step required. PlaylistSyncService/LibrarySyncService's
    // PeerTrustRejected covers the same outcome slightly earlier if an actual
    // sync attempt happens to land first.
    public void HandlePeerTrustChanged(DiscoveredDevice device)
    {
        if (device.Fingerprint != PairedServerFingerprint || device.TrustsUs)
            return;
        HandleTrustRevoked(device.Alias, device.Fingerprint);
    }

    // The 403/unpair-notify counterpart to HandlePeerTrustChanged above -
    // wired to PlaylistSyncService/LibrarySyncService.PeerTrustRejected and
    // SyncHttpServer.PeerUnpairNotified. Same handler, same effect either way.
    public void HandleTrustRevoked(string alias, string fingerprint)
    {
        if (fingerprint != PairedServerFingerprint)
            return;
        _logger.LogWarning("Paired server {Alias} ({Fingerprint}) no longer trusts us - clearing stale local pairing",
            alias, fingerprint);
        Dispatcher.UIThread.Post(UnpairServer);
    }

    // ── Reachability and forced sync ──────────────────────────────────────

    // Whether the Client's paired Server (if any) is currently reachable -
    // a thin pass-through to PairedServerReachability, the single source of
    // truth for this (see that class's own doc comment).
    public bool IsPairedServerReachable => _reachability?.IsReachable ?? false;

    // "Sync Now" action (desktop's ServerPickerView, mobile's SettingsView) -
    // bypasses both _syncedDeviceFingerprints (the once-per-session dedup
    // TriggerSyncIfReady normally applies) and ScheduleContentSync's 5s
    // debounce, so the user can immediately retry a sync that appears stuck
    // or never completed (e.g. LibrarySyncService's own request timing out on
    // a large library) without waiting for the next discovery event or
    // relaunching the app. Requires the paired Server to be currently
    // discovered - same condition as IsPairedServerReachable.
    public bool CanForceSync => IsPairedServerReachable;

    // Set once ForceSyncNow's own awaited calls settle - unlike the automatic
    // trigger paths (TriggerSyncIfReady/DebouncedContentSyncAsync, which fire
    // RunTrackedSync and move on), this is a direct user action, so it's
    // worth reporting something more useful than silence: whether the peer
    // was actually reachable, and how many tracks changed, distinguishing
    // "reached the server but already up to date" from "couldn't reach it at
    // all" - both merge zero new tracks otherwise indistinguishably. Shown
    // next to the "Sync Now" button (desktop ServerPickerView, mobile
    // SettingsView).
    private string? _lastForceSyncResult;
    public string? LastForceSyncResult
    {
        get => _lastForceSyncResult;
        private set { _lastForceSyncResult = value; OnPropertyChanged(); }
    }

    // Bound directly to a command, so it has to be callable without an await -
    // hence the shim here over ForceSyncNowAsync, the awaitable body a test can
    // drive. Not `async void`: a throw on that path is unobserved by
    // TaskScheduler.UnobservedTaskException and tears the process down. Forget()
    // observes the fault and logs it instead.
    public void ForceSyncNow() => ForceSyncNowAsync().Forget(_logger, "Forced sync");

    public async Task ForceSyncNowAsync()
    {
        var pairedFingerprint = PairedServerFingerprint;
        if (string.IsNullOrEmpty(pairedFingerprint))
            return;
        var device = _reachability?.PairedServerDevice;
        if (device == null)
        {
            _logger.LogWarning("Force sync requested but paired server ({Fingerprint}) is not currently discovered", pairedFingerprint);
            LastForceSyncResult = "Server not currently found on the network";
            return;
        }

        _logger.LogInformation("Force sync requested with {Alias} ({Fingerprint})", device.Alias, device.Fingerprint);
        LastForceSyncResult = null;

        if (Interlocked.Increment(ref _activeSyncCount) == 1)
            NotifyIsSyncingChanged();
        try
        {
            var playlistTask = _playlistSyncService?.SyncWithAsync(device, forceInitiator: true) ?? Task.CompletedTask;
            var libraryTask = _librarySyncService?.SyncWithAsync(device) ?? Task.FromResult(new LibrarySyncResult(false, 0, 0));
            await Task.WhenAll(playlistTask, libraryTask);

            var libraryResult = await libraryTask;
            if (libraryResult.Success)
                ConfirmServerTrust(device.Fingerprint);
            LastForceSyncResult = !libraryResult.Success
                ? $"Could not reach {device.Alias} - check it's still on the network and paired"
                // Unchanged means the server answered 304 - its catalog is
                // exactly what was merged last time, so there is no fetched
                // count to report (see LibrarySyncResult).
                : libraryResult.Unchanged
                    ? $"Already up to date with {device.Alias}"
                    : libraryResult.AddedCount > 0
                        ? $"Added {libraryResult.AddedCount} new track(s) from {device.Alias}"
                        : $"Already up to date with {device.Alias} ({libraryResult.FetchedCount} track(s) checked)";
            _logger.LogInformation("Force sync result: {Result}", LastForceSyncResult);
        }
        finally
        {
            if (Interlocked.Decrement(ref _activeSyncCount) == 0)
                NotifyIsSyncingChanged();
        }
    }

    // ── Discovery-driven sync triggers ────────────────────────────────────

    // The last Library.ChangeToken observed on each peer's /info answer (see
    // DiscoveredDevice.LibraryToken). Purely a change detector - the value
    // itself means nothing to this device.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _observedPeerLibraryTokens = new();

    // Closes ARCHITECTURE-REVIEW Tier 1.4's correctness gap: a change made on
    // the Server was never noticed while both apps stayed running, because
    // sync fired only on first mDNS contact (TriggerSyncIfReady) or a
    // debounced *local* change (ScheduleContentSync). The ~5s /info poll every
    // Client already runs now carries the Server's library token, so a
    // server-side edit shows up as that token changing and syncs promptly -
    // no new endpoint, no long-lived connection to keep alive on mobile.
    //
    // Runs *before* TriggerSyncIfReady in the DeviceDiscovered handler, so the
    // first observation of a peer can be told apart from a later change: that
    // first one is TriggerSyncIfReady's initial sync to make, not this one's.
    // A redundant trigger is cheap anyway - LibrarySyncService sends the token
    // back as If-None-Match, so an unchanged catalog costs one 304.
    public void TriggerSyncIfPeerCatalogChanged(DiscoveredDevice device)
    {
        if (string.IsNullOrEmpty(device.Fingerprint) || string.IsNullOrEmpty(device.LibraryToken))
            return;
        if (!SyncRolePolicy.ShouldInitiateSync(_appSettings.IsServer, _appSettings.PairedServerFingerprint, device.Fingerprint))
            return;

        var isFirstObservation = !_observedPeerLibraryTokens.TryGetValue(device.Fingerprint, out var previousToken);
        _observedPeerLibraryTokens[device.Fingerprint] = device.LibraryToken;
        if (isFirstObservation || previousToken == device.LibraryToken)
            return;

        _logger.LogInformation("{Alias} ({Fingerprint}) reports a changed library ({Previous} -> {Current}), syncing",
            device.Alias, device.Fingerprint, previousToken, device.LibraryToken);
        RunTrackedSync(() => SyncLibraryAndConfirmTrust(device));
    }

    // Runs a playlist sync session (Phase 2) and a library sync session
    // (Phase 3 - see LibrarySyncService) with a newly (re-)discovered device
    // once each. DeviceDiscovered fires more than once per peer (mDNS fallback
    // alias, then the resolved /info alias+fingerprint), so this only fires
    // once the fingerprint is known and only the first time per session. Both
    // share this one dedup gate/trigger even though library sync itself has no
    // initiator election (see LibrarySyncService) - there's still only one
    // "first contact" per peer per session worth reacting to.
    public void TriggerSyncIfReady(DiscoveredDevice device)
    {
        if (string.IsNullOrEmpty(device.Fingerprint))
            return;
        // Server never initiates bulk sync; Client only ever bulk-syncs with
        // its one paired Server - see SyncRolePolicy.
        if (!SyncRolePolicy.ShouldInitiateSync(_appSettings.IsServer, _appSettings.PairedServerFingerprint, device.Fingerprint))
            return;
        if (!_syncedDeviceFingerprints.TryAdd(device.Fingerprint, 0))
            return;

        _logger.LogInformation("First contact with {Alias} ({Fingerprint}) this session, triggering initial sync",
            device.Alias, device.Fingerprint);
        // forceInitiator: true - this is always the Client's own paired
        // Server here (ShouldInitiateSync above already guarantees that), and
        // a Server never calls SyncWithAsync back (its own trigger paths are
        // gated off) - without this, PlaylistSyncService's ordinal-fingerprint
        // election could decide the Client isn't the initiator for roughly
        // half of all possible fingerprint pairs, and since the Server never
        // reciprocates, that pair would permanently never sync playlists.
        RunTrackedSync(() => _playlistSyncService?.SyncWithAsync(device, forceInitiator: true) ?? Task.CompletedTask);
        RunTrackedSync(() => SyncLibraryAndConfirmTrust(device));
    }

    // ── Peer track resolution ─────────────────────────────────────────────

    // Shared by DownloadTrackAsync and GetStreamUrl - delegates the actual
    // resolution (and the "only the currently paired Server" gating that goes
    // with it) to PeerTrackResolver, the one place that logic lives - see that
    // class's own doc comment. This wrapper only adds the warning log, since a
    // user-initiated download/stream attempt failing is worth reporting,
    // unlike AlbumArtLoader's own (much more frequent, per-row) calls into the
    // same resolver.
    private DiscoveredDevice? ResolvePeerForTrack(Track track)
    {
        var device = _peerTrackResolver?.Resolve(track);
        if (device == null)
            _logger.LogWarning("Cannot resolve a peer for {Title}: no currently paired, reachable origin device", track.Title);
        return device;
    }

    // Downloads one placeholder track's audio from whichever peer currently
    // holds it - see LibraryDownloadService, SYNC-PLAN.md Phase 3's mobile
    // download button.
    public Task<TrackDownloadResult> DownloadTrackAsync(Track track) =>
        _libraryDownloadService?.DownloadAsync(track, ResolvePeerForTrack(track)) ?? Task.FromResult(TrackDownloadResult.Failed);

    // Counterpart to DownloadTrackAsync - see LibraryDownloadService.DeleteDownloadedFileAsync.
    public Task DeleteDownloadedFileAsync(Track track) =>
        _libraryDownloadService?.DeleteDownloadedFileAsync(track) ?? Task.CompletedTask;

    // Builds an on-demand stream URL for a placeholder track from whichever
    // peer currently holds it, for playing without downloading first - see
    // MobileMainViewModel.PlayTrackCommand and PeerLibraryViewModel's own
    // identical streaming approach for peer-browsed (not yet synced-in)
    // tracks. Null if the peer isn't currently reachable (same resolution
    // DownloadTrackAsync uses) or this device's own identity isn't ready yet.
    public string? GetStreamUrl(Track track)
    {
        if (_deviceIdentity == null || _signingKey == null)
        {
            _logger.LogWarning("Cannot build a stream URL for {Title}: device identity/settings not ready yet", track.Title);
            return null;
        }
        // The id the origin peer itself gave this track, not one recomputed
        // here - see Track.OriginTrackId. Absent only for a track that never
        // came from a peer at all, which has no business on this path.
        if (track.OriginTrackId == null)
        {
            _logger.LogWarning("Cannot build a stream URL for {Title}: it carries no origin track id", track.Title);
            return null;
        }
        var peer = ResolvePeerForTrack(track);
        if (peer == null)
            return null; // ResolvePeerForTrack already logged why.

        var url = PeerOpenSubsonicClientFactory.Create(peer, _deviceIdentity, _appSettings, _signingKey).GetStreamUrl(track.OriginTrackId);
        _logger.LogInformation("Streaming {Title} from {Alias} ({EndPoint}): {Url}", track.Title, peer.Alias, peer.EndPoint, url);
        return url;
    }

    private readonly SubscriptionBag _subscriptions = new();

    // Stops the log-push timer and detaches from the process-wide log store.
    // Owned by MainViewModel, which disposes this alongside its own
    // subscriptions - see docs/ARCHITECTURE-REVIEW.md Tier 2.3.
    public void Dispose()
    {
        _logPushTimer.Stop();
        _subscriptions.Dispose();
    }
}
