using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Flower.Models;
using Flower.Persistence;

namespace Flower.Services;

public enum PlaylistConflictChoice { KeepLocal, KeepRemote }

// Raised when the server refuses a gated request as device-unknown (see
// FlowerProblem) - i.e. it has no key for this device, as opposed to being
// merely unreachable or refusing one request's signature. Shared by PlaylistSyncService and LibrarySyncService, both of
// which hit the same trust gate (see Flower.Server's SyncEndpoints) as the first
// request of their own sync session. MainViewModel uses this to notice a paired
// server has revoked (or
// never granted) trust and clear the stale local PairedServerFingerprint,
// rather than leaving the UI claiming "paired" indefinitely - see
// MainViewModel's own subscription for why that drift is otherwise invisible.
public sealed class PeerTrustRejectedEventArgs : EventArgs
{
    public required string Fingerprint { get; init; }
    public required string Alias { get; init; }
}

// Raised when the same playlist changed on both this device and a peer since they
// last agreed - see PlaylistSyncPlanner. The UI is expected to ask the user which
// version to keep and report back via Resolution; SyncWithAsync suspends that one
// playlist's merge (not the whole session) until it does.
public sealed class PlaylistConflictEventArgs : EventArgs
{
    public required Playlist Local { get; init; }
    public required PlaylistSyncPlaylistDto Remote { get; init; }
    public required string RemoteAlias { get; init; }
    public required TaskCompletionSource<PlaylistConflictChoice> Resolution { get; init; }
}

// Orchestrates a playlist sync session with one discovered peer (see SYNC-PLAN.md
// Phase 2). Pure I/O/coordination shell around PlaylistSyncPlanner, which does the
// actual merge decisions and is unit tested on its own.
public class PlaylistSyncService
{
    // Pinned, for the same reason LibrarySyncService's is - see its remarks.
    private static readonly HttpClient Http = PeerHttpClient.Create(TimeSpan.FromSeconds(10));

    private readonly Library _library;
    private readonly DeviceIdentity _deviceIdentity;
    private readonly IPeerCredentials _credentials;
    private readonly ILogger _logger;
    private readonly PlaylistSyncStateStore _syncStateStore;
    private readonly DeviceNicknameStore _deviceNicknameStore;

    public event EventHandler<PlaylistConflictEventArgs>? ConflictDetected;
    public event EventHandler<PeerTrustRejectedEventArgs>? PeerTrustRejected;

    public PlaylistSyncService(
        Library library,
        DeviceIdentity deviceIdentity,
        DeviceSigningKey signingKey,
        AppSettings appSettings,
        PlaylistSyncStateStore syncStateStore,
        DeviceNicknameStore deviceNicknameStore,
        ILogger<PlaylistSyncService> logger)
    {
        _library = library;
        _deviceIdentity = deviceIdentity;
        // See LibrarySyncService's own note: built here from the three the
        // container already supplies, rather than injected separately.
        _credentials = new SignedDeviceCredentials(deviceIdentity, signingKey);
        _syncStateStore = syncStateStore;
        _deviceNicknameStore = deviceNicknameStore;
        _logger = logger;
    }

    // forceInitiator is set by MainViewModel's Client-side triggers (see
    // SyncRolePolicy) - under Client/Server roles, a Client is the only side
    // that ever calls this for a given pair (a Server's own trigger paths are
    // gated off entirely, so it never reciprocates), so it must always be the
    // initiator regardless of the ordinal comparison below, which would
    // otherwise (for roughly half of all possible fingerprint pairs) decide
    // the Client isn't the initiator and leave that pair permanently unsynced.
    // Virtual for the same reason as LibrarySyncService.SyncWithAsync - see
    // its comment, and docs/ARCHITECTURE-REVIEW.md Tier 5.6.
    public virtual async Task SyncWithAsync(DiscoveredDevice device, bool forceInitiator = false)
    {
        // One session at a time. Several triggers can fire together - first
        // contact, a playlists-token change, the pass after a catalog pull
        // brought new songs in - and two sessions interleaved each plan
        // against a library the other is about to replace, and save baselines
        // over each other's.
        await _sessionGate.WaitAsync();
        try
        {
            await SyncOnceAsync(device, forceInitiator);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private readonly SemaphoreSlim _sessionGate = new(1, 1);

    private async Task SyncOnceAsync(DiscoveredDevice device, bool forceInitiator)
    {
        if (string.IsNullOrEmpty(device.Fingerprint))
        {
            _logger.LogTrace("Playlist sync skipped for {Alias}: no resolved fingerprint yet", device.Alias);
            return;
        }

        // Exactly one side of a discovery pair initiates a sync session - the
        // other just waits to receive the initiator's /apply push once it's done.
        // Ordinal comparison is arbitrary but deterministic and identical on both
        // devices (each compares its own fingerprint against the other's), so a
        // pair never both initiate (double conflict prompts, racing writes) or
        // both stay silent. Skipped entirely when forceInitiator is set - see
        // this method's own doc comment above.
        if (!forceInitiator && string.CompareOrdinal(_deviceIdentity.Fingerprint, device.Fingerprint) >= 0)
        {
            _logger.LogDebug("Playlist sync with {Alias} ({Fingerprint}): not the initiator, waiting for their push instead",
                device.Alias, device.Fingerprint);
            return;
        }

        _logger.LogInformation("Playlist sync starting with {Alias} ({Fingerprint}) at {EndPoint}",
            device.Alias, device.Fingerprint, device.BaseUri);

        // A local nickname (see DeviceNicknameStore - the same override the
        // sidebar's "Rename Device" and Trusted Devices window use) wins over
        // the peer's own raw self-reported alias here too, so the conflict
        // dialog's "Keep X's Version" matches what this device is actually
        // called elsewhere in the UI.
        var remoteDisplayName = _deviceNicknameStore.Get(device.Fingerprint) ?? device.Alias;

        List<PlaylistSyncPlaylistDto> remotePlaylists;
        try
        {
            const string getPath = "/api/flower/v1/playlists";
            using var getRequest = new HttpRequestMessage(HttpMethod.Get, device.Url(getPath));
            await AddSignedIdentityHeadersAsync(getRequest, body: []);
            using var getResponse = await Http.SendAsync(getRequest);
            // A refusal throws with the server's reason, and with whether this
            // server's pinned key signed it - see below.
            await getResponse.EnsureSuccessAsync(device.PublicKey, device.Fingerprint);
            var json = await getResponse.Content.ReadAsStringAsync();
            var manifest = JsonSerializer.Deserialize(json, FlowerJsonContext.Default.PlaylistSyncManifestDto);
            remotePlaylists = manifest?.Playlists ?? new List<PlaylistSyncPlaylistDto>();
        }
        catch (Exception ex)
        {
            // Peer unreachable, not running this endpoint yet, or not (yet) trusted.
            _logger.LogWarning(ex, "Playlist sync with {Alias} ({Fingerprint}): GET /playlists failed, aborting this sync attempt",
                device.Alias, device.Fingerprint);

            // device-unknown specifically means the peer is up and answered, and
            // has no key for this device - distinct from every other failure
            // above, which just means "couldn't tell." Notably distinct from the
            // other 401s, signature-invalid, clock-skew and nonce-reused (a
            // stale timestamp, most often, after this device suspended with the
            // request in flight) - those must never unpair anything, they just
            // fail this attempt. And only when this server's pinned key signed
            // it: it used to be the status alone, 403, which anything on the
            // path could answer. See FlowerProblem, ServerResponseSignature and
            // PeerTrustRejectedEventArgs.
            if (PeerResponses.IsDeviceUnknown(ex))
                PeerTrustRejected?.Invoke(this, new PeerTrustRejectedEventArgs { Fingerprint = device.Fingerprint, Alias = device.Alias });

            return;
        }

        _logger.LogInformation("Playlist sync with {Alias}: fetched {RemoteCount} remote playlist(s), have {LocalCount} local",
            device.Alias, remotePlaylists.Count, _library.Playlists.Count);

        var baselines = _syncStateStore.LoadBaselines(device.Fingerprint);
        var index = new PlaylistTrackIndex(_library.Tracks);
        // What the merge is worked out from, kept so it can be laid over
        // whatever the user does before it is installed - see
        // PlaylistSessionOverlay. Taken now: the playlists themselves are
        // edited in place, so a later read would see those edits as the
        // version planned from.
        var plannedFrom = _library.Playlists;
        var planned = plannedFrom.ToDictionary(p => p.Id, PlannedPlaylist.Of);
        var decisions = PlaylistSyncPlanner.Plan(
            plannedFrom,
            remotePlaylists,
            id => baselines.TryGetValue(id, out var v) ? v : null,
            index);

        var finalPlaylists = new List<Playlist>();
        var newBaselines = new Dictionary<Guid, DateTimeOffset>(baselines);

        // Deleted here, and still there: the peer is told outright, because
        // it no longer reads a playlist missing from the push as deleted (see
        // PlaylistSyncMapper.ApplyPushedManifest).
        var deletedHere = new List<Guid>();

        foreach (var decision in decisions)
        {
            var name = decision.Local?.Name ?? decision.Remote?.Name ?? "?";
            // One line per playlist per sync session, and the overwhelmingly
            // common decision is "nothing to do" - Trace, so the interesting
            // outcomes (the conflict and delete-vs-edit lines further down,
            // which stay at Information) are not buried under a per-playlist
            // roll call every time two devices agree.
            _logger.LogTrace("Playlist sync with {Alias}: \"{Name}\" ({PlaylistId}) -> {Decision}",
                device.Alias, name, decision.PlaylistId, decision.Kind);

            // Deleted on one side (see PlaylistSyncPlanner.Delete) - drop it from
            // the merged result (and its baseline, since it no longer exists to
            // have one) rather than resolving it to some Playlist to keep.
            if (decision.Kind == PlaylistSyncDecisionKind.Delete)
            {
                newBaselines.Remove(decision.PlaylistId);
                if (decision.Remote != null)
                    deletedHere.Add(decision.PlaylistId);
                continue;
            }

            var resolved = decision.Kind switch
            {
                PlaylistSyncDecisionKind.NoChange  => decision.Local!,
                PlaylistSyncDecisionKind.KeepLocal => decision.Local!,
                PlaylistSyncDecisionKind.AdoptRemote => PlaylistSyncMapper.ToPlaylist(decision.Remote!, index, _logger),
                PlaylistSyncDecisionKind.Conflict => await ResolveConflictAsync(decision, remoteDisplayName, index),
                _ => throw new ArgumentOutOfRangeException(),
            };

            finalPlaylists.Add(resolved);
            newBaselines[decision.PlaylistId] = resolved.UpdatedAt;
        }

        // Persisted by Library.PlaylistsChanged. Laid over what the user did
        // while this session ran (PlaylistSessionOverlay): a playlist deleted
        // here mid-session keeps the baseline just agreed, so the next session
        // reads it as deleted here and tells the server; one created
        // mid-session has none yet, and goes up as new.
        PlaylistSessionResult result = null!;
        _library.ReplacePlaylists(current =>
        {
            result = PlaylistSessionOverlay.Apply(finalPlaylists, planned, current);
            return result.Installed;
        });
        var installed = result.Installed;

        // An edit made here mid-session and combined with the other side's
        // is a new version, and is agreed once the push below lands.
        foreach (var playlist in installed.Where(p => result.Merged.Contains(p.Id)))
            newBaselines[playlist.Id] = playlist.UpdatedAt;

        // One that could not be combined is not agreed at all: its baseline
        // goes back to what it was, so the session the edit scheduled finds
        // both sides changed and asks which to keep - see ConflictDetected.
        foreach (var id in result.HeldBack)
        {
            _logger.LogInformation(
                "Playlist sync with {Alias}: \"{Name}\" was edited here while the session ran and cannot be combined with their edit - left for the next session to ask about",
                device.Alias, installed.First(p => p.Id == id).Name);
            if (baselines.TryGetValue(id, out var agreed))
                newBaselines[id] = agreed;
            else
                newBaselines.Remove(id);
        }

        await _syncStateStore.SaveBaselinesAsync(device.Fingerprint, newBaselines);

        try
        {
            // Held back from the push too: the server takes a copy newer than
            // its own, which this is, and the other edit would go unasked.
            var pushed = installed.Where(p => !result.HeldBack.Contains(p.Id)).ToList();
            var manifest = PlaylistSyncMapper.ToManifest(_deviceIdentity.Fingerprint, pushed, deletedHere);
            var bodyBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, FlowerJsonContext.Default.PlaylistSyncManifestDto));
            const string postPath = "/api/flower/v1/playlists/apply";
            using var content = new ByteArrayContent(bodyBytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            using var postRequest = new HttpRequestMessage(HttpMethod.Post, device.Url(postPath)) { Content = content };
            await AddSignedIdentityHeadersAsync(postRequest, bodyBytes);
            using var postResponse = await Http.SendAsync(postRequest);
            postResponse.EnsureSuccessStatusCode();
            _logger.LogInformation("Playlist sync with {Alias}: pushed {Count} playlist(s) to their /apply successfully",
                device.Alias, installed.Count);

            // Logged, and nothing else: see PlaylistApplyResponseDto for why
            // the next session, not this answer, is what drops them here.
            var answer = JsonSerializer.Deserialize(
                await postResponse.Content.ReadAsStringAsync(), PlaylistSyncJsonContext.Default.PlaylistApplyResponseDto);
            if (answer?.Refused is { Count: > 0 } refused)
            {
                _logger.LogInformation(
                    "Playlist sync with {Alias}: the server refused {Count} playlist(s) that belong to another listener on it ({PlaylistIds}); they will be dropped here on the next sync",
                    device.Alias, refused.Count, string.Join(", ", refused));
            }
        }
        catch (Exception ex)
        {
            // Peer went away mid-session, or hasn't approved us via the trust gate
            // yet - our own state is already fully merged and saved either way; it
            // converges next time these two devices are both up (and trusted).
            _logger.LogWarning(ex, "Playlist sync with {Alias}: POST /apply failed - our own merge is saved, but the peer did not receive it this time",
                device.Alias);
        }
    }

    // See Flower.Server's trust gate - every gated endpoint requires these
    // (now including a signature proving possession of the private key behind
    // Fingerprint, not just the fingerprint string itself - see
    // DeviceSigningKey/SignatureVerifier) to evaluate trust. ConnectionClose
    // forces a fresh connection per request rather than pooling/reusing one -
    // sync sessions are now just a couple of requests each (see LibrarySyncService's
    // own history of this), so the extra handshake is negligible, and it avoids
    // HttpClient trying to reuse a keep-alive connection the server (or the
    // OS, e.g. after iOS backgrounds the app - see
    // SYNC-PLAN.md's foreground-only note) has already torn down - observed in
    // practice as "Connection reset by peer" / "Socket is not connected" on iOS.
    private async Task AddSignedIdentityHeadersAsync(HttpRequestMessage request, byte[] body)
    {
        await request.AddPeerCredentialsAsync(_credentials, body);
        request.Headers.ConnectionClose = true;
    }

    private async Task<Playlist> ResolveConflictAsync(PlaylistSyncDecision decision, string remoteAlias, PlaylistTrackIndex index)
    {
        // Delete-vs-edit: one side deleted a playlist the two devices had
        // previously agreed on, while the other side edited it since that same
        // baseline (see PlaylistSyncPlanner). Only one side has anything left to
        // show, so the two-column "yours vs. theirs" prompt below has nothing to
        // put in one of its columns.
        //
        // Resolved without asking, in favour of the surviving edit. An edit
        // beating a delete is the safe direction - the worst case is a playlist
        // the user meant to delete coming back (visible, one tap to delete
        // again, and it converges because the merge is pushed straight back to
        // the peer's /apply below), against a worst case the other way of edits
        // vanishing with nothing on screen to say so. That silent loss is
        // exactly what this whole branch exists to stop; a real "they deleted
        // this, keep it?" prompt is worth adding, but it needs its own UI on
        // both desktop and mobile - see docs/ARCHITECTURE-REVIEW.md.
        if (decision.Local == null || decision.Remote == null)
        {
            var survivor = decision.Local ?? PlaylistSyncMapper.ToPlaylist(decision.Remote!, index, _logger);
            _logger.LogInformation(
                "Playlist {Name}: deleted on {DeletedSide} but edited on the other side since they last agreed - keeping the edit rather than propagating the delete",
                survivor.Name, decision.Local == null ? "this device" : remoteAlias);
            return survivor;
        }

        var handler = ConflictDetected;
        if (handler == null)
        {
            // No UI listening (e.g. sync running before the view attaches) -
            // keep local rather than silently discarding it.
            return KeepLocalOver(decision.Local!, decision.Remote!);
        }

        var tcs = new TaskCompletionSource<PlaylistConflictChoice>();
        handler.Invoke(this, new PlaylistConflictEventArgs
        {
            Local = decision.Local!,
            Remote = decision.Remote!,
            RemoteAlias = remoteAlias,
            Resolution = tcs,
        });

        var choice = await tcs.Task;
        _logger.LogInformation("Playlist conflict for {Name} with {RemoteAlias} resolved: {Choice}",
            decision.Local!.Name, remoteAlias, choice);
        return choice == PlaylistConflictChoice.KeepLocal
            ? KeepLocalOver(decision.Local!, decision.Remote!)
            : PlaylistSyncMapper.ToPlaylist(decision.Remote!, index, _logger);
    }

    // Keeping this device's version of a conflict is an edit made after the
    // remote one, whatever the two clocks said: the peer takes a pushed copy
    // only when it is newer than its own (PlaylistSyncMapper.ApplyPushedManifest),
    // so a local copy that happened to carry the older timestamp was kept here
    // and quietly refused there.
    private static Playlist KeepLocalOver(Playlist local, PlaylistSyncPlaylistDto remote)
    {
        local.MarkEditedAfter(remote.UpdatedAt);
        return local;
    }
}
