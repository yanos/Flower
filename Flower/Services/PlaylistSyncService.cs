using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Flower.Models;
using Flower.Persistence;

namespace Flower.Services;

public enum PlaylistConflictChoice { KeepLocal, KeepRemote }

// Raised when a peer's SyncHttpServer answers 403 to a gated request - i.e. it
// has actively decided this device is not (or no longer) trusted, as opposed
// to being merely unreachable. Shared by PlaylistSyncService and
// LibrarySyncService, both of which hit the same trust gate (see
// SyncHttpServer.AuthorizeAsync) as the first request of their own sync
// session. MainViewModel uses this to notice a paired Server has revoked (or
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
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly Library _library;
    private readonly DeviceIdentity _deviceIdentity;
    private readonly DeviceSigningKey _signingKey;
    private readonly AppSettings _appSettings;
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
        _signingKey = signingKey;
        _appSettings = appSettings;
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
        if (string.IsNullOrEmpty(device.Fingerprint))
        {
            _logger.LogDebug("Playlist sync skipped for {Alias}: no resolved fingerprint yet", device.Alias);
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
            device.Alias, device.Fingerprint, device.EndPoint);

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
            using var getRequest = new HttpRequestMessage(HttpMethod.Get, $"http://{device.EndPoint}{getPath}");
            AddSignedIdentityHeaders(getRequest, "GET", getPath, body: []);
            using var getResponse = await Http.SendAsync(getRequest);
            getResponse.EnsureSuccessStatusCode(); // Throws on a 403 from an unapproved trust gate - handled below like any other unreachable peer.
            var json = await getResponse.Content.ReadAsStringAsync();
            var manifest = JsonSerializer.Deserialize(json, FlowerJsonContext.Default.PlaylistSyncManifestDto);
            remotePlaylists = manifest?.Playlists ?? new List<PlaylistSyncPlaylistDto>();
        }
        catch (Exception ex)
        {
            // Peer unreachable, not running this endpoint yet, or not (yet) trusted.
            _logger.LogWarning(ex, "Playlist sync with {Alias} ({Fingerprint}): GET /playlists failed, aborting this sync attempt",
                device.Alias, device.Fingerprint);

            // A 403 specifically means the peer is up and answered, but has actively
            // decided not to trust us - distinct from every other failure above,
            // which just means "couldn't tell." Notably distinct from a 401, which
            // both peer servers answer a signature that did not verify with (a
            // stale timestamp, most often, after this device suspended with the
            // request in flight) - that one must never unpair anything, it just
            // fails this attempt. See PeerTrustRejectedEventArgs and
            // PeerSignatureAuth.AuthenticateTrustedPeer.
            if (ex is HttpRequestException { StatusCode: HttpStatusCode.Forbidden })
                PeerTrustRejected?.Invoke(this, new PeerTrustRejectedEventArgs { Fingerprint = device.Fingerprint, Alias = device.Alias });

            return;
        }

        _logger.LogInformation("Playlist sync with {Alias}: fetched {RemoteCount} remote playlist(s), have {LocalCount} local",
            device.Alias, remotePlaylists.Count, _library.Playlists.Count);

        var baselines = _syncStateStore.LoadBaselines(device.Fingerprint);
        var decisions = PlaylistSyncPlanner.Plan(
            _library.Playlists,
            remotePlaylists,
            id => baselines.TryGetValue(id, out var v) ? v : null);

        var finalPlaylists = new List<Playlist>();
        var newBaselines = new Dictionary<Guid, DateTimeOffset>(baselines);

        foreach (var decision in decisions)
        {
            var name = decision.Local?.Name ?? decision.Remote?.Name ?? "?";
            _logger.LogInformation("Playlist sync with {Alias}: \"{Name}\" ({PlaylistId}) -> {Decision}",
                device.Alias, name, decision.PlaylistId, decision.Kind);

            // Deleted on one side (see PlaylistSyncPlanner.Delete) - drop it from
            // the merged result (and its baseline, since it no longer exists to
            // have one) rather than resolving it to some Playlist to keep.
            if (decision.Kind == PlaylistSyncDecisionKind.Delete)
            {
                newBaselines.Remove(decision.PlaylistId);
                continue;
            }

            var resolved = decision.Kind switch
            {
                PlaylistSyncDecisionKind.NoChange  => decision.Local!,
                PlaylistSyncDecisionKind.KeepLocal => decision.Local!,
                PlaylistSyncDecisionKind.AdoptRemote => PlaylistSyncMapper.ToPlaylist(decision.Remote!, _library.Tracks),
                PlaylistSyncDecisionKind.Conflict => await ResolveConflictAsync(decision, remoteDisplayName),
                _ => throw new ArgumentOutOfRangeException(),
            };

            finalPlaylists.Add(resolved);
            newBaselines[decision.PlaylistId] = resolved.UpdatedAt;
        }

        // Persisted by Library.PlaylistsChanged; see SyncHttpServer's twin.
        _library.ReplacePlaylists(finalPlaylists);
        await _syncStateStore.SaveBaselinesAsync(device.Fingerprint, newBaselines);

        try
        {
            var manifest = PlaylistSyncMapper.ToManifest(_deviceIdentity.Fingerprint, finalPlaylists);
            var bodyBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, FlowerJsonContext.Default.PlaylistSyncManifestDto));
            const string postPath = "/api/flower/v1/playlists/apply";
            using var content = new ByteArrayContent(bodyBytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            using var postRequest = new HttpRequestMessage(HttpMethod.Post, $"http://{device.EndPoint}{postPath}") { Content = content };
            AddSignedIdentityHeaders(postRequest, "POST", postPath, bodyBytes);
            using var postResponse = await Http.SendAsync(postRequest);
            postResponse.EnsureSuccessStatusCode();
            _logger.LogInformation("Playlist sync with {Alias}: pushed {Count} playlist(s) to their /apply successfully",
                device.Alias, finalPlaylists.Count);
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

    // See SyncHttpServer's trust gate - every gated endpoint requires these
    // (now including a signature proving possession of the private key behind
    // Fingerprint, not just the fingerprint string itself - see
    // DeviceSigningKey/SignatureVerifier) to evaluate trust. ConnectionClose
    // forces a fresh connection per request rather than pooling/reusing one -
    // sync sessions are now just a couple of requests each (see LibrarySyncService's
    // own history of this), so the extra handshake is negligible, and it avoids
    // HttpClient trying to reuse a keep-alive connection SyncHttpServer's
    // HttpListener (or the OS, e.g. after iOS backgrounds the app - see
    // SYNC-PLAN.md's foreground-only note) has already torn down - observed in
    // practice as "Connection reset by peer" / "Socket is not connected" on iOS.
    private void AddSignedIdentityHeaders(HttpRequestMessage request, string method, string path, byte[] body)
    {
        var (signature, timestamp, nonce) = _signingKey.Sign(method, path, [], body);
        request.Headers.Add("X-Flower-Fingerprint", _deviceIdentity.Fingerprint);
        request.Headers.Add("X-Flower-Alias", _deviceIdentity.Alias);
        request.Headers.Add("X-Flower-Role", _appSettings.IsServer ? "server" : "client");
        request.Headers.Add("X-Flower-Signature", signature);
        request.Headers.Add("X-Flower-Timestamp", timestamp);
        request.Headers.Add("X-Flower-Nonce", nonce);
        request.Headers.ConnectionClose = true;
    }

    private async Task<Playlist> ResolveConflictAsync(PlaylistSyncDecision decision, string remoteAlias)
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
            var survivor = decision.Local ?? PlaylistSyncMapper.ToPlaylist(decision.Remote!, _library.Tracks);
            _logger.LogInformation(
                "Playlist {Name}: deleted on {DeletedSide} but edited on the other side since they last agreed - keeping the edit rather than propagating the delete",
                survivor.Name, decision.Local == null ? "this device" : remoteAlias);
            return survivor;
        }

        var handler = ConflictDetected;
        if (handler == null)
            return decision.Local!; // No UI listening (e.g. sync running before the view attaches) - keep local rather than silently discarding it.

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
            ? decision.Local!
            : PlaylistSyncMapper.ToPlaylist(decision.Remote!, _library.Tracks);
    }
}
