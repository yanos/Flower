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
    private readonly Library? _library;
    private readonly LibrarySyncService? _librarySyncService;
    private readonly LibraryDownloadService? _libraryDownloadService;
    private readonly PeerPairingService? _peerPairingService;
    private readonly PeerTrackResolver? _peerTrackResolver;

    // Where a successfully-paired server's own public key is recorded, so
    // PeerHttpClient can pin its TLS certificate against it - see
    // PairWithServer. This device never approves anything else into it: it
    // does not serve, so nothing else ever asks.
    private readonly TrustedPeerStore? _trustedPeerStore;
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
        TrustedPeerStore? trustedPeerStore = null,
        DeviceIdentity? deviceIdentity = null,
        DeviceSigningKey? signingKey = null,
        Library? library = null)
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
        _trustedPeerStore       = trustedPeerStore;
        _deviceIdentity         = deviceIdentity;
        _signingKey             = signingKey;
        _library                = library;

        // A plain periodic tick that offers the paired Server whatever this
        // device has logged since the last successful push, entirely
        // independent of ScheduleContentSync's debounce. Only the log lines:
        // the catalog and playlists do not move because a line was logged, and
        // pulling them on this tick's cadence is what put a client permanently
        // over the server's bulk rate limit - see PushPendingLogsAsync.
        //
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
        //
        // This used to be armed by an InMemoryLogStore.EntryAdded subscription
        // setting a "something was logged" flag, which the tick then required.
        // That flag was a proxy for "the server is missing something", and a
        // bad one in both directions. Discovery deliberately drops its own
        // repeating chatter to Trace once it settles (see HandleUnreachable,
        // and "info updated" firing only when something changed), so an idle
        // device emits nothing at Debug+, never re-arms, and stops pushing
        // outright - which is why a phone delivered exactly one snapshot per
        // launch while a desktop playing music (whose decode watchdog logs a
        // Warning about once a second) pushed every five seconds forever. In
        // the other direction the flag was consumed before the attempt, so
        // activity accumulated while no peer was listed was dropped rather
        // than deferred. LibrarySyncService now owns the real answer as a
        // per-peer high-water mark over the buffer, so this tick can simply
        // ask every time and cost nothing when there is nothing to send.
        _logPushTimer = new DispatcherTimer { Interval = ContentSyncCooldown };
        _logPushTimer.Tick += (_, _) => LogPushTick();
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
        var devices = PendingSyncDevices();
        if (devices.Count == 0)
            return;

        foreach (var device in devices)
        {
            // forceInitiator: true - see TriggerSyncIfReady's identical
            // reasoning; every device here is already this device's own
            // paired server (MayRequestFrom above guarantees it).
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

    // Every currently-known, fingerprint-resolved peer this device should
    // bulk-sync with per SyncRolePolicy - not gated by
    // _syncedDeviceFingerprints (that dedup is specifically for "don't
    // double-sync from DeviceDiscovered re-firing at first contact" - see
    // TriggerSyncIfReady - and is orthogonal to resyncing on a later change).
    // Collapses to at most one device - the paired server - since that is the
    // only peer this device is allowed to dial at all.
    private List<DiscoveredDevice> PendingSyncDevices()
    {
        var pairedServerFingerprint = _appSettings.PairedServerFingerprint;
        return _host.ListedPeers
            .Where(d => d.Fingerprint.Length > 0 &&
                        SyncRolePolicy.MayRequestFrom(pairedServerFingerprint, d.Fingerprint))
            .ToList();
    }

    // Set for the duration of one tick's push, so a slow or hanging POST
    // doesn't have a second one stacked on top of it five seconds later. Only
    // ever touched from the timer's Tick and the continuation below, both on
    // the UI thread, so a plain bool is enough - and unlike _activeSyncCount
    // this deliberately does not drive IsSyncing: a log push is background
    // chatter, and blinking the spinner next to the server's name every five
    // seconds would read as the app perpetually syncing.
    private bool _logPushInFlight;

    // One firing of _logPushTimer. Internal rather than inline in the Tick
    // handler so a test can drive it directly: parking the headless dispatcher
    // long enough to let a real DispatcherTimer fire holds up every other
    // [AvaloniaFact] queued behind it - see MainViewModelSyncTriggerTests'
    // PumpUntil on why that is avoided throughout this suite.
    internal void LogPushTick()
    {
        if (_activeSyncCount != 0 || _logPushInFlight || _librarySyncService == null)
            return;

        // Runs whether or not there is anywhere to push to: draining the memory
        // ring onto disk is the half that must not depend on a server being
        // listed, since lines logged while nothing was reachable are exactly
        // the ones someone will later go looking for.
        _logPushInFlight = true;
        _ = PushPendingLogsAsync(PendingSyncDevices());
    }

    private async Task PushPendingLogsAsync(List<DiscoveredDevice> devices)
    {
        var allSucceeded = true;
        try
        {
            await _librarySyncService!.ArchiveOwnLogsAsync();
            foreach (var device in devices)
                allSucceeded &= await _librarySyncService!.PushLogsOnlyAsync(device);
        }
        catch (Exception ex)
        {
            // LibrarySyncService normally converts transport failures to a
            // false result. Keep the fire-and-forget coordinator safe if an
            // unexpected implementation failure escapes that boundary.
            allSucceeded = false;
            _logger.LogDebug(ex, "Unexpected failure while pushing logs to the paired server");
        }
        finally
        {
            // A failed push stays pending on its own: LibrarySyncService only
            // advances a peer's watermark on a 2xx, so the next timer tick
            // re-offers the same lines - including after a remembered remote
            // address becomes reachable again.
            if (!allSucceeded)
                _logger.LogTrace("Log push did not fully succeed; the next tick will retry the same lines");
            _logPushInFlight = false;
        }
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

    // Paired but not yet confirmed - the code was redeemed and the first sync
    // that proves the server really did accept it has not landed yet.
    public bool IsPairedServerAwaitingApproval =>
        !string.IsNullOrEmpty(PairedServerFingerprint) && !IsPairedServerTrustConfirmed;

    // Every currently-discovered server - the pool ServerPickerView (and
    // mobile's SettingsView) picks a pairing from. Everything discovery finds
    // is one now, so this is simply what it knows about. Unrelated to trust:
    // an untrusted server still appears here, it just will not sync until a
    // code has been redeemed against it.
    //
    // One entry per server that has said who it is. KnownDevices already
    // dedupes by fingerprint, but it deliberately keeps entries that have
    // none - an address that has never answered /info - since it cannot prove
    // those are duplicates of anything. For a *picker* that is the wrong
    // trade: an unidentified address is not a server anyone can pair with
    // (pairing pins a fingerprint), so each one only ever appeared as an
    // extra dead row labelled with a raw URL. Filtered here rather than in
    // either view, so both heads agree on what "an available server" is.
    public IEnumerable<DiscoveredDevice> AvailableServers =>
        _networkDiscovery?.KnownDevices.Where(d => !string.IsNullOrEmpty(d.Fingerprint))
        ?? Enumerable.Empty<DiscoveredDevice>();

    // What this device calls itself to the server (shown in its Devices list,
    // and against this device's log snapshots there) - see DeviceIdentity.Alias
    // for why this has to be user-editable rather than read from the OS. The
    // same DeviceIdentity instance is shared with NetworkDiscoveryService/
    // PlaylistSyncService/LibrarySyncService/LibraryDownloadService (see
    // App.axaml.cs), so mutating it here takes effect immediately - no restart
    // needed for a rename to reach the server's next answer.
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

    // Manual pairing (see decision: this device picks its one server
    // explicitly, no automatic first-found pairing and no popup offering it
    // the moment a server is seen - the user has to go looking, via the
    // sidebar's device-detail Pair button or ServerPickerView) - called from
    // either of those.
    //
    // pairingCode is the whole of the authorization: a server is headless, so
    // there is nobody in front of it to tap Allow, and an admin issues a
    // one-time code instead (SYNC-PLAN.md, "Passwordless by design").
    public void PairWithServer(DiscoveredDevice device, string pairingCode)
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

        // The server doesn't trust us yet, so a bulk sync attempt right now
        // would just get a flat 403 - a sync request is never itself treated
        // as a pairing attempt. Redeem the code first, and only start syncing
        // once that comes back accepted.
        RunTrackedSync(() => RedeemPairingCodeThenSyncAsync(device, pairingCode));
    }

    // The code the admin issued *is* the approval, so there is no waiting
    // state at all - the redeem either comes back trusted or the code was
    // wrong, and the difference is known within one round trip. See
    // PeerPairingService.RedeemPairingCodeAsync.
    private async Task RedeemPairingCodeThenSyncAsync(DiscoveredDevice device, string pairingCode)
    {
        var rejection = await (_peerPairingService?.RedeemPairingCodeAsync(device, pairingCode)
                               ?? Task.FromResult<string?>("Pairing is not available on this device."));

        if (_appSettings.PairedServerFingerprint != device.Fingerprint)
            return;

        if (rejection != null)
        {
            // Roll the pairing straight back rather than leaving the UI in
            // "Waiting for server..." - nothing is coming. A bad code is a
            // retry, not a state to sit in.
            _logger.LogWarning(
                "Pairing code for {Alias} ({Fingerprint}) was rejected: {Reason}",
                device.Alias, device.Fingerprint, rejection);
            Dispatcher.UIThread.Post(UnpairServer);
            PairingCodeRejected?.Invoke(this, rejection);
            return;
        }

        // Record the server on this side too, which is what lets
        // PeerHttpClient pin its TLS certificate from here on instead of
        // falling back to ordinary chain validation a self-signed server
        // cannot pass - see DiscoveredDevice.PublicKey and PeerHttpClient.
        // IsPinnedServerKey. Skipped, rather than guessed at, if /info has
        // not produced a key yet: pinning nothing is a refusal, and pinning
        // the wrong thing is worse.
        if (_trustedPeerStore != null && device.PublicKey.Length > 0)
            await _trustedPeerStore.ApproveAsync(device.Fingerprint, device.Alias, device.PublicKey);

        ConfirmServerTrust(device.Fingerprint);
        _syncedDeviceFingerprints.TryAdd(device.Fingerprint, 0);
        await (_playlistSyncService?.SyncWithAsync(device, forceInitiator: true) ?? Task.CompletedTask);
        await SyncLibraryAndConfirmTrust(device);
    }

    // Raised when a redeem came back rejected, so the view can say so instead
    // of the pairing simply appearing not to have happened. Deliberately an
    // event and not a piece of state: the message is about one attempt, and
    // the next keystroke in the code box should clear it. Carries the reason
    // as a finished, showable sentence - see PeerPairingService.
    // DescribeRejectionAsync for who phrases what.
    public event EventHandler<string>? PairingCodeRejected;

    // Marks the paired server as having actually approved this device - see
    // AppSettings.PairedServerTrustConfirmed's own doc comment. Called directly
    // once the code redeem comes back accepted, and again (a cheap no-op by then) after any later bulk sync attempt that
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

    // "Last synced" for the Devices screen, stamped wherever a bulk sync with
    // the paired server actually came back successful - the forced one and the
    // discovery-driven one alike, since a user asking how fresh their library is
    // does not care which of the two fetched it. Same guard as
    // ConfirmServerTrust: a stale in-flight sync landing after an unpair must
    // not stamp a server this device is no longer paired to.
    private void RecordSyncedNow(string? fingerprint)
    {
        if (string.IsNullOrEmpty(fingerprint) || fingerprint != _appSettings.PairedServerFingerprint)
            return;

        _appSettings.PairedServerLastSyncedAt = DateTimeOffset.Now;
        _ = (_appSettingsStore?.SaveAsync(_appSettings) ?? Task.CompletedTask);
        Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(LastSyncedAt)));
    }

    // Null until this device has ever completed one with its current pairing.
    public DateTimeOffset? LastSyncedAt => _appSettings.PairedServerLastSyncedAt;

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
        {
            ConfirmServerTrust(device.Fingerprint);
            RecordSyncedNow(device.Fingerprint);
        }
    }

    // ServerPickerView's "Unpair" action - must be called before pairing
    // with a different server (switching requires an explicit unpair-first
    // step, not a direct one-click switch).
    public void UnpairServer()
    {
        // Before the pointer is cleared, because clearing it is what makes the
        // fingerprint unrecoverable. Everything that server was the only source
        // of goes with the pairing: a placeholder is a promise it would serve
        // the file on request, and that promise is void the moment this runs.
        // Leaving them behind is how a client ended up sitting on a library of
        // 86 rows that could not be played and could not be got rid of - every
        // click logging "no currently paired, reachable origin device", every
        // relaunch carrying the same rows forward through a rescan that found
        // nothing (see Library.RemoveTracksFromOrigin, and the carry-forward
        // predicate in UpdateTracks that now refuses to keep them either).
        if (_appSettings.PairedServerFingerprint is { } origin)
            _library?.RemoveTracksFromOrigin(origin);

        // Drops the TLS pin along with the pairing - see PairWithServer for
        // why it was recorded. A certificate this device would have accepted
        // on the strength of a pairing should stop being acceptable the moment
        // the pairing does.
        if (_appSettings.PairedServerFingerprint is { } paired && _trustedPeerStore != null)
            _ = _trustedPeerStore.RevokeAsync(paired);

        _appSettings.PairedServerFingerprint = null;
        _appSettings.PairedServerAlias = null;
        _appSettings.PairedServerTrustConfirmed = false;
        _appSettings.PairedServerLastSyncedAt = null;
        _ = (_appSettingsStore?.SaveAsync(_appSettings) ?? Task.CompletedTask);
        NotifyPairingChanged();
        _reachability?.Recompute();
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

    // The 403 counterpart to HandlePeerTrustChanged above - wired to
    // PlaylistSyncService/LibrarySyncService.PeerTrustRejected. Same handler,
    // same effect whether the revoke is noticed from a refused request or from
    // the /info poll.
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

    // How the paired server is being reached, for the settings screens to show.
    // Null when it is not reached at all (the existing "Server not reachable"
    // text covers that) and empty for the ordinary case of being at home,
    // where naming the obvious would just be noise. Worth surfacing at all
    // because a fallback from the LAN to a possibly-relayed tailnet path is
    // silent, and "why has this got slow" otherwise has no answer anywhere in
    // the app. See PairedServerReachability.Route.
    public string? PairedServerRouteDescription => _reachability?.Route switch
    {
        ServerRoute.Tailnet => "Connected over your tailnet",
        ServerRoute.Remote => "Connected over a remote address",
        _ => null,
    };

    // Addresses the user typed for a server they cannot discover - the
    // bootstrap case, and only that: a server paired with on the LAN reports
    // its own addresses and needs none of this. See
    // docs/REMOTE-ACCESS-PLAN.md.
    public IEnumerable<string> ManualServerAddresses => _appSettings.ManualServerAddresses;

    // Returns the server if the address answered, null otherwise. A caller shows
    // the failure rather than leaving a row that will never resolve - the
    // overwhelmingly likely cause is a typo, and finding that out at pairing
    // time is far better than at the coffee shop. The device itself, not just a
    // bool, because the settings screen pairs with it in the same gesture that
    // added it (see ServerPickerView's Pair button) and would otherwise have to
    // go looking for what it just created.
    public async Task<DiscoveredDevice?> AddManualServerAsync(string address)
    {
        var trimmed = address.Trim();
        if (trimmed.Length == 0 || _networkDiscovery == null)
            return null;

        if (!_appSettings.ManualServerAddresses.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
        {
            _appSettings.ManualServerAddresses.Add(trimmed);
            _ = (_appSettingsStore?.SaveAsync(_appSettings) ?? Task.CompletedTask);
        }

        var device = await _networkDiscovery.AddRememberedAsync(trimmed);
        _reachability?.Recompute();
        NotifyPairingChanged();
        return device is { IsResponding: true } ? device : null;
    }

    public void RemoveManualServer(string address)
    {
        if (!_appSettings.ManualServerAddresses.Remove(address))
            return;

        _ = (_appSettingsStore?.SaveAsync(_appSettings) ?? Task.CompletedTask);
        _networkDiscovery?.RemoveRemembered(address);
        _reachability?.Recompute();
        NotifyPairingChanged();
    }

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
            {
                ConfirmServerTrust(device.Fingerprint);
                RecordSyncedNow(device.Fingerprint);
            }
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
    // debounced *local* change (ScheduleContentSync). The ~5s /info poll this
    // device already runs now carries the server's library token, so a
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
        if (!SyncRolePolicy.MayRequestFrom(_appSettings.PairedServerFingerprint, device.Fingerprint))
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
        // This device only ever bulk-syncs with its one paired server - see
        // SyncRolePolicy.
        if (!SyncRolePolicy.MayRequestFrom(_appSettings.PairedServerFingerprint, device.Fingerprint))
            return;
        if (!_syncedDeviceFingerprints.TryAdd(device.Fingerprint, 0))
            return;

        _logger.LogInformation("First contact with {Alias} ({Fingerprint}) this session, triggering initial sync",
            device.Alias, device.Fingerprint);
        // forceInitiator: true - this is always this device's own paired
        // server here (MayRequestFrom above already guarantees that), and a
        // server never calls SyncWithAsync back (it does not dial out at all)
        // - without this, PlaylistSyncService's ordinal-fingerprint election
        // could decide this side isn't the initiator for roughly half of all
        // possible fingerprint pairs, and since the server never reciprocates,
        // that pair would permanently never sync playlists.
        RunTrackedSync(() => _playlistSyncService?.SyncWithAsync(device, forceInitiator: true) ?? Task.CompletedTask);
        RunTrackedSync(() => SyncLibraryAndConfirmTrust(device));
    }

    // ── Peer track resolution ─────────────────────────────────────────────

    // Shared by DownloadTrackAsync and GetStreamUrl - delegates the actual
    // resolution (and the "only the currently paired server" gating that goes
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
