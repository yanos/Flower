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
    public const string UploadsPath = "/api/admin/library/uploads";
    public const string MovePath = "/api/admin/library/move";
    public const string TagsPath = "/api/admin/library/tags";
    public const string ArtworkPath = "/api/admin/library/artwork";
    public const string CoverArtPath = "/api/flower/v1/cover-art";
    public const string DownloadPath = "/api/flower/v1/download";

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

    // Where this server keeps its music, when a test gives it somewhere - the
    // folder an upload lands in (LibraryIngest) and the one RelativePath is
    // relative to. Null leaves the server with paths that name no real file,
    // which is all most scenarios need.
    public string? MusicFolder { get; }

    private readonly LibraryIngest? _ingest;

    public SimulatedFlowerServer(IEnumerable<Track> tracks, string fingerprint = "server-fp", string? musicFolder = null)
    {
        Library = new Library(tracks.ToList());
        Fingerprint = fingerprint;
        MusicFolder = musicFolder;
        if (musicFolder != null)
        {
            Directory.CreateDirectory(musicFolder);
            _ingest = new LibraryIngest(
                Library, () => [musicFolder], musicFolder + "-staging",
                new Flower.Importer.Importer(Microsoft.Extensions.Logging.Abstractions.NullLogger<Flower.Importer.Importer>.Instance),
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        }

        _http = new FakePeerHttpServer(HandleAsync);
    }

    // AdminEndpoints.ToResult: the outcome as the status a device acts on.
    private static async Task WriteIngestResultAsync(HttpListenerContext context, IngestResult result)
    {
        context.Response.StatusCode = result.Outcome switch
        {
            IngestOutcome.Accepted or IngestOutcome.Completed => 200,
            IngestOutcome.Conflict => 409,
            IngestOutcome.Unavailable => 503,
            IngestOutcome.UnknownUpload => 404,
            IngestOutcome.Corrupt => 422,
            _ => 400,
        };
        var bytes = result.Moved != null
            ? JsonSerializer.SerializeToUtf8Bytes(result.Moved, AdminJson)
            : result.Status != null
                ? JsonSerializer.SerializeToUtf8Bytes(result.Status, AdminJson)
                : JsonSerializer.SerializeToUtf8Bytes(new { error = result.Error }, AdminJson);
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
    }

    // The admin filter's two refusals, for the upload routes. True when the
    // request may go on.
    private bool AdmitsAdmin(HttpListenerContext context)
    {
        if (!AdminReachable || _ingest == null)
        {
            context.Response.StatusCode = 503;
            return false;
        }
        if (!CallerIsAdmin)
        {
            context.Response.StatusCode = 403;
            return false;
        }

        return true;
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
                case ("POST", UploadsPath):
                {
                    if (!AdmitsAdmin(context))
                        return;

                    using var reader = new StreamReader(context.Request.InputStream);
                    var request = JsonSerializer.Deserialize<LibraryUploadRequestDto>(await reader.ReadToEndAsync(), AdminJson)!;
                    await WriteIngestResultAsync(context, await _ingest!.BeginAsync(request));
                    return;
                }
                case ("POST", MovePath):
                {
                    if (!AdmitsAdmin(context))
                        return;

                    using var reader = new StreamReader(context.Request.InputStream);
                    var request = JsonSerializer.Deserialize<LibraryMoveRequestDto>(await reader.ReadToEndAsync(), AdminJson)!;
                    await WriteIngestResultAsync(context, await _ingest!.MoveAsync(request));
                    return;
                }
                case ("PUT" or "DELETE", ArtworkPath):
                {
                    if (!AdmitsAdmin(context))
                        return;

                    using var body = new MemoryStream();
                    await context.Request.InputStream.CopyToAsync(body);
                    var ids = context.Request.QueryString["ids"]!.Split(',').ToList();
                    var at = DateTimeOffset.Parse(context.Request.QueryString["editedAt"]!,
                        System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
                    var art = method == "PUT"
                        ? new LocalAlbumArt(body.ToArray(), LocalAlbumArtReader.MimeTypeForBytes(body.ToArray()) ?? "image/png")
                        : null;
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(
                        TrackArtwork.ApplyEdit(Library, ids, at, art, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance), AdminJson);
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes);
                    return;
                }
                // MediaEndpoints.GetCoverArt, for a song id: the picture in
                // that song's file, or 404.
                case ("GET", CoverArtPath):
                {
                    var art = Library.Find(context.Request.QueryString["id"]) is { Path: { } file }
                        ? LocalAlbumArtReader.ForFile(file)
                        : null;
                    if (art == null)
                    {
                        context.Response.StatusCode = 404;
                        return;
                    }

                    context.Response.ContentType = art.MimeType;
                    context.Response.ContentLength64 = art.Bytes.Length;
                    await context.Response.OutputStream.WriteAsync(art.Bytes);
                    return;
                }
                // MediaEndpoints.Download: the song's file, whole.
                case ("GET", DownloadPath):
                {
                    if (Library.Find(context.Request.QueryString["id"]) is not { Path: { } file } || !File.Exists(file))
                    {
                        context.Response.StatusCode = 404;
                        return;
                    }

                    var bytes = await File.ReadAllBytesAsync(file);
                    context.Response.ContentType = "application/octet-stream";
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes);
                    return;
                }
                case ("POST", TagsPath):
                {
                    if (!AdmitsAdmin(context))
                        return;

                    using var reader = new StreamReader(context.Request.InputStream);
                    var request = JsonSerializer.Deserialize<LibraryTagEditsRequestDto>(await reader.ReadToEndAsync(), AdminJson)!;
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(
                        TrackTags.ApplyEdits(Library, request.Edits, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance), AdminJson);
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes);
                    return;
                }
                case ("PUT", _) when path.StartsWith(UploadsPath + "/", StringComparison.Ordinal):
                {
                    if (!AdmitsAdmin(context))
                        return;

                    using var body = new MemoryStream();
                    await context.Request.InputStream.CopyToAsync(body);
                    var uploadId = Uri.UnescapeDataString(path[(UploadsPath.Length + 1)..]);
                    var offset = long.Parse(context.Request.QueryString["offset"]!);
                    await WriteIngestResultAsync(context, await _ingest!.AppendAsync(uploadId, offset, body.ToArray()));
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
            .Select(track => LibraryDtoMapper.ToTrackDto(track, Fingerprint, MusicFolder == null ? null : [MusicFolder]))
            .ToList();
        var removed = Library.RemovedTracks.Where(r => r.Deliberate).Select(r => r.Track.Id.ToKey()).ToList();
        await WriteJsonAsync(context, new LibrarySyncManifestDto(Fingerprint, songs, removed));
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
