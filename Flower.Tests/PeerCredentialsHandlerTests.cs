using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Flower.Services;

using Xunit;

namespace Flower.Tests;

// One signature per request, rather than one per URL.
//
// The bug this exists for: PeerStreamUrlResolver builds a streamed track's URL
// once, with the whole signed credential set baked into its query string, and
// hands it onward as a plain string. SeekableHttpStream then fetches that one
// string several times - the bytes=0-0 length probe, the body GET, a reopen
// after a cut connection - while the nonce inside it is single-use on the
// server (NonceReplayGuard.TryRecord is a TryAdd).
//
// So the probe was accepted and everything after it was rejected as a replay,
// which on this surface means Subsonic's "Wrong username or password" on an
// HTTP 200. The track got a length from the request that worked and an error
// message for audio from the one that mattered, and an entire album was
// unplayable on a phone while the server's log showed nothing but successful
// one-byte probes.
public class PeerCredentialsHandlerTests
{
    private static byte[] Content(int length) =>
        Enumerable.Range(0, length).Select(i => (byte)(i % 251)).ToArray();

    // Signs the way SignedDeviceCredentials does in the one respect this is
    // about: a fresh nonce for every call.
    private sealed class FreshNonceCredentials : IPeerCredentials
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<(string Key, string Value)>> AuthorizeAsync(
            string method, string absolutePath, IEnumerable<(string Key, string Value)> query, byte[] body)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<(string Key, string Value)>>(
            [
                ("X-Flower-Fingerprint", "test-device"),
                ("X-Flower-Nonce", Guid.NewGuid().ToString("N")),
            ]);
        }
    }

    // What a URL built by OpenSubsonicClient.BuildUrlAsync signs with, and what
    // a caller that only ever signs once is stuck reusing.
    private sealed class OneShotCredentials : IPeerCredentials
    {
        private readonly string _nonce = Guid.NewGuid().ToString("N");

        public Task<IReadOnlyList<(string Key, string Value)>> AuthorizeAsync(
            string method, string absolutePath, IEnumerable<(string Key, string Value)> query, byte[] body) =>
            Task.FromResult<IReadOnlyList<(string Key, string Value)>>(
            [
                ("X-Flower-Fingerprint", "test-device"),
                ("X-Flower-Nonce", _nonce),
            ]);
    }

    // The server's half: NonceReplayGuard's rule, and the refusal the real
    // server sends when it fires - an error envelope on an HTTP 200, per the
    // Subsonic protocol.
    private sealed class ReplayGuardedServer(byte[] content) : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, byte> _seen = new();

        public int Refused { get; private set; }
        public int Served { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Ahead of the nonce check, and that ordering is load-bearing.
            // Flower.Server maps /rest/stream with MapGet, so a HEAD is a
            // routing 405 that never reaches the auth filter and therefore
            // never spends a nonce. That is precisely why the field failure
            // presented the way it did: the HEAD bounced, the bytes=0-0 probe
            // spent the URL's one nonce and succeeded, and the body GET that
            // followed was the first request to be refused - so the track had
            // a correct length and no audio.
            if (request.Method == HttpMethod.Head)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));

            var nonce = request.Headers.TryGetValues("X-Flower-Nonce", out var values)
                ? values.FirstOrDefault()
                : QueryValue(request.RequestUri!, "X-Flower-Nonce");

            if (nonce == null || !_seen.TryAdd(nonce, 0))
            {
                Refused++;
                var refusal = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"subsonic-response\":{\"status\":\"failed\",\"version\":\"1.16.1\","
                        + "\"error\":{\"code\":40,\"message\":\"Wrong username or password.\"}}}"),
                };
                refusal.Content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                return Task.FromResult(refusal);
            }

            Served++;

            var from = 0L;
            var to = content.Length - 1L;
            var ranged = request.Headers.Range?.Ranges.FirstOrDefault();
            if (ranged != null)
            {
                from = ranged.From ?? 0;
                to = ranged.To ?? content.Length - 1;
            }

            var length = to - from + 1;
            var response = new HttpResponseMessage(
                ranged != null ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content.AsSpan((int)from, (int)length).ToArray()),
            };

            if (ranged != null)
            {
                response.Content.Headers.ContentRange =
                    new System.Net.Http.Headers.ContentRangeHeaderValue(from, from + length - 1, content.Length);
            }

            response.Headers.AcceptRanges.Add("bytes");
            return Task.FromResult(response);
        }

        private static string? QueryValue(Uri uri, string name)
        {
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = pair.IndexOf('=');
                if (separator > 0 && pair[..separator] == name)
                    return pair[(separator + 1)..];
            }

            return null;
        }
    }

    private static HttpClient Signed(HttpMessageHandler server, IPeerCredentials credentials) =>
        new(new PeerCredentialsHandler(() => credentials, server));

    // The whole failure, end to end, and then not happening: a stream read
    // through a client that signs per request survives a server that accepts
    // each nonce exactly once.
    [Fact]
    public async Task A_stream_reads_whole_through_a_server_that_accepts_each_nonce_once()
    {
        var content = Content(20_000);
        var server = new ReplayGuardedServer(content);
        var stream = await SeekableHttpStream.OpenAsync(
            Signed(server, new FreshNonceCredentials()),
            new Uri("https://server/rest/stream?id=abc"));

        var read = new byte[content.Length];
        stream.ReadExactly(read);

        Assert.Equal(content, read);
        Assert.Equal(0, server.Refused);
    }

    // The same stream, against a caller that signs once and reuses it - which
    // is what a URL with the credentials baked into its query string is. Kept
    // as a test rather than deleted with the bug, because it is what says the
    // server-side guard is real and the check above is not passing for some
    // unrelated reason.
    [Fact]
    public async Task Reusing_one_signature_loses_the_track_after_the_probe()
    {
        var content = Content(20_000);
        var server = new ReplayGuardedServer(content);
        var stream = await SeekableHttpStream.OpenAsync(
            Signed(server, new OneShotCredentials()),
            new Uri("https://server/rest/stream?id=abc"));

        // The probe went through, so the length is right and the track looks
        // perfectly healthy at this point. That is the trap.
        Assert.Equal(content.Length, stream.Length);

        Assert.Throws<HttpProtocolErrorException>(() => stream.Read(new byte[1024]));
        Assert.True(server.Refused > 0);
    }

    [Fact]
    public async Task Every_request_is_signed_again()
    {
        var content = Content(4_000);
        var credentials = new FreshNonceCredentials();
        var stream = await SeekableHttpStream.OpenAsync(
            Signed(new ReplayGuardedServer(content), credentials),
            new Uri("https://server/rest/stream?id=abc"));

        stream.ReadExactly(new byte[content.Length]);

        // A probe and at least one body request, each signed for itself.
        Assert.True(credentials.Calls >= 2);
    }

    // A URL that arrives already carrying a spent credential set has it
    // replaced rather than added to - otherwise a stale nonce rides along on
    // every request and lands in the server's device log at rest.
    [Fact]
    public void A_spent_credential_set_is_stripped_from_the_url()
    {
        var stripped = PeerCredentialsHandler.WithoutIdentityParams(new Uri(
            "https://server/rest/stream?id=abc&u=someone&t=token&s=salt"
            + "&X-Flower-Fingerprint=aa&X-Flower-Nonce=bb&X-Flower-Signature=cc"));

        Assert.DoesNotContain("X-Flower-", stripped.Query, StringComparison.OrdinalIgnoreCase);

        // The Subsonic params are the server's fallback credential and part of
        // the signed query on both sides, so they must survive untouched.
        Assert.Contains("id=abc", stripped.Query, StringComparison.Ordinal);
        Assert.Contains("u=someone", stripped.Query, StringComparison.Ordinal);
        Assert.Contains("t=token", stripped.Query, StringComparison.Ordinal);
        Assert.Contains("s=salt", stripped.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void A_url_with_nothing_to_strip_is_left_alone()
    {
        var uri = new Uri("https://server/rest/stream?id=abc");

        Assert.Equal(uri, PeerCredentialsHandler.WithoutIdentityParams(uri));
    }

    // Null credentials are the ordinary state for a head with no signing key -
    // the browser, whose media URL carries a stream ticket instead. The request
    // has to go out untouched rather than fail.
    [Fact]
    public async Task A_client_with_no_credentials_still_sends_the_request()
    {
        var content = Content(1_000);
        using var client = new HttpClient(
            new PeerCredentialsHandler(() => null, new ReplayGuardedServer(content)));

        var response = await client.GetAsync("https://server/rest/stream?id=abc&X-Flower-Nonce=ticketish");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
