using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Flower.Logging;
using Flower.Models;
using Flower.Persistence;

namespace Flower.Services;

// Outcome of one SyncWithAsync call - lets a user-initiated caller (see
// MainViewModel.ForceSyncNow) report something more useful than silence when
// nothing visibly changes: "reached the peer but already up to date" and
// "couldn't reach the peer at all" both merge zero new tracks, but they're
// very different things to tell the user.
public readonly record struct LibrarySyncResult(bool Success, int FetchedCount, int AddedCount);

// Pulls a peer's full track catalog in one request (GET /api/flower/v1/library
// - see LibrarySyncContracts) and merges anything this device doesn't already
// have as Path == null placeholders - see SYNC-PLAN.md Phase 3. Talks to the
// peer's embedded SyncHttpServer host directly (same plain-HTTP identity
// headers as PlaylistSyncService, not real OpenSubsonic credentials - see
// SyncHttpServer.AuthorizeAsync) rather than through OpenSubsonicClient: an
// earlier version used the OpenSubsonic-shaped getAlbumList2/getAlbum pair,
// one request per album, which for a library of hundreds/thousands of albums
// meant hundreds/thousands of individual connections in a burst - observed in
// practice as heavy iOS nw_connection log churn. OpenSubsonicClient itself is
// unaffected and still used for the OpenSubsonic-shaped endpoints (stream/
// download, and real third-party server support later).
//
// Originally both sides of a discovered pair ran this independently rather
// than electing one initiator - there's no write-back to the peer here, just
// a local, additive merge, so there was no risk of two conflicting writes
// racing, and in the old mesh model both sides genuinely needed to learn
// about the other's exclusive tracks. Under Client/Server roles (see
// SyncRolePolicy) this method is only ever called by a Client pulling from
// its one paired Server - a Server's own trigger paths (MainViewModel) are
// gated off entirely, so it never calls this at all, making the pull
// effectively one-directional (client-pulls-from-server) without needing any
// change to this method itself.
public class LibrarySyncService
{
    // A real library's manifest can run into the tens of thousands of songs
    // (observed: 16k+ tracks) - PlaylistSyncService's 10s timeout is fine for
    // its much smaller payload, but this one needs enough headroom for a much
    // bigger JSON response over a possibly-imperfect WiFi link without silently
    // timing out and aborting the whole sync (see the catch below).
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Library _library;
    private readonly DeviceIdentity _deviceIdentity;
    private readonly AppSettings _appSettings;
    private readonly LibraryStore _libraryStore;
    private readonly InMemoryLogStore _logStore;
    private readonly ILogger _logger;

    public LibrarySyncService(Library library, DeviceIdentity deviceIdentity, AppSettings appSettings, LibraryStore libraryStore, InMemoryLogStore logStore, ILogger<LibrarySyncService> logger)
    {
        _library = library;
        _deviceIdentity = deviceIdentity;
        _appSettings = appSettings;
        _libraryStore = libraryStore;
        _logStore = logStore;
        _logger = logger;
    }

    public async Task<LibrarySyncResult> SyncWithAsync(DiscoveredDevice device)
    {
        if (string.IsNullOrEmpty(device.Fingerprint))
        {
            _logger.LogDebug("Library sync skipped for {Alias}: no resolved fingerprint yet", device.Alias);
            return new LibrarySyncResult(false, 0, 0);
        }

        _logger.LogInformation("Library sync starting with {Alias} ({Fingerprint}) at {EndPoint}",
            device.Alias, device.Fingerprint, device.EndPoint);

        List<Child> songs;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://{device.EndPoint}/api/flower/v1/library");
            request.Headers.Add("X-Flower-Fingerprint", _deviceIdentity.Fingerprint);
            request.Headers.Add("X-Flower-Alias", _deviceIdentity.Alias);
            request.Headers.Add("X-Flower-Role", _appSettings.IsServer ? "server" : "client");
            // Fresh connection per request rather than pooling one - see
            // PlaylistSyncService.AddIdentityHeaders for why (avoids reusing a
            // keep-alive connection the server/OS already tore down).
            request.Headers.ConnectionClose = true;

            using var response = await Http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            var manifest = JsonSerializer.Deserialize<LibrarySyncManifestDto>(json, JsonOptions);
            songs = manifest?.Songs ?? [];
        }
        catch (Exception ex)
        {
            // Peer unreachable, not running this endpoint yet, or not (yet) trusted.
            _logger.LogWarning(ex, "Library sync with {Alias} ({Fingerprint}): GET /library failed, aborting this sync attempt",
                device.Alias, device.Fingerprint);
            return new LibrarySyncResult(false, 0, 0);
        }

        var placeholders = songs
            .Select(song => LibrarySyncMapper.ToPlaceholderTrack(song, device.Fingerprint, _deviceIdentity.Fingerprint))
            .ToList();

        _logger.LogInformation("Library sync with {Alias}: fetched {SongCount} song(s) from their catalog", device.Alias, songs.Count);

        // No early-return for an empty catalog: a peer reporting zero songs
        // (its whole library emptied, or a fresh pairing to one with nothing
        // yet) must still prune every not-yet-downloaded placeholder this
        // device previously learned from it - see Library.MergeSyncedTracks.
        var beforeCount = _library.Tracks.Count;
        var removedCount = _library.MergeSyncedTracks(device.Fingerprint, placeholders);
        var addedCount = _library.Tracks.Count - beforeCount + removedCount;
        _logger.LogInformation("Library sync with {Alias}: merged catalog, {AddedCount} new placeholder(s) added, {RemovedCount} stale placeholder(s) pruned ({TotalBefore} -> {TotalAfter})",
            device.Alias, addedCount, removedCount, beforeCount, _library.Tracks.Count);

        // Without this, a merge only lives in memory - a killed/relaunched app
        // (mobile has no always-on background process) would lose every
        // not-yet-downloaded placeholder learned this way until the next
        // successful sync. PlaylistSyncService and LibraryDownloadService both
        // already persist after their own mutations; this one previously did not.
        await _libraryStore.SaveAsync(_library.Tracks);

        // Piggybacks the Log window's remote-log feature on this exact sync
        // session, so it fires "at the same time as the library" with no
        // extra caller-side wiring - see docs plan / LogViewModel's own doc
        // comment. Defense-in-depth, not reliance on the caller's own gating
        // (see SyncRolePolicy's doc comment above): a Server must never push
        // logs to anything, only a Client pushes its own snapshot to its one
        // paired Server.
        if (!_appSettings.IsServer)
            await PushLogSnapshotAsync(device);

        return new LibrarySyncResult(true, songs.Count, addedCount);
    }

    private async Task PushLogSnapshotAsync(DiscoveredDevice device)
    {
        try
        {
            var entries = _logStore.Snapshot().Select(LogEntryDto.FromEntry).ToList();
            var report = new LogReportDto(_deviceIdentity.Fingerprint, _deviceIdentity.Alias, DateTimeOffset.UtcNow, entries);

            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://{device.EndPoint}/api/flower/v1/log/report");
            request.Headers.Add("X-Flower-Fingerprint", _deviceIdentity.Fingerprint);
            request.Headers.Add("X-Flower-Alias", _deviceIdentity.Alias);
            request.Headers.Add("X-Flower-Role", _appSettings.IsServer ? "server" : "client");
            request.Headers.ConnectionClose = true;
            request.Content = new StringContent(JsonSerializer.Serialize(report, JsonOptions), Encoding.UTF8, "application/json");

            using var response = await Http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            _logger.LogDebug("Pushed {Count} log line(s) to paired server {Alias}", entries.Count, device.Alias);
        }
        catch (Exception ex)
        {
            // Not fatal to the library sync itself - the library merge above
            // already succeeded and saved; the log snapshot just converges
            // next cycle.
            _logger.LogDebug(ex, "Could not push log snapshot to {Alias} - not fatal to this sync", device.Alias);
        }
    }
}
