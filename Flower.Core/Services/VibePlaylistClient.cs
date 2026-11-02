using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Flower.Models;

namespace Flower.Services;

// A preview's answer: songs, or why there are none.
public sealed record VibePreview(IReadOnlyList<Track> Tracks, VibeRefusal Refusal = VibeRefusal.None);

public enum VibeRefusal
{
    None,
    // The owner has Vibe Playlists off.
    Off,
    // Nothing is analysed yet.
    NotReady,
    // Asked too often; the next pause in typing tries again.
    Busy,
    // The server couldn't be reached, or answered nothing usable.
    Unreachable,
}

// Where a device asks for a Vibe Playlist's songs. A paired device and a
// browser tab ask their server (VibePlaylistClient); a device on its own will
// ask its own engine (phase 5).
public interface IVibePlaylists
{
    Task<VibePreview> PreviewAsync(string prompt, bool instrumental, CancellationToken ct = default);

    // More songs for an endless queue that has played most of its pool, never
    // one it already has. Empty when there are none or the server can't say.
    Task<IReadOnlyList<Track>> ExtendAsync(Playlist playlist, IEnumerable<Track> extensions, CancellationToken ct = default);
}

// The server's answers (VibeEndpoints), resolved against this device's library
// the way a synced playlist's songs are: by the server's id, then by tags. A
// song this device's catalog doesn't have yet is left out.
public sealed class VibePlaylistClient(
    HttpClient http, IPeerCredentials credentials, Func<Uri?> server, Library library, bool closesConnection,
    ILogger<VibePlaylistClient> logger) : IVibePlaylists
{
    public async Task<VibePreview> PreviewAsync(string prompt, bool instrumental, CancellationToken ct = default)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(
            new VibePreviewRequestDto(prompt, instrumental), VibePlaylistJsonContext.Default.VibePreviewRequestDto);
        var (songs, refusal) = await PostAsync(VibeRoutes.Preview, body, ct);
        return new VibePreview(songs, refusal);
    }

    public async Task<IReadOnlyList<Track>> ExtendAsync(Playlist playlist, IEnumerable<Track> extensions, CancellationToken ct = default)
    {
        var exclude = extensions.Select(t => t.OriginTrackId ?? t.Id.ToKey()).ToList();
        var body = JsonSerializer.SerializeToUtf8Bytes(
            new VibeExtendRequestDto(exclude), VibePlaylistJsonContext.Default.VibeExtendRequestDto);
        var (songs, _) = await PostAsync(VibeRoutes.Extend(playlist.Id), body, ct);
        return songs;
    }

    private async Task<(IReadOnlyList<Track> Songs, VibeRefusal Refusal)> PostAsync(string path, byte[] body, CancellationToken ct)
    {
        if (server() is not { } origin)
            return ([], VibeRefusal.Unreachable);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(origin, path));
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            await request.AddPeerCredentialsAsync(credentials, body);
            if (closesConnection)
                request.Headers.ConnectionClose = true;

            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return ([], VibeRefusal.Busy);
            if (!response.IsSuccessStatusCode)
                return ([], await RefusalOf(response, ct));

            var answer = await JsonSerializer.DeserializeAsync(
                await response.Content.ReadAsStreamAsync(ct), VibePlaylistJsonContext.Default.VibeSongsDto, ct);
            if (answer is null)
                return ([], VibeRefusal.Unreachable);

            return (new PlaylistTrackIndex(library.Tracks).Resolve(answer.Tracks, out _), VibeRefusal.None);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Could not ask the server for Vibe Playlist songs");
            return ([], VibeRefusal.Unreachable);
        }
    }

    private static async Task<VibeRefusal> RefusalOf(HttpResponseMessage response, CancellationToken ct)
    {
        var problem = FlowerProblem.TryRead(await response.Content.ReadAsStringAsync(ct));
        return problem?.Code switch
        {
            ProblemCodes.VibeOff => VibeRefusal.Off,
            ProblemCodes.VibeNotReady => VibeRefusal.NotReady,
            _ => VibeRefusal.Unreachable,
        };
    }
}
