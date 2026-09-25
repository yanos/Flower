using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

using Flower.Models;
using Flower.Services;

namespace Flower.Tests.TestSupport;

// A Flower.Server's sync surface over a real socket, backed by a real Library
// and answering through the same code the server's SyncEndpoints does -
// LibraryDtoMapper for the catalog, PlaylistSyncMapper.ApplyPushedManifest for
// a pushed playlist set, Library.MergeReportedTrackState for track state. What
// it leaves out is the trust boundary (no signature is checked), which
// Flower.Server.Tests covers against the real host; what it adds is the one
// thing that host cannot give a client test, which is a port LibrarySyncService
// and PlaylistSyncService can actually dial.
//
// Everything a scenario might want to do to the server between two syncs -
// retag, delete, rescan, edit a playlist - is done to Library directly.
public sealed class SimulatedFlowerServer : IDisposable
{
    public const string LibraryPath = "/api/flower/v1/library";
    public const string PlaylistsPath = "/api/flower/v1/playlists";
    public const string ApplyPath = "/api/flower/v1/playlists/apply";
    public const string TrackStatePath = "/api/flower/v1/track-state";
    public const string RemoveFromLibraryPath = "/api/admin/library/remove";

    // The admin surface answers camelCase (AdminEndpoints' own options).
    private static readonly JsonSerializerOptions AdminJson = new(JsonSerializerDefaults.Web);

    // False makes the server unreachable for admin calls - a 503, the way a
    // proxy in front of a server that is down answers.
    public bool AdminReachable { get; set; } = true;

    // SyncEndpoints' own options: PascalCase, nulls omitted, read case-insensitively.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    private readonly FakePeerHttpServer _http;

    public Library Library { get; }
    public string Fingerprint { get; }

    // Whether the device dialling in is one the server made an admin - what
    // TrustedPeerStore.IsAdmin answers on the real host.
    public bool CallerIsAdmin { get; set; } = true;

    // Runs after GET /playlists has been answered and before the response is
    // closed, once - the window in which another client's push can land
    // between this client's read and its write.
    public Action? AfterNextPlaylistsGet { get; set; }

    public List<string> Requests { get; } = new();
    public int PlaylistApplyCount { get; private set; }

    public SimulatedFlowerServer(IEnumerable<Track> tracks, string fingerprint = "server-fp")
    {
        Library = new Library(tracks.ToList());
        Fingerprint = fingerprint;
        _http = new FakePeerHttpServer(HandleAsync);
    }

    public DiscoveredDevice Device => new()
    {
        InstanceName = "server",
        BaseUri = NetworkDiscoveryService.HttpOrigin(new IPEndPoint(IPAddress.Loopback, _http.Port)),
        Fingerprint = Fingerprint,
        WeAreAdmin = CallerIsAdmin,
        Alias = "Server",
    };

    private async Task HandleAsync(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? "";
        var method = context.Request.HttpMethod;
        lock (Requests)
            Requests.Add($"{method} {path}");

        try
        {
            switch (method, path)
            {
                case ("GET", LibraryPath):
                    await ServeLibraryAsync(context);
                    return;
                case ("GET", PlaylistsPath):
                    await WriteJsonAsync(context, PlaylistSyncMapper.ToManifest(Fingerprint, Library.Playlists));
                    var after = AfterNextPlaylistsGet;
                    AfterNextPlaylistsGet = null;
                    after?.Invoke();
                    return;
                case ("POST", ApplyPath):
                {
                    var manifest = await ReadJsonAsync<PlaylistSyncManifestDto>(context);
                    PlaylistSyncMapper.ApplyPushedManifest(Library, manifest!);
                    PlaylistApplyCount++;
                    context.Response.StatusCode = 204;
                    return;
                }
                case ("POST", TrackStatePath):
                {
                    var report = await ReadJsonAsync<TrackStateReportDto>(context);
                    var fingerprint = context.Request.Headers["X-Flower-Fingerprint"] ?? "";
                    Library.MergeReportedTrackState(fingerprint, report!.Tracks, CallerIsAdmin);
                    context.Response.Headers[TrackStateReportHeaders.LibraryToken] = Library.ChangeToken;
                    context.Response.StatusCode = 204;
                    return;
                }
                case ("POST", RemoveFromLibraryPath):
                {
                    if (!AdminReachable)
                    {
                        context.Response.StatusCode = 503;
                        return;
                    }
                    if (!CallerIsAdmin)
                    {
                        context.Response.StatusCode = 403;
                        return;
                    }

                    using var reader = new StreamReader(context.Request.InputStream);
                    var request = JsonSerializer.Deserialize<LibraryRemovalRequestDto>(await reader.ReadToEndAsync(), AdminJson)!;
                    var tracks = request.TrackIds.Select(Library.Find).OfType<Track>().ToList();
                    var result = LibraryRemoval.Remove(Library, tracks, request.DeleteFiles,
                        Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(
                        new LibraryRemovalResponseDto(result.Removed, result.FilesTrashed, result.FilesDeleted, result.FilesNotDeleted.Count), AdminJson);
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes);
                    return;
                }
                default:
                    context.Response.StatusCode = 404;
                    return;
            }
        }
        finally
        {
            context.Response.Close();
        }
    }

    // SyncEndpoints.GetLibrary: only tracks with a file here, the token as the ETag.
    private async Task ServeLibraryAsync(HttpListenerContext context)
    {
        var token = Library.ChangeToken;
        context.Response.Headers["ETag"] = token;
        if (context.Request.Headers["If-None-Match"] == token)
        {
            context.Response.StatusCode = 304;
            return;
        }

        var songs = Library.Snapshot.Albums
            .SelectMany(album => album.Tracks)
            .Where(track => track.Path != null)
            .Select(track => LibraryDtoMapper.ToTrackDto(track, Fingerprint))
            .ToList();
        await WriteJsonAsync(context, new LibrarySyncManifestDto(Fingerprint, songs));
    }

    private static async Task WriteJsonAsync<T>(HttpListenerContext context, T value)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions));
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream);
        return JsonSerializer.Deserialize<T>(await reader.ReadToEndAsync(), JsonOptions);
    }

    public void Dispose() => _http.Dispose();
}
