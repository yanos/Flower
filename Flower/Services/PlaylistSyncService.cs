using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using Flower.Models;
using Flower.Persistence;

namespace Flower.Services;

public enum PlaylistConflictChoice { KeepLocal, KeepRemote }

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
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Library _library;
    private readonly DeviceIdentity _deviceIdentity;
    private readonly PlaylistStore _playlistStore = new();
    private readonly PlaylistSyncStateStore _syncStateStore = new();

    public event EventHandler<PlaylistConflictEventArgs>? ConflictDetected;

    public PlaylistSyncService(Library library, DeviceIdentity deviceIdentity)
    {
        _library = library;
        _deviceIdentity = deviceIdentity;
    }

    public async Task SyncWithAsync(DiscoveredDevice device)
    {
        if (string.IsNullOrEmpty(device.Fingerprint))
            return;

        // Exactly one side of a discovery pair initiates a sync session - the
        // other just waits to receive the initiator's /apply push once it's done.
        // Ordinal comparison is arbitrary but deterministic and identical on both
        // devices (each compares its own fingerprint against the other's), so a
        // pair never both initiate (double conflict prompts, racing writes) or
        // both stay silent.
        if (string.CompareOrdinal(_deviceIdentity.Fingerprint, device.Fingerprint) >= 0)
            return;

        // A local nickname (see DeviceNicknameStore - the same override the
        // sidebar's "Rename Device" and Trusted Devices window use) wins over
        // the peer's own raw self-reported alias here too, so the conflict
        // dialog's "Keep X's Version" matches what this device is actually
        // called elsewhere in the UI.
        var remoteDisplayName = new DeviceNicknameStore().Get(device.Fingerprint) ?? device.Alias;

        List<PlaylistSyncPlaylistDto> remotePlaylists;
        try
        {
            using var getRequest = new HttpRequestMessage(HttpMethod.Get, $"http://{device.EndPoint}/api/flower/v1/playlists");
            AddIdentityHeaders(getRequest);
            using var getResponse = await Http.SendAsync(getRequest);
            getResponse.EnsureSuccessStatusCode(); // Throws on a 403 from an unapproved trust gate - handled below like any other unreachable peer.
            var json = await getResponse.Content.ReadAsStringAsync();
            var manifest = JsonSerializer.Deserialize<PlaylistSyncManifestDto>(json, JsonOptions);
            remotePlaylists = manifest?.Playlists ?? new List<PlaylistSyncPlaylistDto>();
        }
        catch
        {
            return; // Peer unreachable, not running this endpoint yet, or not (yet) trusted.
        }

        var baselines = _syncStateStore.LoadBaselines(device.Fingerprint);
        var decisions = PlaylistSyncPlanner.Plan(
            _library.Playlists,
            remotePlaylists,
            id => baselines.TryGetValue(id, out var v) ? v : null);

        var finalPlaylists = new List<Playlist>();
        var newBaselines = new Dictionary<Guid, DateTimeOffset>(baselines);

        foreach (var decision in decisions)
        {
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

        _library.ReplacePlaylists(finalPlaylists);
        await _playlistStore.SaveAsync(finalPlaylists);
        await _syncStateStore.SaveBaselinesAsync(device.Fingerprint, newBaselines);

        try
        {
            var manifest = PlaylistSyncMapper.ToManifest(_deviceIdentity.Fingerprint, finalPlaylists);
            var body = JsonSerializer.Serialize(manifest, JsonOptions);
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var postRequest = new HttpRequestMessage(HttpMethod.Post, $"http://{device.EndPoint}/api/flower/v1/playlists/apply") { Content = content };
            AddIdentityHeaders(postRequest);
            await Http.SendAsync(postRequest);
        }
        catch
        {
            // Peer went away mid-session, or hasn't approved us via the trust gate
            // yet - our own state is already fully merged and saved either way; it
            // converges next time these two devices are both up (and trusted).
        }
    }

    // See SyncHttpServer.AuthorizeAsync - every /api/flower/v1/* endpoint requires
    // these to evaluate (and, on first contact, prompt for) trust. ConnectionClose
    // forces a fresh connection per request rather than pooling/reusing one -
    // sync sessions are now just a couple of requests each (see LibrarySyncService's
    // own history of this), so the extra handshake is negligible, and it avoids
    // HttpClient trying to reuse a keep-alive connection SyncHttpServer's
    // HttpListener (or the OS, e.g. after iOS backgrounds the app - see
    // SYNC-PLAN.md's foreground-only note) has already torn down - observed in
    // practice as "Connection reset by peer" / "Socket is not connected" on iOS.
    private void AddIdentityHeaders(HttpRequestMessage request)
    {
        request.Headers.Add("X-Flower-Fingerprint", _deviceIdentity.Fingerprint);
        request.Headers.Add("X-Flower-Alias", _deviceIdentity.Alias);
        request.Headers.ConnectionClose = true;
    }

    private async Task<Playlist> ResolveConflictAsync(PlaylistSyncDecision decision, string remoteAlias)
    {
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
        return choice == PlaylistConflictChoice.KeepLocal
            ? decision.Local!
            : PlaylistSyncMapper.ToPlaylist(decision.Remote!, _library.Tracks);
    }
}
