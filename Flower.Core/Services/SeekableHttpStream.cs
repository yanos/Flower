using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Flower.Logging;

namespace Flower.Services;

// A remote track, presented to a decoder as an ordinary seekable Stream.
//
// Flower does not hand a URL to its decoder any more, it hands one of these.
// Two problems went away with that change and neither of them is about
// convenience:
//
// **Seeking.** VLC's mp4 demuxer refuses outright to work on a stream it
// cannot seek - "MP4 plugin discarded (not seekable)" - whatever the file's
// box layout is. On desktop that is invisible because VLC quietly falls back
// to libavformat's demuxer; on iOS the fallback did not save it, and an entire
// AAC album produced no audio at all while the queue raced through it in
// twenty seconds. The server has served byte ranges the whole time
// (enableRangeProcessing on the Subsonic /stream endpoint). Nothing was wrong
// with either end - the platform's own HTTP access module simply decided the
// stream was not seekable, and there was no way to argue with it. Owning the
// fetching settles the question rather than negotiating it.
//
// **Authentication.** LibVLC opening its own https URL means LibVLC's TLS
// stack, which knows nothing about the key this device paired with - the gap
// VlcCertificateDialogs exists to paper over. Reading through PeerHttpClient
// means audio is fetched by the same pinned client as every other request.
//
// **Every HTTP call here is the async one, blocked on.** That looks like
// something to tidy up and is not: .NET's mobile HttpClientHandler has no
// synchronous path at all - Send and ReadAsStream throw
// PlatformNotSupportedException on iOS and Android outright, whichever
// underlying handler is configured, so Flower.iOS's own
// UseNativeHttpHandler=false does not buy it back. The first build of this
// class used them, passed every desktop test, and made *every* streamed track
// on the phone unplayable - each read failed instantly with "Operation is not
// supported on this platform" and the queue skipped the whole album. Blocking
// on the async call is safe: these run on a decoder's own reading thread,
// which has no synchronization context to deadlock against, never the UI
// thread. Flower.DeviceChecks is what now runs this code on the platform it
// broke on.
//
// It is deliberately built on plain range requests and nothing else, because
// the thing that reads it next is FFmpeg's AVIOContext, whose read/seek
// callbacks are this class's Read and Seek with different signatures. See
// docs/AUDIOPHILE-PLAN.md #5.
public sealed class SeekableHttpStream : Stream
{
    // Seeking backwards, or far forwards, costs a new request. Seeking a
    // little way forwards is what a demuxer does constantly while parsing -
    // skipping a box it does not care about - and answering those by reading
    // and discarding is far cheaper than a round trip. The bound is what fits
    // in one buffer's worth of reads rather than a tuned number.
    private const int MaxForwardSkipBytes = 512 * 1024;

    // A stream cut mid-track is the ordinary case on a phone changing
    // networks, not an exceptional one. Reopening at the current offset is
    // exactly what a range request is for.
    private const int MaxReopenAttempts = 3;

    private const int RetryBackoffMs = 250;

    // Being throttled is not the stream failing. 429 means "ask again later",
    // and the difference between honouring that and treating it as an I/O
    // error is the difference between a track that stalls and a track the
    // queue declares dead - which is what used to happen: a cover-art burst
    // spent the server's shared per-source budget, every body GET for the
    // album came back 429, this class reopened three times (spending more of
    // an exhausted budget), the decoder faulted, the queue skipped to the next
    // track, and five of those in a row stopped playback altogether. An entire
    // album vanished because some pictures were being fetched.
    //
    // So a 429 is waited out rather than retried against, it never latches
    // _broken, and Retry-After is believed when the server sends one. The
    // budget below bounds the wait: a server that is throttling this hard for
    // two minutes is a problem the caller should hear about, but it is still
    // never grounds for calling the stream unrecoverable.
    private static readonly TimeSpan FirstThrottleWait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxThrottleWait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DefaultThrottleWaitBudget = TimeSpan.FromSeconds(120);

    // Only ever moved by tests, which otherwise have to spend the real budget
    // in real seconds to see what happens at the end of it. Per-instance
    // rather than a static so setting it cannot leak into another test.
    internal TimeSpan ThrottleWaitBudget { get; set; } = DefaultThrottleWaitBudget;

    private readonly HttpClient _client;
    private readonly Uri _uri;
    private readonly ILogger? _logger;
    private readonly bool _ownsClient;

    private readonly Lock _probeLock = new();

    private bool _broken;

    // What the server said, published as one object rather than as a flag
    // beside two fields. The probe happens on whichever thread got there first
    // - a decode-ahead prepare, or LibVLC's reading thread - and is read from
    // the other, so a "have we probed yet" bool set after the values it
    // describes can be seen true while they are still stale. One reference,
    // written once, removes the question.
    private ServerFacts? _facts;

    private sealed record ServerFacts(long Length, bool CanSeek);

    private Stream? _body;
    private long _bodyPosition;
    private long _position;
    private bool _disposed;

    // Construction costs nothing and asks the server nothing.
    //
    // That matters because of who opens these: LibVLC calls MediaInput.Open on
    // its own thread when it opens the media, and FFmpeg's AVIOContext is the
    // same shape. Probing in the constructor would put a network round trip on
    // whichever thread happened to build the decoder - the UI thread, for a
    // track the user just double-clicked. Probing on first use puts it where
    // the reading already is.
    public SeekableHttpStream(HttpClient client, Uri uri, bool ownsClient = false, ILogger? logger = null)
    {
        _client = client;
        _uri = uri;
        _logger = logger;
        _ownsClient = ownsClient;
    }

    // For callers that would rather find out now whether the track is
    // reachable - a decode-ahead prepare, say - than at the first read.
    public static async Task<SeekableHttpStream> OpenAsync(
        HttpClient client,
        Uri uri,
        bool ownsClient = false,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var stream = new SeekableHttpStream(client, uri, ownsClient, logger);
        await stream.ProbeAsync(cancellationToken);
        return stream;
    }

    // Finds out now, off the caller's thread, what the first read would
    // otherwise find out later: whether the server answers at all, and how big
    // the track is. A decode-ahead prepare wants that answer before it commits
    // to arming the track - see TrackDecoder.PrepareAsync.
    public async Task ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _facts) != null)
            return;

        var (length, acceptsRanges) = await ProbeServerAsync(_client, _uri, _logger, ThrottleWaitBudget, cancellationToken);
        Adopt(length, acceptsRanges);
    }

    private ServerFacts EnsureProbed()
    {
        if (Volatile.Read(ref _facts) is { } known)
            return known;

        lock (_probeLock)
        {
            if (Volatile.Read(ref _facts) is { } raced)
                return raced;

            var (length, acceptsRanges) = ProbeServerAsync(_client, _uri, _logger, ThrottleWaitBudget, CancellationToken.None).GetAwaiter().GetResult();
            return Adopt(length, acceptsRanges);
        }
    }

    private ServerFacts Adopt(long length, bool acceptsRanges)
    {
        lock (_probeLock)
        {
            if (Volatile.Read(ref _facts) is { } raced)
                return raced;

            Volatile.Write(ref _facts, new ServerFacts(length, acceptsRanges && length > 0));
        }

        if (!acceptsRanges)
        {
            // Worth a line: this is the condition that produced the original
            // bug, and if a server ever stops advertising ranges the symptom
            // will be an unplayable m4a rather than anything that says so.
            _logger?.LogWarning(
                "{Uri} does not advertise byte ranges; the stream will not be seekable and container formats that need it may refuse to play",
                LogPath.Short(_uri.ToString()));
        }

        return _facts!;
    }

    // Every request this class makes goes through here, so there is one place
    // that knows what a 429 means. A throttled request is re-sent after the
    // wait the server asked for - not counted as an attempt, not retried
    // against, and never turned into a broken stream.
    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        Uri uri,
        Func<HttpRequestMessage> build,
        ILogger? logger,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        var waited = TimeSpan.Zero;
        var wait = FirstThrottleWait;

        while (true)
        {
            var request = build();
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode != HttpStatusCode.TooManyRequests)
            {
                if (await ProtocolErrorFor(response, cancellationToken) is { } complaint)
                {
                    response.Dispose();
                    throw new HttpProtocolErrorException(
                        $"{LogPath.Short(uri.ToString())} answered {complaint}");
                }

                return response;
            }

            var asked = RetryAfter(response);
            response.Dispose();

            // A named delay is believed, only capped: a server that knows its
            // own window is a better source than a guess, and one that says
            // "come back in an hour" still must not park a decoder for an
            // hour. Absent a header there is nothing to believe, so the guess
            // starts at a second and doubles - never a hot retry loop against
            // a server that has just said it is overloaded.
            var pause = TimeSpan.FromMilliseconds(Math.Min(
                (asked ?? wait).TotalMilliseconds, MaxThrottleWait.TotalMilliseconds));
            if (pause < TimeSpan.Zero)
                pause = TimeSpan.Zero;

            if (waited + pause > budget)
            {
                throw new HttpThrottledException(
                    $"{LogPath.Short(uri.ToString())} has been rate limited for {waited.TotalSeconds:F0}s and is still refusing requests");
            }

            logger?.LogWarning(
                "{Uri} answered 429; waiting {Wait:F1}s before asking again ({Waited:F0}s so far)",
                LogPath.Short(uri.ToString()), pause.TotalSeconds, waited.TotalSeconds);

            await Task.Delay(pause, cancellationToken);
            waited += pause;
            wait = TimeSpan.FromMilliseconds(Math.Min(wait.TotalMilliseconds * 2, MaxThrottleWait.TotalMilliseconds));
        }
    }

    // How much of a textual response is worth reading to quote it back. An
    // error envelope is a couple of hundred bytes; anything beyond this is not
    // going to become a better log line.
    private const int MaxProtocolErrorBytes = 2048;

    // "This is a 200, and it is still not audio."
    //
    // The Subsonic protocol answers a failed request with HTTP 200 and an error
    // envelope in the body - "Wrong username or password" is a 200 - so
    // EnsureSuccessStatusCode is not, on this surface, a check that anything
    // went right. Without this, a refusal became roughly 130 bytes of JSON
    // handed to a decoder as though it were the start of the track: the stream
    // then ended far short of the length the probe had just established, which
    // read as a cut connection, which reopened, which got the same JSON again.
    // What the listener saw was an album skipping through itself in seconds,
    // and what every log showed was a successful probe.
    //
    // Keyed on the content type rather than on parsing the body, because the
    // rule is broader than Subsonic and does not depend on a shape: a success
    // on /rest/stream is audio bytes, so any textual body under a 2xx is an
    // error being mistaken for one - a proxy's HTML sign-in page and a
    // captive portal land here too, and used to be decoded just as eagerly.
    private static async Task<string?> ProtocolErrorFor(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
            return null;

        if (response.Content.Headers.ContentType?.MediaType is not { } mediaType || !IsTextual(mediaType))
            return null;

        var body = await ReadPrefixAsync(response, cancellationToken);
        var detail = SubsonicErrorMessage(body) ?? Summarize(body);
        return detail.Length > 0
            ? $"{(int)response.StatusCode} {mediaType} rather than audio: {detail}"
            : $"{(int)response.StatusCode} {mediaType} rather than audio";
    }

    private static bool IsTextual(string mediaType) =>
        mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("application/xml", StringComparison.OrdinalIgnoreCase)
        || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)
        || mediaType.EndsWith("+xml", StringComparison.OrdinalIgnoreCase);

    private static async Task<string> ReadPrefixAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[MaxProtocolErrorBytes];
            var filled = 0;

            while (filled < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(filled), cancellationToken);
                if (read <= 0)
                    break;

                filled += read;
            }

            return Encoding.UTF8.GetString(buffer, 0, filled);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            // The status and content type are the finding; the body was only
            // ever going to make the message nicer.
            return "";
        }
    }

    // Pulls the human-readable half out of a Subsonic error envelope, in either
    // of the two encodings the protocol defines:
    //   {"subsonic-response":{"status":"failed","error":{"code":40,"message":"..."}}}
    //   <subsonic-response status="failed"><error code="40" message="..."/>
    // Hand-rolled rather than parsed, because this runs on a failure path in
    // the shared library and the answer is one string.
    private static string? SubsonicErrorMessage(string body)
    {
        foreach (var opening in (string[])["\"message\":\"", "message=\""])
        {
            var at = body.IndexOf(opening, StringComparison.Ordinal);
            if (at < 0)
                continue;

            var from = at + opening.Length;
            var to = body.IndexOf('"', from);
            if (to > from)
                return body[from..to];
        }

        return null;
    }

    private static string Summarize(string body)
    {
        var collapsed = string.Join(' ', body.Split((char[])['\r', '\n', '\t', ' '],
            StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= 200 ? collapsed : collapsed[..200] + "...";
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is not { } header)
            return null;

        if (header.Delta is { } delta)
            return delta;

        // The date form. Measured against the server's own clock where it sent
        // one, because a phone's clock and a server's are not the same clock
        // and the difference is exactly the size of the mistake.
        if (header.Date is { } date)
            return date - (response.Headers.Date ?? DateTimeOffset.UtcNow);

        return null;
    }

    private static async Task<(long Length, bool AcceptsRanges)> ProbeServerAsync(HttpClient client, Uri uri, ILogger? logger, TimeSpan budget, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            client, uri, () => new HttpRequestMessage(HttpMethod.Head, uri), logger, budget, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var length = response.Content.Headers.ContentLength ?? 0;
            if (length > 0)
                return (length, response.Headers.AcceptRanges.Contains("bytes"));
        }

        // The fallback: ask for one byte. A server that honours it answers 206
        // with a Content-Range whose total is the length, which settles both
        // questions at once and costs one byte of body.
        using var probed = await SendAsync(client, uri, () =>
        {
            var probe = new HttpRequestMessage(HttpMethod.Get, uri);
            probe.Headers.Range = new RangeHeaderValue(0, 0);
            return probe;
        }, logger, budget, cancellationToken);
        probed.EnsureSuccessStatusCode();

        if (probed.StatusCode == HttpStatusCode.PartialContent && probed.Content.Headers.ContentRange is { Length: { } total })
            return (total, true);

        return (probed.Content.Headers.ContentLength ?? 0, false);
    }

    public override bool CanRead => true;
    public override bool CanWrite => false;

    public override bool CanSeek => EnsureProbed().CanSeek;
    public override long Length => EnsureProbed().Length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (buffer.IsEmpty)
            return 0;

        // Once the retries are spent, stay spent. Without this latch each
        // fresh Read starts its own run of attempts, and a caller that keeps
        // reading after a failure - LibVLC does, several times a second -
        // turns a dead connection into an unbounded storm of requests against
        // a server that is already in trouble. Measured on a fixture that
        // resets the connection every time: one decode, requests without end.
        if (_broken)
            throw new IOException($"The stream for {LogPath.Short(_uri.ToString())} failed at offset {_position} and was not recoverable");

        var facts = EnsureProbed();
        if (facts.Length > 0 && _position >= facts.Length)
            return 0;

        for (var attempt = 0; ; attempt++)
        {
            var lastAttempt = attempt >= MaxReopenAttempts;

            try
            {
                EnsureBodyAt(_position);
                var read = ReadBody(buffer);

                if (read > 0)
                {
                    _position += read;
                    _bodyPosition += read;
                    return read;
                }

                // A zero-length read is the end of the body. At the end of the
                // file that is the truth; anywhere else the connection was cut
                // and reopening is the answer, not reporting EOF to a decoder
                // that would take it as a finished track.
                if (facts.Length == 0 || _position >= facts.Length)
                    return 0;

                if (lastAttempt)
                    break;

                _logger?.LogWarning(
                    "Stream for {Uri} ended at {Position} of {Length} bytes; reopening from there",
                    LogPath.Short(_uri.ToString()), _position, facts.Length);
            }
            // Ahead of the general clause below, because it is the one
            // failure that must not spend an attempt or latch the stream. By
            // the time this is thrown the sender has already waited out its
            // whole budget, so retrying here would only add to a wait the
            // caller has been in for two minutes - but the stream is intact,
            // and a read a moment later may well succeed.
            catch (HttpThrottledException)
            {
                DropBody();
                throw;
            }
            // Also ahead of the general clause, for the opposite reason: this
            // is the one failure that must not be *retried* at all. A server
            // answering a protocol error is not a connection that dropped -
            // asking again produces the same error, three times, and then
            // reports a dead track for what is very often a fixable thing like
            // a credential the caller can refresh. Failing at once puts the
            // server's own words in the log instead.
            catch (HttpProtocolErrorException)
            {
                _broken = true;
                DropBody();
                throw;
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                if (lastAttempt)
                {
                    _broken = true;
                    DropBody();
                    throw;
                }

                _logger?.LogWarning(ex,
                    "Read of {Uri} failed at {Position}; reopening (attempt {Attempt} of {Max})",
                    LogPath.Short(_uri.ToString()), _position, attempt + 1, MaxReopenAttempts);
            }

            DropBody();

            // A connection that has just been reset is rarely ready again in
            // the same millisecond, and a hot retry loop is the shape that
            // takes a struggling server down rather than riding it out.
            Thread.Sleep(RetryBackoffMs);
        }

        _broken = true;
        throw new IOException($"The stream for {LogPath.Short(_uri.ToString())} stopped at {_position} of {facts.Length} bytes and could not be resumed");
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var facts = EnsureProbed();

        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => facts.Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        if (target < 0)
            throw new IOException("Cannot seek before the start of the stream");
        if (!facts.CanSeek && target != _position)
            throw new NotSupportedException("This server does not serve byte ranges, so the stream cannot be seeked");

        _position = target;
        return _position;
    }

    // Makes the open response body be the one that starts at `position`,
    // reusing the one already open when that is possible.
    //
    // Nothing here issues a request for a seek on its own: a demuxer that
    // seeks four times while probing and then reads should cost one request,
    // not four. The request happens on the first read after the seek, which is
    // why Seek above only moves a number.
    private void EnsureBodyAt(long position)
    {
        if (_body != null)
        {
            if (_bodyPosition == position)
                return;

            var skip = position - _bodyPosition;
            if (skip > 0 && skip <= MaxForwardSkipBytes && SkipForward(skip))
                return;

            DropBody();
        }

        var seekable = EnsureProbed().CanSeek;
        var response = SendAsync(_client, _uri, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, _uri);
            if (seekable && position > 0)
                request.Headers.Range = new RangeHeaderValue(position, null);
            return request;
        }, _logger, ThrottleWaitBudget, CancellationToken.None).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();

        // A server that answers a ranged request with 200 is serving from
        // zero regardless of what was asked, and reading it as though it
        // started at the offset would hand the decoder audio from the wrong
        // place. Discarding forward is correct and, on the first open of a
        // track, costs nothing because the offset is zero.
        if (position > 0 && response.StatusCode != HttpStatusCode.PartialContent)
        {
            _body = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
            _bodyPosition = 0;
            if (!SkipForward(position))
                throw new IOException($"Could not reach offset {position} in a response that ignored the range request");
            return;
        }

        _body = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
        _bodyPosition = position;
    }

    // The response body is read through ReadAsync for the same reason the
    // request is sent through SendAsync - see this class's header. It costs a
    // pooled array, because a Span cannot cross an await boundary even one
    // that is immediately blocked on.
    private int ReadBody(Span<byte> destination)
    {
        var scratch = ArrayPool<byte>.Shared.Rent(destination.Length);
        try
        {
            var read = _body!.ReadAsync(scratch, 0, destination.Length).GetAwaiter().GetResult();
            if (read > 0)
                scratch.AsSpan(0, read).CopyTo(destination);
            return read;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    private bool SkipForward(long count)
    {
        var scratch = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            return SkipForward(count, scratch);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    private bool SkipForward(long count, byte[] scratch)
    {
        while (count > 0)
        {
            var read = _body!.ReadAsync(scratch, 0, (int)Math.Min(count, scratch.Length)).GetAwaiter().GetResult();
            if (read <= 0)
                return false;

            count -= read;
            _bodyPosition += read;
        }

        return true;
    }

    private void DropBody()
    {
        _body?.Dispose();
        _body = null;
        _bodyPosition = -1;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            DropBody();
            if (_ownsClient)
                _client.Dispose();
        }

        base.Dispose(disposing);
    }
}

// A server that asked to be left alone, rather than one that broke. Separate
// from every other IOException so SeekableHttpStream.Read can tell the two
// apart in a catch clause - and an IOException at all so a caller that only
// knows about streams still handles it.
public sealed class HttpThrottledException(string message) : IOException(message);

// A response that arrived intact and is not the track: a Subsonic error
// envelope on its mandated HTTP 200, a proxy's sign-in page, a captive portal.
// An IOException so every existing caller treats it as a failed read rather
// than as a finished one, but distinct so SeekableHttpStream's own read loop
// can refuse to retry it - see the catch there.
public sealed class HttpProtocolErrorException(string message) : IOException(message);
