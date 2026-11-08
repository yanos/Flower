using System.Net;
using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using Flower.Models;
using Flower.Persistence;
using Flower.Services;

using Flower.Server.Tests.TestSupport;

namespace Flower.Server.Tests.Endpoints;

// GET /cover-art's ETag and both art routes' thumbnail size
// (docs/CITED-DECISIONS.md, October C2). In a class of its own because it gives
// the fixture's library a song with a real cover on disk, and a class fixture
// is shared by every test in its class.
public sealed class CoverArtEndpointTests(FlowerServerFixture server) : IClassFixture<FlowerServerFixture>, IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "flower-cover-route-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    private Library Library => server.Services.GetRequiredService<Library>();

    // A song beside a cover.jpg of this size, added to whatever the library
    // already holds. The song is not audio, so the folder's cover is its art.
    private Track SongWithCover(int width, int height)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllBytes(Path.Combine(_folder, "cover.jpg"), TestCovers.Jpeg(width, height));
        var path = Path.Combine(_folder, "01 Debaser.mp3");
        File.WriteAllBytes(path, [1, 2, 3]);

        var song = new Track { Path = path, Title = "Debaser", Album = "Doolittle " + Guid.NewGuid().ToString("N"), AlbumArtists = "Pixies", Artists = "Pixies" };
        Library.UpdateTracks([.. Library.Tracks.Where(t => t.Path != null).Select(Copy), song]);
        return Library.Tracks.Single(t => t.Path == path);
    }

    private static Track Copy(Track t) => new()
    {
        Path = t.Path, Title = t.Title, Artists = t.Artists, AlbumArtists = t.AlbumArtists,
        Album = t.Album, Year = t.Year, Genre = t.Genre, Duration = t.Duration, DateAdded = t.DateAdded,
    };

    private async Task<DeviceSigningKey> DeviceAsync()
    {
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var q = ecdsa.ExportParameters(false).Q;
        var device = new DeviceSigningKey(ecdsa, [0x04, .. q.X!, .. q.Y!]);
        await server.Services.GetRequiredService<TrustedPeerStore>()
            .ApproveAsync(device.Fingerprint, "Kitchen iPad", device.PublicKeyBase64, isAdmin: false);
        return device;
    }

    private async Task<HttpContext> SendAsync(
        DeviceSigningKey device, string method, string path, (string Key, string Value)[] query,
        string? body = null, string? ifNoneMatch = null)
    {
        var bodyBytes = body == null ? [] : Encoding.UTF8.GetBytes(body);
        var (signature, timestamp, nonce) = device.Sign(method, path, query, bodyBytes);

        return await server.Server.SendAsync(c =>
        {
            c.Request.Method = method;
            c.Request.Path = path;
            c.Request.QueryString = QueryString.Create(query.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)));
            c.Connection.RemoteIpAddress = IPAddress.Parse("10.0.3.1");
            c.Request.Headers["X-Flower-Fingerprint"] = device.Fingerprint;
            c.Request.Headers["X-Flower-Alias"] = "Kitchen iPad";
            c.Request.Headers["X-Flower-Role"] = "client";
            c.Request.Headers["X-Flower-Signature"] = signature;
            c.Request.Headers["X-Flower-Timestamp"] = timestamp;
            c.Request.Headers["X-Flower-Nonce"] = nonce;
            if (ifNoneMatch != null)
                c.Request.Headers.IfNoneMatch = ifNoneMatch;
            if (body != null)
            {
                c.Request.ContentType = "application/json";
                c.Request.Body = new MemoryStream(bodyBytes);
                c.Request.ContentLength = bodyBytes.Length;
            }
        });
    }

    private static async Task<byte[]> BodyOf(HttpContext context)
    {
        using var buffer = new MemoryStream();
        await context.Response.Body.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    // A browser tab keeps what it was sent and asks again with the tag; the
    // same picture is a 304 and no body, rather than the whole cover again.
    [Fact]
    public async Task GET_cover_art_answers_a_tag_it_already_sent_with_304()
    {
        var song = SongWithCover(600, 600);
        using var device = await DeviceAsync();
        (string, string)[] query = [("id", song.Id.ToKey())];

        var first = await SendAsync(device, "GET", "/api/flower/v1/cover-art", query);
        var etag = first.Response.Headers.ETag.ToString();

        Assert.Equal(StatusCodes.Status200OK, first.Response.StatusCode);
        Assert.False(string.IsNullOrEmpty(etag));
        Assert.Contains("no-cache", first.Response.Headers.CacheControl.ToString());

        var again = await SendAsync(device, "GET", "/api/flower/v1/cover-art", query, ifNoneMatch: etag);

        Assert.Equal(StatusCodes.Status304NotModified, again.Response.StatusCode);
        Assert.Empty(await BodyOf(again));
    }

    [Fact]
    public async Task GET_cover_art_at_thumbnail_size_sends_the_cover_shrunk()
    {
        var song = SongWithCover(1400, 1400);
        using var device = await DeviceAsync();

        var context = await SendAsync(device, "GET", "/api/flower/v1/cover-art",
            [("id", song.Id.ToKey()), ("size", CoverArtBatch.ThumbnailSize)]);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(CoverArtBatch.ThumbnailPixels, TestCovers.SizeOf(await BodyOf(context)).Width);
    }

    // A named size, not a number: each size is a copy of every cover held in
    // memory, so a size the server has no name for is refused, not guessed at.
    [Fact]
    public async Task Both_art_routes_refuse_a_size_they_do_not_know()
    {
        var song = SongWithCover(300, 300);
        using var device = await DeviceAsync();

        var single = await SendAsync(device, "GET", "/api/flower/v1/cover-art",
            [("id", song.Id.ToKey()), ("size", "640")]);
        var batch = await SendAsync(device, "POST", "/api/flower/v1/cover-art/batch", [],
            body: """{"Ids":["al-one"],"Size":"640"}""");

        Assert.Equal(StatusCodes.Status400BadRequest, single.Response.StatusCode);
        Assert.Equal(StatusCodes.Status400BadRequest, batch.Response.StatusCode);
    }

    [Fact]
    public async Task A_batch_at_thumbnail_size_sends_every_cover_shrunk()
    {
        var song = SongWithCover(1000, 1000);
        var albumId = CatalogIdentity.AlbumIdFor(song);
        using var device = await DeviceAsync();

        var context = await SendAsync(device, "POST", "/api/flower/v1/cover-art/batch", [],
            body: $$"""{"Ids":["{{albumId}}"],"Size":"{{CoverArtBatch.ThumbnailSize}}"}""");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var art = CoverArtBatch.Read(await BodyOf(context));
        Assert.Equal(CoverArtBatch.ThumbnailPixels, TestCovers.SizeOf(art![albumId]).Width);
    }
}
