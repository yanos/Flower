using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Flower.Models;
using Flower.Services;

namespace Flower.Importer;

// The browser head's playlists: the origin server's, read once per rescan
// alongside its catalog.
//
// Read-only, and one-way on purpose. PlaylistSyncService - the peer-to-peer
// path a desktop uses - is a negotiation: the two sides elect an initiator,
// compare UpdatedAt, raise conflicts for a human to resolve, and push the
// merged result back. None of that fits a tab. A tab has no durable identity
// to be a party to a merge (its whole database lives in a sandbox that a
// refresh may reset), no second device to elect against, and nothing of its
// own to contribute - its playlists *are* the server's. So this reads them and
// stops, which is honest about what a tab is rather than pretending it is a
// third peer.
//
// Read-only here does not mean a tab's edits are stranded: they go back by the
// other road, IPlaylistWriter, which POSTs the whole set to /playlists/apply.
// That is still not a merge - it is the same "a tab's playlists are the
// server's" premise read in the other direction. The one coupling between the
// two: this path's ReplacePlaylists raises the same event a user's own edit
// does, so the writer is told what was fetched before it is installed, or every
// rescan would push the server's own playlists straight back at it.
public sealed class OriginPlaylistImporter(
    HttpClient http,
    string baseUrl,
    IPeerCredentials credentials,
    ILogger<OriginPlaylistImporter> logger) : IPlaylistImporter
{
    public const string PlaylistsPath = "/api/flower/v1/playlists";

    public async Task<List<Playlist>> ImportAsync(IReadOnlyList<Track> library)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}{PlaylistsPath}");
        await request.AddPeerCredentialsAsync(credentials);

        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var manifest = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            PlaylistSyncJsonContext.Default.PlaylistSyncManifestDto);

        var playlists = new List<Playlist>();
        foreach (var dto in manifest?.Playlists ?? [])
            playlists.Add(PlaylistSyncMapper.ToPlaylist(dto, library));

        logger.LogInformation("Origin server at {BaseUrl}: fetched {PlaylistCount} playlist(s)", baseUrl, playlists.Count);
        return playlists;
    }
}
