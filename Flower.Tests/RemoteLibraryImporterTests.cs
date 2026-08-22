using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Importer;
using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests;

// The importer on its own, against a real socket - not through
// LibrarySyncService, which is where LibrarySyncConditionalPullTests exercises
// it. Worth testing at this level because the browser head has no
// LibrarySyncService: it registers this class as its IMusicImporter outright,
// so everything below is the whole of what stands between a served manifest and
// a populated library there.
public class RemoteLibraryImporterTests
{
    // Web defaults (camelCase) rather than the PascalCase a real Flower host
    // writes, deliberately: the reader is meant to be case-insensitive, and a
    // test that only ever feeds it the exact casing it emits would not notice
    // if that stopped being true.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly HttpClient Http = new();

    private const string OriginFingerprint = "server-fingerprint";
    private const string OwnFingerprint = "our-fingerprint";

    private static Child RemoteSong(string title, Dictionary<string, int>? playCounts = null) => new(
        Id: "sync:" + title,
        Title: title,
        Album: "Remote Album",
        Artist: "Remote Artist",
        AlbumId: null, ArtistId: null, Track: null, Year: null, Genre: null,
        Size: null, ContentType: null, Suffix: "flac", Duration: 180, BitRate: null,
        CoverArt: "art-1",
        PlayCounts: playCounts);

    private static RemoteLibraryImporter Importer(FakePeerHttpServer peer, string ownFingerprint = OwnFingerprint) =>
        new(Http, $"http://127.0.0.1:{peer.Port}", new UnauthenticatedCredentials(),
            OriginFingerprint, ownFingerprint, NullLogger<RemoteLibraryImporter>.Instance);

    private static FakePeerHttpServer ServingManifest(
        List<Child> songs, string? etag = null, Action<HttpListenerContext>? inspect = null) =>
        new(async context =>
        {
            inspect?.Invoke(context);
            if (etag != null)
            {
                if (context.Request.Headers["If-None-Match"] == etag)
                {
                    context.Response.StatusCode = 304;
                    context.Response.Close();
                    return;
                }

                context.Response.Headers["ETag"] = etag;
            }

            var bytes = Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new LibrarySyncManifestDto(OriginFingerprint, songs), JsonOptions));
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        });

    [Fact]
    public async Task Every_imported_track_is_a_playable_placeholder_addressed_back_to_the_origin()
    {
        using var peer = ServingManifest([RemoteSong("Remote One")]);

        var tracks = await Importer(peer).ImportAsync();

        var track = Assert.Single(tracks);
        Assert.Equal("Remote One", track.Title);
        // The three facts a later stream or download request is built from. A
        // placeholder missing any of them is in the library and unplayable,
        // which is worse than not being there at all.
        Assert.Null(track.Path);
        Assert.Equal(OriginFingerprint, track.OriginDeviceFingerprint);
        Assert.Equal("sync:Remote One", track.OriginTrackId);
    }

    [Fact]
    public async Task It_asks_the_bulk_endpoint_rather_than_walking_albums()
    {
        var paths = new List<string>();
        using var peer = ServingManifest([], inspect: context => paths.Add(context.Request.Url!.AbsolutePath));

        await Importer(peer).ImportAsync();

        // One request for the whole catalog. The album-at-a-time Subsonic shape
        // this replaced is what made a real library hundreds of connections in
        // a burst - see LibrarySyncContracts.
        Assert.Equal([RemoteLibraryImporter.LibraryPath], paths);
    }

    [Fact]
    public async Task A_play_count_the_origin_learned_from_us_does_not_come_back_as_a_remote_one()
    {
        using var peer = ServingManifest([RemoteSong("Remote One", new Dictionary<string, int>
        {
            [OwnFingerprint] = 3,
            ["someone-else"] = 7,
        })]);

        var track = Assert.Single(await Importer(peer).ImportAsync());

        // Our own count is authoritative locally and must never be overwritten
        // by a peer echoing back what it once learned about us.
        Assert.Equal(new Dictionary<string, int> { ["someone-else"] = 7 }, track.RemotePlayCounts);
    }

    [Fact]
    public async Task A_head_with_no_fingerprint_of_its_own_keeps_every_count_it_is_sent()
    {
        using var peer = ServingManifest([RemoteSong("Remote One", new Dictionary<string, int>
        {
            ["someone-else"] = 7,
        })]);

        // The browser passes an empty own-fingerprint, which matches nothing -
        // correctly, since it has never played anything for a peer to echo.
        var track = Assert.Single(await Importer(peer, ownFingerprint: "").ImportAsync());

        Assert.Equal(new Dictionary<string, int> { ["someone-else"] = 7 }, track.RemotePlayCounts);
    }

    [Fact]
    public async Task An_unchanged_catalog_reports_itself_as_unchanged_and_not_as_an_empty_one()
    {
        const string token = "abc12345-7";
        using var peer = ServingManifest([RemoteSong("Remote One")], etag: token);
        var importer = Importer(peer);

        var first = await importer.FetchAsync();
        var second = await importer.FetchAsync(first.ETag);

        Assert.False(first.NotModified);
        Assert.Equal(token, first.ETag);
        Assert.Single(first.Tracks);

        // The distinction the whole record struct exists for: a caller that read
        // Tracks alone would see an emptied catalog and prune the library.
        Assert.True(second.NotModified);
        Assert.Empty(second.Tracks);
        Assert.Equal(token, second.ETag);
    }

    [Fact]
    public async Task A_refusal_throws_rather_than_reading_as_a_peer_with_no_music()
    {
        using var peer = new FakePeerHttpServer(context =>
        {
            context.Response.StatusCode = 403;
            context.Response.Close();
            return Task.CompletedTask;
        });

        // The failure mode this guards: swallowing the refusal and returning an
        // empty list hands Library.MergeSyncedTracks an empty catalog, which
        // prunes every placeholder learned from this origin. Throwing also lets
        // LibrarySyncService tell 403 (revoked) from 401 (bad signature).
        var thrown = await Assert.ThrowsAsync<HttpRequestException>(() => Importer(peer).ImportAsync());
        Assert.Equal(HttpStatusCode.Forbidden, thrown.StatusCode);
    }

    // Stands in for a credential that proves nothing - enough to exercise the
    // importer, since FakePeerHttpServer has no auth gate of its own. The real
    // implementations are covered where they are verified: SignedDeviceCredentials
    // through StreamAuthEndToEndTests, against the same PeerSignatureAuth a
    // server runs.
    private sealed class UnauthenticatedCredentials : IPeerCredentials
    {
        public IEnumerable<(string Key, string Value)> Authorize(
            string method, string absolutePath, IEnumerable<(string Key, string Value)> query, byte[] body) => [];
    }
}
