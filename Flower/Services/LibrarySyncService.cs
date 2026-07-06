using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

using Flower.Models;

namespace Flower.Services;

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
// Unlike PlaylistSyncService, both sides of a discovered pair run this
// independently rather than electing one initiator: there's no write-back to
// the peer here, just a local, additive merge, so there's no risk of two
// conflicting writes racing - and both sides genuinely need to learn about the
// other's exclusive tracks, which a one-sided pull would miss entirely.
public class LibrarySyncService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Library _library;
    private readonly string _ownFingerprint;
    private readonly string _ownAlias;

    public LibrarySyncService(Library library, string ownFingerprint, string ownAlias)
    {
        _library = library;
        _ownFingerprint = ownFingerprint;
        _ownAlias = ownAlias;
    }

    public async Task SyncWithAsync(DiscoveredDevice device)
    {
        if (string.IsNullOrEmpty(device.Fingerprint))
            return;

        List<Child> songs;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://{device.EndPoint}/api/flower/v1/library");
            request.Headers.Add("X-Flower-Fingerprint", _ownFingerprint);
            request.Headers.Add("X-Flower-Alias", _ownAlias);
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
        catch
        {
            return; // Peer unreachable, not running this endpoint yet, or not (yet) trusted.
        }

        var placeholders = songs
            .Select(song => LibrarySyncMapper.ToPlaceholderTrack(song, device.Fingerprint))
            .ToList();

        if (placeholders.Count > 0)
            _library.MergeSyncedTracks(placeholders);
    }
}
