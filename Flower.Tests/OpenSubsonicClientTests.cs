using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using Flower.Services;

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

    // Builds the same kind of peerIdentityParams delegate
    // PeerOpenSubsonicClientFactory.Create wires up, standalone here so the
    // test doesn't need a DiscoveredDevice/AppSettings just to exercise the
    // "does every call get a fresh nonce" property.
    private static PeerIdentityParamsBuilder MakePeerIdentityParams()
    {
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var q = ecdsa.ExportParameters(false).Q;
        var raw = new byte[65];
        raw[0] = 0x04;
        Buffer.BlockCopy(q.X!, 0, raw, 1, 32);
        Buffer.BlockCopy(q.Y!, 0, raw, 33, 32);
        var signingKey = new DeviceSigningKey(ecdsa, raw);

        return (method, path, extraParams, body) =>
        {
            var identityParams = new List<(string Key, string Value)>
            {
                ("X-Flower-Fingerprint", signingKey.Fingerprint),
                ("X-Flower-PublicKey", signingKey.PublicKeyBase64),
            };
            var (signature, timestamp, nonce) = signingKey.Sign(method, path, extraParams.Concat(identityParams), body);
            return identityParams.Concat(
            [
                ("X-Flower-Signature", signature),
                ("X-Flower-Timestamp", timestamp),
                ("X-Flower-Nonce", nonce),
            ]);
        };
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
        var client = new OpenSubsonicClient("http://peer.local:53317", "", "", http, peerIdentityParams: MakePeerIdentityParams());

        await client.PingAsync();
        await client.PingAsync();

        Assert.Equal(2, handler.NonceHeaders.Count);
        Assert.All(handler.NonceHeaders, n => Assert.False(string.IsNullOrEmpty(n)));
        Assert.NotEqual(handler.NonceHeaders[0], handler.NonceHeaders[1]);
    }
}
