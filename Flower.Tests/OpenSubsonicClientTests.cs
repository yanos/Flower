using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;

namespace Flower.Tests;

public class OpenSubsonicClientTests
{
    // Records the last requested URL (and every request's X-Flower-Nonce
    // header, for the peer-identity nonce-uniqueness test below) and replies
    // with a fixed body - stands in for a real OpenSubsonic server so these
    // tests need no network/live instance.
    private sealed class FakeHandler(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }
        public List<string?> NonceHeaders { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            NonceHeaders.Add(request.Headers.TryGetValues("X-Flower-Nonce", out var values) ? values.FirstOrDefault() : null);
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody),
            });
        }
    }

    // The real SignedDeviceCredentials, over a throwaway keypair - this used to
    // build its own stand-in delegate, which is no longer worth doing now that
    // one class serves every call site (see IPeerCredentials).
    private static IPeerCredentials MakePeerCredentials()
    {
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var q = ecdsa.ExportParameters(false).Q;
        var raw = new byte[65];
        raw[0] = 0x04;
        Buffer.BlockCopy(q.X!, 0, raw, 1, 32);
        Buffer.BlockCopy(q.Y!, 0, raw, 33, 32);
        var signingKey = new DeviceSigningKey(ecdsa, raw);

        return new SignedDeviceCredentials(
            new DeviceIdentity { Fingerprint = signingKey.Fingerprint, Alias = "test-device" },
            signingKey,
            new AppSettings());
    }

    private static OpenSubsonicClient MakeClient(string responseBody, out FakeHandler handler)
    {
        handler = new FakeHandler(responseBody);
        var http = new HttpClient(handler);
        var client = new OpenSubsonicClient("http://peer.local:4533", "alice", "hunter2", http);
        return client;
    }

    [Fact]
    public void ComputeToken_is_deterministic_md5_of_password_plus_salt()
    {
        // Fixture from the Subsonic API docs' own worked example.
        var token = OpenSubsonicClient.ComputeToken("sesame", "c19b2d");

        Assert.Equal("26719a1196d2a940705a59634eb18eab", token);
    }

    [Fact]
    public async Task PingAsync_sends_auth_params_and_succeeds_on_ok_status()
    {
        const string body = """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""";
        var client = MakeClient(body, out var handler);

        await client.PingAsync();

        Assert.NotNull(handler.LastRequestUri);
        var query = handler.LastRequestUri!.Query;
        Assert.Contains("u=alice", query);
        Assert.Contains("f=json", query);
        Assert.Contains("t=", query);
        Assert.Contains("s=", query);
        Assert.StartsWith("http://peer.local:4533/rest/ping", handler.LastRequestUri.GetLeftPart(UriPartial.Path));
    }

    [Fact]
    public async Task Failed_status_throws_with_server_error_code_and_message()
    {
        const string body = """{"subsonic-response":{"status":"failed","version":"1.16.1","error":{"code":40,"message":"Wrong username or password."}}}""";
        var client = MakeClient(body, out _);

        var ex = await Assert.ThrowsAsync<SubsonicException>(() => client.PingAsync());

        Assert.Equal(40, ex.Code);
        Assert.Equal("Wrong username or password.", ex.Message);
    }

    // No "subsonic-response" wrapper at all - distinct from the "failed"
    // status case above (a well-formed error the server deliberately sent):
    // this is what a byte-for-byte truncated/corrupted response, or a
    // non-Subsonic server answering on the same port, looks like.
    [Fact]
    public async Task Malformed_envelope_throws_SubsonicException()
    {
        var client = MakeClient("{}", out _);

        var ex = await Assert.ThrowsAsync<SubsonicException>(() => client.PingAsync());

        Assert.Equal("Empty or malformed subsonic-response envelope.", ex.Message);
    }

    // A non-2xx status (peer's trust gate rejecting us, or any other HTTP
    // error) must surface as a plain HttpRequestException from
    // EnsureSuccessStatusCode - not get swallowed or misread as valid JSON -
    // see SendAsync's own comment on this.
    [Fact]
    public async Task Non_success_status_throws_HttpRequestException_before_attempting_to_parse_the_body()
    {
        var handler = new FakeHandler("not json at all", HttpStatusCode.Forbidden);
        var http = new HttpClient(handler);
        var client = new OpenSubsonicClient("http://peer.local:4533", "alice", "hunter2", http);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.PingAsync());
    }

    // Real socket-level connection failure (nothing listening on the port at
    // all) rather than a fake handler standing in for one - proves a
    // genuinely unreachable peer (network outage, peer app not running)
    // surfaces the same way any other HttpClient consumer would expect,
    // rather than hanging or throwing something Flower-specific.
    [Fact]
    public async Task Connection_refused_throws_HttpRequestException()
    {
        var unboundPort = FakePeerHttpServer.GetUnboundPort();
        var client = new OpenSubsonicClient($"http://127.0.0.1:{unboundPort}", "alice", "hunter2");

        await Assert.ThrowsAsync<HttpRequestException>(() => client.PingAsync());
    }

    // Simulates a network outage partway through a stream/download: the
    // peer accepts the connection and starts responding but the connection
    // drops before the declared Content-Length is fully sent. Confirms this
    // surfaces as an exception the caller can act on (LibraryDownloadService
    // catches it and reports TrackDownloadResult.Failed - see
    // LibraryDownloadServiceTests) rather than silently succeeding with a
    // truncated file.
    [Fact]
    public async Task DownloadTrackAsync_throws_when_the_connection_drops_mid_transfer()
    {
        var fullPayload = new byte[64 * 1024];
        Random.Shared.NextBytes(fullPayload);
        using var server = new FakePeerHttpServer(async ctx =>
        {
            ctx.Response.ContentLength64 = fullPayload.Length;
            await ctx.Response.OutputStream.WriteAsync(fullPayload.AsMemory(0, fullPayload.Length / 4));
            await ctx.Response.OutputStream.FlushAsync();
            ctx.Response.Abort();
        });
        var client = new OpenSubsonicClient($"http://127.0.0.1:{server.Port}", "alice", "hunter2");
        var destination = Path.Combine(Path.GetTempPath(), $"flower-download-test-{Guid.NewGuid():N}.bin");

        try
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => client.DownloadTrackAsync("sg-1", destination));

            // The destination itself must not exist - a truncated file under
            // the name the library records would be indistinguishable from a
            // playable download. The bytes that did arrive are kept beside it
            // instead, which is what the next attempt resumes from.
            Assert.False(File.Exists(destination));
            Assert.True(new FileInfo(destination + OpenSubsonicClient.PartialSuffix).Length > 0);
        }
        finally
        {
            File.Delete(destination);
            File.Delete(destination + OpenSubsonicClient.PartialSuffix);
        }
    }

    // The point of Tier 4.4: on flaky wifi a large track otherwise restarts
    // at byte 0 every attempt and may never finish.
    [Fact]
    public async Task DownloadTrackAsync_resumes_from_the_bytes_a_failed_attempt_already_wrote()
    {
        var fullPayload = new byte[64 * 1024];
        Random.Shared.NextBytes(fullPayload);
        var sent = 0;
        var rangesSeen = new List<string?>();
        using var server = new FakePeerHttpServer(async ctx =>
        {
            rangesSeen.Add(ctx.Request.Headers["Range"]);
            if (Interlocked.Increment(ref sent) == 1)
            {
                ctx.Response.ContentLength64 = fullPayload.Length;
                await ctx.Response.OutputStream.WriteAsync(fullPayload.AsMemory(0, fullPayload.Length / 4));
                await ctx.Response.OutputStream.FlushAsync();
                ctx.Response.Abort();
                return;
            }

            var from = int.Parse(ctx.Request.Headers["Range"]!["bytes=".Length..].TrimEnd('-'));
            ctx.Response.StatusCode = 206;
            ctx.Response.Headers["Content-Range"] = $"bytes {from}-{fullPayload.Length - 1}/{fullPayload.Length}";
            ctx.Response.ContentLength64 = fullPayload.Length - from;
            await ctx.Response.OutputStream.WriteAsync(fullPayload.AsMemory(from));
        });
        var client = new OpenSubsonicClient($"http://127.0.0.1:{server.Port}", "alice", "hunter2");
        var destination = Path.Combine(Path.GetTempPath(), $"flower-download-test-{Guid.NewGuid():N}.bin");

        try
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => client.DownloadTrackAsync("sg-1", destination));
            var partial = new FileInfo(destination + OpenSubsonicClient.PartialSuffix).Length;

            await client.DownloadTrackAsync("sg-1", destination);

            Assert.Equal(fullPayload, File.ReadAllBytes(destination));
            // Not just "the file is right at the end" - the second attempt
            // asked for the remainder rather than the whole thing again.
            Assert.Equal([null, $"bytes={partial}-"], rangesSeen);
        }
        finally
        {
            File.Delete(destination);
            File.Delete(destination + OpenSubsonicClient.PartialSuffix);
        }
    }

    // The partial is longer than the track now is (the file changed on the
    // serving device between attempts), so the server answers 416 - retrying
    // the same request forever would never recover.
    [Fact]
    public async Task DownloadTrackAsync_discards_an_unsatisfiable_partial_and_refetches_the_whole_track()
    {
        var fullPayload = new byte[8 * 1024];
        Random.Shared.NextBytes(fullPayload);
        var requests = 0;
        using var server = new FakePeerHttpServer(async ctx =>
        {
            Interlocked.Increment(ref requests);
            if (ctx.Request.Headers["Range"] != null)
            {
                ctx.Response.StatusCode = 416;
                ctx.Response.Headers["Content-Range"] = $"bytes */{fullPayload.Length}";
                ctx.Response.ContentLength64 = 0;
                ctx.Response.Close();
                return;
            }

            ctx.Response.ContentLength64 = fullPayload.Length;
            await ctx.Response.OutputStream.WriteAsync(fullPayload.AsMemory());
        });
        var client = new OpenSubsonicClient($"http://127.0.0.1:{server.Port}", "alice", "hunter2");
        var destination = Path.Combine(Path.GetTempPath(), $"flower-download-test-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(destination + OpenSubsonicClient.PartialSuffix, new byte[fullPayload.Length * 2]);

        try
        {
            await client.DownloadTrackAsync("sg-1", destination);

            Assert.Equal(fullPayload, File.ReadAllBytes(destination));
            Assert.Equal(2, requests);
        }
        finally
        {
            File.Delete(destination);
            File.Delete(destination + OpenSubsonicClient.PartialSuffix);
        }
    }

    // A peer running an older build, or any server that simply ignores Range,
    // answers 200 with the whole body - which cannot be appended to a partial
    // without corrupting it, so the partial is overwritten instead.
    [Fact]
    public async Task DownloadTrackAsync_overwrites_the_partial_when_the_server_ignores_the_resume_range()
    {
        var fullPayload = new byte[32 * 1024];
        Random.Shared.NextBytes(fullPayload);
        var sent = 0;
        using var server = new FakePeerHttpServer(async ctx =>
        {
            if (Interlocked.Increment(ref sent) == 1)
            {
                ctx.Response.ContentLength64 = fullPayload.Length;
                await ctx.Response.OutputStream.WriteAsync(fullPayload.AsMemory(0, fullPayload.Length / 4));
                await ctx.Response.OutputStream.FlushAsync();
                ctx.Response.Abort();
                return;
            }

            ctx.Response.ContentLength64 = fullPayload.Length;
            await ctx.Response.OutputStream.WriteAsync(fullPayload.AsMemory());
        });
        var client = new OpenSubsonicClient($"http://127.0.0.1:{server.Port}", "alice", "hunter2");
        var destination = Path.Combine(Path.GetTempPath(), $"flower-download-test-{Guid.NewGuid():N}.bin");

        try
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => client.DownloadTrackAsync("sg-1", destination));

            await client.DownloadTrackAsync("sg-1", destination);

            Assert.Equal(fullPayload, File.ReadAllBytes(destination));
        }
        finally
        {
            File.Delete(destination);
            File.Delete(destination + OpenSubsonicClient.PartialSuffix);
        }
    }

    [Fact]
    public async Task GetArtistsAsync_parses_indexed_artist_list()
    {
        const string body = """
            {"subsonic-response":{"status":"ok","version":"1.16.1","artists":{"index":[
                {"name":"B","artist":[{"id":"ar-1","name":"Beatles","coverArt":null,"albumCount":3}]}
            ]}}}
            """;
        var client = MakeClient(body, out _);

        var index = await client.GetArtistsAsync();

        var group = Assert.Single(index);
        Assert.Equal("B", group.Name);
        var artist = Assert.Single(group.Artist);
        Assert.Equal("Beatles", artist.Name);
        Assert.Equal(3, artist.AlbumCount);
    }

    [Fact]
    public async Task GetAlbumAsync_parses_album_with_songs()
    {
        const string body = """
            {"subsonic-response":{"status":"ok","version":"1.16.1","album":{
                "id":"al-1","name":"Abbey Road","artist":"Beatles","artistId":"ar-1",
                "coverArt":"al-1","songCount":2,"duration":3000,"year":1969,"genre":"Rock",
                "song":[
                    {"id":"sg-1","title":"Come Together","album":"Abbey Road","artist":"Beatles","duration":259,"track":1},
                    {"id":"sg-2","title":"Something","album":"Abbey Road","artist":"Beatles","duration":183,"track":2}
                ]
            }}}
            """;
        var client = MakeClient(body, out _);

        var album = await client.GetAlbumAsync("al-1");

        Assert.Equal("Abbey Road", album.Name);
        Assert.Equal(2, album.Song?.Count);
        Assert.Equal("Come Together", album.Song![0].Title);
    }

    [Fact]
    public async Task GetPlaylistsAsync_parses_playlist_list()
    {
        const string body = """
            {"subsonic-response":{"status":"ok","version":"1.16.1","playlists":{"playlist":[
                {"id":"pl-1","name":"Road Trip","songCount":5,"duration":1200,"owner":"alice","public":false}
            ]}}}
            """;
        var client = MakeClient(body, out _);

        var playlists = await client.GetPlaylistsAsync();

        var playlist = Assert.Single(playlists);
        Assert.Equal("Road Trip", playlist.Name);
        Assert.Equal(5, playlist.SongCount);
    }

    [Fact]
    public async Task CreatePlaylistAsync_sends_repeated_songId_params()
    {
        const string body = """{"subsonic-response":{"status":"ok","version":"1.16.1","playlist":{"id":"pl-2","name":"New","songCount":2,"duration":0,"owner":"alice","public":false}}}""";
        var client = MakeClient(body, out var handler);

        var created = await client.CreatePlaylistAsync("New", ["sg-1", "sg-2"]);

        Assert.NotNull(created);
        Assert.Equal("pl-2", created!.Id);
        var query = handler.LastRequestUri!.Query;
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(query, "songId=").Count);
    }

    [Fact]
    public void GetStreamUrl_and_GetCoverArtUrl_build_authed_urls_without_a_request()
    {
        var client = new OpenSubsonicClient("http://peer.local:4533", "alice", "hunter2", new HttpClient(new FakeHandler("")));

        var streamUrl = client.GetStreamUrl("sg-1");
        var coverUrl = client.GetCoverArtUrl("al-1", size: 300);

        Assert.StartsWith("http://peer.local:4533/rest/stream?", streamUrl);
        Assert.Contains("id=sg-1", streamUrl);
        Assert.StartsWith("http://peer.local:4533/rest/getCoverArt?", coverUrl);
        Assert.Contains("size=300", coverUrl);
    }

    // Regression guard against reverting to a fixed, computed-once header
    // list (the pre-signing-scheme extraHeaders shape) - a signature/nonce
    // must be freshly generated on every call (see DeviceSigningKey.Sign),
    // since SignatureVerifier.NonceReplayGuard treats a repeated nonce as a
    // replay attempt.
    [Fact]
    public async Task Consecutive_peer_identity_calls_send_different_nonces()
    {
        const string body = """{"subsonic-response":{"status":"ok","version":"1.16.1"}}""";
        var handler = new FakeHandler(body);
        var http = new HttpClient(handler);
        var client = new OpenSubsonicClient("http://peer.local:53317", "", "", http, credentials: MakePeerCredentials());

        await client.PingAsync();
        await client.PingAsync();

        Assert.Equal(2, handler.NonceHeaders.Count);
        Assert.All(handler.NonceHeaders, n => Assert.False(string.IsNullOrEmpty(n)));
        Assert.NotEqual(handler.NonceHeaders[0], handler.NonceHeaders[1]);
    }
}
