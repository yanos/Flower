using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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

        var (length, acceptsRanges) = await ProbeServerAsync(_client, _uri, cancellationToken);
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

            var (length, acceptsRanges) = ProbeServerAsync(_client, _uri, CancellationToken.None).GetAwaiter().GetResult();
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

    private static async Task<(long Length, bool AcceptsRanges)> ProbeServerAsync(HttpClient client, Uri uri, CancellationToken cancellationToken)
    {
        using var head = new HttpRequestMessage(HttpMethod.Head, uri);
        using var response = await client.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var length = response.Content.Headers.ContentLength ?? 0;
            if (length > 0)
                return (length, response.Headers.AcceptRanges.Contains("bytes"));
        }

        // The fallback: ask for one byte. A server that honours it answers 206
        // with a Content-Range whose total is the length, which settles both
        // questions at once and costs one byte of body.
        using var probe = new HttpRequestMessage(HttpMethod.Get, uri);
        probe.Headers.Range = new RangeHeaderValue(0, 0);
        using var probed = await client.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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

        var request = new HttpRequestMessage(HttpMethod.Get, _uri);
        if (EnsureProbed().CanSeek && position > 0)
            request.Headers.Range = new RangeHeaderValue(position, null);

        var response = _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
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
