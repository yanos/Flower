using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Flower.Services;

using Xunit;

namespace Flower.Tests;

// The stream a remote track is decoded from. What is being pinned down here
// is that a decoder can treat it exactly like a local file - read, seek
// backwards, seek to the end and back - and that the request traffic behind
// that stays sane, because the whole point of the class is to stop the
// platform's HTTP stack deciding those questions for us. See
// SeekableHttpStream's own header for the bug that produced it.
public class SeekableHttpStreamTests
{
    private static byte[] Content(int length) =>
        Enumerable.Range(0, length).Select(i => (byte)(i % 251)).ToArray();

    // Serves one byte array the way the Subsonic /stream endpoint does, and
    // counts requests so a test can assert about traffic rather than only
    // about bytes.
    private sealed class FakeServer : HttpMessageHandler
    {
        private readonly byte[] _content;

        public FakeServer(byte[] content) => _content = content;

        public bool ServesRanges { get; init; } = true;

        // Every GET resets the connection, standing in for a server that has
        // gone away for good rather than for a moment.
        public bool FailsEverything { get; init; }
        public bool AnswersHead { get; init; } = true;
        public int Requests { get; private set; }
        public int RangedRequests { get; private set; }

        // Refuses this many requests with 429 before serving anything, and
        // says how long to wait - or does not, which is the case a client has
        // to have its own answer for.
        public int Refusals { get; set; }
        public TimeSpan? RetryAfter { get; init; } = TimeSpan.FromMilliseconds(1);
        public int Refused { get; private set; }

        // Cuts the body short once, at this offset, to stand in for a
        // connection dropped mid-track.
        public long? TruncateAt { get; set; }

        // Refuses body requests the way the real server refuses an
        // unauthenticated one: with Subsonic's error envelope on an HTTP 200,
        // which the protocol mandates and which makes a status-code check
        // useless on this surface.
        //
        // Bodies only, and the probe deliberately let through, because that
        // asymmetry is the whole reason the field failure was so hard to read:
        // a stream URL was signed once and its single-use nonce was spent by
        // the bytes=0-0 probe, so the track got a correct length from a request
        // that succeeded and then 130 bytes of JSON from the one that mattered.
        public bool RefusesBodiesWithSubsonicError { get; init; }

        // The other body that is not audio and used to be decoded as though it
        // were: a proxy or captive portal answering with a sign-in page.
        public bool RefusesBodiesWithHtml { get; init; }

        public const string SubsonicErrorMessage = "Wrong username or password.";

        private static HttpResponseMessage SubsonicError() =>
            Textual("application/json",
                "{\"subsonic-response\":{\"status\":\"failed\",\"version\":\"1.16.1\","
                + "\"error\":{\"code\":40,\"message\":\"" + SubsonicErrorMessage + "\"}}}");

        private static HttpResponseMessage Textual(string mediaType, string body)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body),
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType)
            {
                CharSet = "utf-8",
            };
            return response;
        }

        // Exactly what a phone does. .NET's mobile HttpClientHandler has no
        // synchronous path, so this throws for every request - which is how
        // the first version of SeekableHttpStream, green on desktop, made
        // every streamed track on the phone unplayable. Refusing it here is
        // what keeps a synchronous call from being reintroduced.
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new PlatformNotSupportedException("Operation is not supported on this platform.");

        private HttpResponseMessage Respond(HttpRequestMessage request)
        {
            Requests++;

            if (Refusals > 0)
            {
                Refusals--;
                Refused++;
                var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new ByteArrayContent([]),
                };
                if (RetryAfter is { } after)
                    throttled.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(after);
                return throttled;
            }

            if (request.Method == HttpMethod.Head)
            {
                if (!AnswersHead)
                    return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);

                var head = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
                head.Content.Headers.ContentLength = _content.Length;
                if (ServesRanges)
                    head.Headers.AcceptRanges.Add("bytes");
                return head;
            }

            if (FailsEverything)
                throw new IOException("connection reset");

            var probe = request.Headers.Range?.Ranges.FirstOrDefault() is { From: 0, To: 0 };
            if (!probe && RefusesBodiesWithSubsonicError)
                return SubsonicError();
            if (!probe && RefusesBodiesWithHtml)
                return Textual("text/html", "<html><body>Please sign in</body></html>");

            var from = 0L;
            var to = _content.Length - 1L;
            var ranged = request.Headers.Range?.Ranges.FirstOrDefault();
            if (ranged != null)
            {
                RangedRequests++;
                if (ServesRanges)
                {
                    from = ranged.From ?? 0;
                    to = ranged.To ?? _content.Length - 1;
                }
            }

            var length = to - from + 1;
            if (TruncateAt is { } cut && from < cut)
            {
                length = Math.Min(length, cut - from);
                TruncateAt = null;
            }

            var body = _content.AsSpan((int)from, (int)length).ToArray();
            var partial = ServesRanges && ranged != null;
            var response = new HttpResponseMessage(partial ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
            };

            if (partial)
                response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, from + length - 1, _content.Length);
            if (ServesRanges)
                response.Headers.AcceptRanges.Add("bytes");

            return response;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Respond(request));
    }

    private static async Task<(SeekableHttpStream Stream, FakeServer Server)> OpenAsync(byte[] content, Action<FakeServer>? configure = null, FakeServer? server = null)
    {
        server ??= new FakeServer(content);
        configure?.Invoke(server);
        var stream = await SeekableHttpStream.OpenAsync(new HttpClient(server), new Uri("https://server/rest/stream?id=abc"));
        return (stream, server);
    }

    private static byte[] ReadFully(Stream stream, int count)
    {
        var buffer = new byte[count];
        stream.ReadExactly(buffer);
        return buffer;
    }

    [Fact]
    public async Task The_length_is_known_before_a_byte_is_read()
    {
        var content = Content(4096);
        var (stream, server) = await OpenAsync(content);

        Assert.Equal(content.Length, stream.Length);
        Assert.True(stream.CanSeek);
        Assert.Equal(1, server.Requests);
    }

    [Fact]
    public async Task Reading_from_the_start_returns_the_file()
    {
        var content = Content(10_000);
        var (stream, _) = await OpenAsync(content);

        Assert.Equal(content, ReadFully(stream, content.Length));
    }

    [Fact]
    public async Task Seeking_forwards_reads_from_the_new_offset()
    {
        var content = Content(10_000);
        var (stream, _) = await OpenAsync(content);

        stream.Seek(6_000, SeekOrigin.Begin);

        Assert.Equal(content[6_000..6_100], ReadFully(stream, 100));
    }

    // The one a non-seekable stream cannot do, and the reason this class
    // exists at all.
    [Fact]
    public async Task Seeking_backwards_reads_from_the_new_offset()
    {
        var content = Content(10_000);
        var (stream, _) = await OpenAsync(content);
        ReadFully(stream, 9_000);

        stream.Seek(120, SeekOrigin.Begin);

        Assert.Equal(content[120..220], ReadFully(stream, 100));
    }

    // How a demuxer finds an MP4's moov box when it was written last.
    [Fact]
    public async Task Seeking_relative_to_the_end_reads_the_tail()
    {
        var content = Content(10_000);
        var (stream, _) = await OpenAsync(content);

        stream.Seek(-64, SeekOrigin.End);

        Assert.Equal(content[^64..], ReadFully(stream, 64));
    }

    [Fact]
    public async Task Reading_past_the_end_stops_rather_than_reopening()
    {
        var content = Content(1_000);
        var (stream, _) = await OpenAsync(content);
        ReadFully(stream, 1_000);

        Assert.Equal(0, stream.Read(new byte[16]));
    }

    // A seek is a number until something is read, so a demuxer probing its way
    // around the container costs one request rather than one per seek.
    [Fact]
    public async Task Seeking_without_reading_costs_no_request()
    {
        var (stream, server) = await OpenAsync(Content(10_000));
        var before = server.Requests;

        stream.Seek(1_000, SeekOrigin.Begin);
        stream.Seek(2_000, SeekOrigin.Begin);
        stream.Seek(3_000, SeekOrigin.Begin);

        Assert.Equal(before, server.Requests);
    }

    // Skipping a box the demuxer does not care about should not cost a round
    // trip - the bytes are already in flight.
    [Fact]
    public async Task A_short_forward_seek_is_served_from_the_response_already_open()
    {
        var content = Content(200_000);
        var (stream, server) = await OpenAsync(content);
        ReadFully(stream, 16);
        var after = server.Requests;

        stream.Seek(40_000, SeekOrigin.Begin);

        Assert.Equal(content[40_000..40_016], ReadFully(stream, 16));
        Assert.Equal(after, server.Requests);
    }

    [Fact]
    public async Task A_connection_cut_mid_track_is_reopened_from_where_it_stopped()
    {
        var content = Content(20_000);
        var (stream, server) = await OpenAsync(content, s => s.TruncateAt = 8_000);

        Assert.Equal(content, ReadFully(stream, content.Length));
        Assert.True(server.RangedRequests >= 1, "the reopen should have asked for the remaining range");
    }

    // Some servers refuse HEAD outright; the length still has to come from
    // somewhere before the first read.
    [Fact]
    public async Task A_server_that_refuses_HEAD_is_probed_with_a_ranged_GET_instead()
    {
        var content = Content(4_096);
        var (stream, _) = await OpenAsync(content, server: new FakeServer(content) { AnswersHead = false });

        Assert.Equal(content.Length, stream.Length);
        Assert.True(stream.CanSeek);
        Assert.Equal(content, ReadFully(stream, content.Length));
    }

    // Not a supported configuration, but it must degrade to "plays if nothing
    // seeks" rather than to wrong audio - reading from the wrong offset would
    // be worse than refusing.
    [Fact]
    public async Task A_server_without_ranges_reads_forward_and_refuses_to_seek()
    {
        var content = Content(4_096);
        var (stream, _) = await OpenAsync(content, server: new FakeServer(content) { ServesRanges = false });

        Assert.False(stream.CanSeek);
        Assert.Equal(content[..100], ReadFully(stream, 100));
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
    }

    // Who opens these decides where the round trip lands: LibVLC and FFmpeg
    // both call open on their own reading thread, and a probe in the
    // constructor would put it on whichever thread built the decoder.
    [Fact]
    public void Constructing_one_asks_the_server_nothing()
    {
        var content = Content(4_096);
        var server = new FakeServer(content);

        _ = new SeekableHttpStream(new HttpClient(server), new Uri("https://server/rest/stream?id=abc"));

        Assert.Equal(0, server.Requests);
    }

    [Fact]
    public void A_lazily_constructed_stream_probes_on_first_use()
    {
        var content = Content(4_096);
        var server = new FakeServer(content);
        var stream = new SeekableHttpStream(new HttpClient(server), new Uri("https://server/rest/stream?id=abc"));

        Assert.Equal(content.Length, stream.Length);
        Assert.Equal(content, ReadFully(stream, content.Length));
    }

    // A server that never recovers must cost a bounded number of requests, not
    // one run of retries per Read call. LibVLC reads several times a second
    // and does not stop when told there was an error, so an unlatched failure
    // becomes an unbounded storm against a server already in trouble - which
    // is how it was found.
    [Fact]
    public async Task A_stream_that_cannot_recover_stops_asking()
    {
        var content = Content(20_000);
        var server = new FakeServer(content) { FailsEverything = true };
        var (stream, _) = await OpenAsync(content, server: server);
        var afterProbe = server.Requests;

        for (var i = 0; i < 20; i++)
            Assert.Throws<IOException>(() => stream.Read(new byte[1024]));

        Assert.InRange(server.Requests - afterProbe, 1, 8);
    }

    // The one that cost an album. 429 is not the stream failing - it is the
    // server asking to be left alone for a moment - and the difference between
    // waiting it out and treating it as an I/O error is the difference between
    // a track that stalls and a track the queue declares dead and skips. On
    // Flower.Server the budget was shared across the whole /rest surface, so a
    // cover-art burst spent it and the next thing refused was the audio.
    [Fact]
    public async Task A_throttled_server_is_waited_out_rather_than_retried_against()
    {
        var content = Content(20_000);
        var server = new FakeServer(content);
        var (stream, _) = await OpenAsync(content, server: server);

        // More refusals than the reopen budget, which is exactly the shape
        // that used to lose the track: three attempts, then dead.
        server.Refusals = 5;

        Assert.Equal(content, ReadFully(stream, content.Length));
        Assert.Equal(5, server.Refused);
    }

    // Without a Retry-After the client has to pick its own wait, and still
    // must not turn the refusal into a failure.
    [Fact]
    public async Task A_throttled_server_that_names_no_delay_is_still_waited_out()
    {
        var content = Content(20_000);
        var server = new FakeServer(content) { RetryAfter = null };
        var (stream, _) = await OpenAsync(content, server: server);

        server.Refusals = 1;

        Assert.Equal(content, ReadFully(stream, content.Length));
    }

    // Being throttled must not spend the reopen budget either. A 429 that
    // counted as an attempt would leave a stream that was fine one real
    // failure away from being latched broken.
    [Fact]
    public async Task Being_throttled_does_not_spend_the_reopen_budget()
    {
        var content = Content(20_000);
        var server = new FakeServer(content) { TruncateAt = 8_000 };
        var (stream, _) = await OpenAsync(content, server: server);

        server.Refusals = 4;

        Assert.Equal(content, ReadFully(stream, content.Length));
    }

    // Even a throttle that outlasts the wait budget leaves the stream usable:
    // the server is busy, not gone, and latching it broken is how a transient
    // refusal became a permanently unplayable track.
    [Fact]
    public async Task A_throttle_that_outlasts_the_wait_does_not_break_the_stream()
    {
        var content = Content(20_000);
        var server = new FakeServer(content) { RetryAfter = TimeSpan.FromMilliseconds(20) };
        var (stream, _) = await OpenAsync(content, server: server);
        stream.ThrottleWaitBudget = TimeSpan.FromMilliseconds(100);

        server.Refusals = 1_000;
        Assert.Throws<HttpThrottledException>(() => stream.Read(new byte[1024]));

        server.Refusals = 0;
        Assert.Equal(content, ReadFully(stream, content.Length));
    }

    // The Subsonic protocol answers a failed request with HTTP 200 and an
    // error envelope, so EnsureSuccessStatusCode proves nothing on this
    // surface. Without this check those ~130 bytes of JSON became the start of
    // the track: the body then ended far short of the length the probe had
    // just established, which read as a dropped connection, which reopened and
    // got the same JSON again. The album skipped through itself in seconds and
    // every log showed a successful probe.
    [Fact]
    public async Task A_subsonic_error_on_a_200_is_not_read_as_audio()
    {
        var content = Content(20_000);
        var (stream, _) = await OpenAsync(content, server: new FakeServer(content)
        {
            RefusesBodiesWithSubsonicError = true,
        });

        var thrown = Assert.Throws<HttpProtocolErrorException>(() => stream.Read(new byte[1024]));

        // The server's own words, rather than a byte count nobody can act on.
        Assert.Contains(FakeServer.SubsonicErrorMessage, thrown.Message);
    }

    // The rule is about the content type, not about Subsonic: any textual body
    // under a 2xx on this surface is an error wearing a success. A captive
    // portal's sign-in page was decoded just as eagerly.
    [Fact]
    public async Task A_sign_in_page_on_a_200_is_not_read_as_audio()
    {
        var content = Content(20_000);
        var (stream, _) = await OpenAsync(content, server: new FakeServer(content)
        {
            RefusesBodiesWithHtml = true,
        });

        var thrown = Assert.Throws<HttpProtocolErrorException>(() => stream.Read(new byte[1024]));

        Assert.Contains("text/html", thrown.Message);
    }

    // A protocol error is not a connection that dropped, so retrying produces
    // the same error three times and then reports a dead track. Failing at once
    // is both faster and the only way the reason survives into the log.
    [Fact]
    public async Task A_protocol_error_is_not_retried()
    {
        var content = Content(20_000);
        var server = new FakeServer(content) { RefusesBodiesWithSubsonicError = true };
        var (stream, _) = await OpenAsync(content, server: server);

        var beforeRead = server.Requests;
        Assert.Throws<HttpProtocolErrorException>(() => stream.Read(new byte[1024]));

        Assert.Equal(1, server.Requests - beforeRead);
    }

    // The counterpart, so the check above cannot be satisfied by refusing
    // everything: a server sending actual audio under a 200 is still audio,
    // whatever its Accept-Ranges say.
    [Fact]
    public async Task An_ordinary_body_is_still_read_as_audio()
    {
        var content = Content(20_000);
        var (stream, _) = await OpenAsync(content);

        Assert.Equal(content, ReadFully(stream, content.Length));
    }
}
