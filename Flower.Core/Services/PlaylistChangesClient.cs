using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Flower.Services;

// POST /api/flower/v1/playlists/changes, signed: the one write a device makes
// to its server's playlists, whether a sync session sends it
// (PlaylistSyncService) or a browser tab does (OriginPlaylistWriter). What to
// send, and what to make of the answer, are each caller's own.
public static class PlaylistChangesClient
{
    public const string Path = "/api/flower/v1/playlists/changes";

    // Throws for a refusal, as for no answer at all. closeConnection asks for
    // a fresh connection, for a caller whose kept-alive one may have been
    // torn down under it (a phone back from the background).
    public static async Task<PlaylistChangesResponseDto?> PostAsync(
        HttpClient http, Uri url, IPeerCredentials credentials, IReadOnlyList<PlaylistChangeDto> changes,
        bool closeConnection = false)
    {
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new PlaylistChangesRequestDto([.. changes]), PlaylistSyncJsonContext.Default.PlaylistChangesRequestDto));

        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        await request.AddPeerCredentialsAsync(credentials, body);
        if (closeConnection)
            request.Headers.ConnectionClose = true;

        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(), PlaylistSyncJsonContext.Default.PlaylistChangesResponseDto);
    }
}
