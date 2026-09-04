using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Flower.DeviceChecks;

// A one-file HTTP server on loopback, standing in for the Subsonic /stream
// endpoint.
//
// Hand-rolled on a TcpListener rather than built on HttpListener, which is the
// obvious choice and is not available on iOS - and iOS is the whole reason
// these checks exist as something that can run somewhere other than a
// developer's desktop. What it serves is deliberately the narrow slice a
// decoder actually exercises: HEAD for the length, GET, one open-ended byte
// range, and the two failure shapes that have caused real bugs - a server that
// refuses ranges, and a body that stops early.
public sealed class LoopbackMediaServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();

    private byte[] _content = [];

    public LoopbackMediaServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptAsync);
    }

    public int Port { get; }

    public bool ServesRanges { get; set; } = true;

    // Flower.Server maps /rest/stream with MapGet, so a HEAD to it is a 405
    // and every real stream reaches the length through the ranged-GET probe
    // instead. A loopback that answers HEAD takes that whole path out of the
    // check, which is how it stayed untested while it was the one being used.
    public bool AnswersHead { get; set; } = true;

    // Cuts every response body short at this offset, standing in for a
    // connection that goes away mid-track.
    public long? CutBodyAt { get; set; }

    // Refuses this many *body* requests with 429 before serving anything,
    // standing in for a server whose per-source budget is spent - which is not
    // a hypothetical shape: an album grid's cover-art burst spent the shared
    // budget on Flower.Server and every /rest/stream body GET for the album
    // came back 429 for the next minute. The client used to read that as a
    // dead track and skip it, so an album disappeared because some pictures
    // were being fetched.
    //
    // Body requests only. The bytes=0-0 probe is cheap enough that it kept
    // getting through in the real failure, which is precisely why the symptom
    // was "the track exists and has a length and plays nothing" rather than
    // anything that looked like being throttled.
    public int RefuseBodiesWith429 { get; set; }

    // How long the 429s claim they will last. Zero sends no Retry-After at
    // all, which is the other case a client has to survive.
    public int RetryAfterSeconds { get; set; } = 1;

    // Requires every request to carry an X-Flower-Nonce - header or query
    // param - that this server has not seen before, exactly as
    // NonceReplayGuard does on the real one, and answers a repeat with the
    // Subsonic error envelope the real one sends.
    //
    // This is the shape that made a whole album unplayable on a phone while
    // every desktop test stayed green, and it stayed green because nothing
    // here authenticated anything: the checks proved a decoder could turn a
    // stream into audio, and the failure was a request never reaching the
    // decoder at all. A stream URL was signed once and then fetched several
    // times - probe, body, reopen - so the probe spent the nonce, the body GET
    // was refused as a replay, and the refusal arrived as HTTP 200 with about
    // 130 bytes of JSON that went straight into the decoder as audio.
    //
    // Both halves of that are worth reproducing on the platform rather than
    // only in a unit test: that the client now signs per request, and that a
    // 200 which is not audio is refused rather than decoded.
    public bool RequiresFreshNonce { get; set; }

    private readonly HashSet<string> _seenNonces = [];
    private readonly Lock _nonceLock = new();

    private int _refusalsLeft;

    public string UrlFor(string path) => $"http://127.0.0.1:{Port}/{path}";

    public string Serve(byte[] content, string path = "rest/stream?id=abc")
    {
        _content = content;
        _refusalsLeft = RefuseBodiesWith429;
        lock (_nonceLock)
        {
            _seenNonces.Clear();
        }

        return UrlFor(path);
    }

    // A port nothing is listening on, for the "the server is not there" check.
    public static int ClosedPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task AcceptAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stopping.Token);
            }
            catch
            {
                return;
            }

            _ = Task.Run(() => Handle(client));
        }
    }

    private void Handle(TcpClient client)
    {
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                // Connections are not reused here, so one request per socket
                // is the whole protocol that needs supporting.
                var request = ReadRequestHead(stream);
                if (request == null)
                    return;

                Respond(stream, request);
            }
        }
        catch
        {
            // A client that hangs up mid-body is the normal case: a decoder
            // closes its stream the moment it is retired.
        }
    }

    private sealed record Request(string Method, long RangeFrom, long? RangeTo, bool HasRange, string? Nonce);

    private static Request? ReadRequestHead(NetworkStream stream)
    {
        var head = new StringBuilder();
        var one = new byte[1];

        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (stream.Read(one, 0, 1) <= 0)
                return null;

            head.Append((char)one[0]);
            if (head.Length > 8192)
                return null;
        }

        var lines = head.ToString().Split("\r\n");
        var requestLine = lines[0].Split(' ');
        var method = requestLine[0];

        // Either transport, the same way the real server accepts them (see
        // PeerSignatureAuth.Identity): a header wins, a query param is the
        // fallback for a URL that cannot carry headers.
        var nonce = NonceHeader(lines) ?? NonceParam(requestLine.Length > 1 ? requestLine[1] : "");

        foreach (var line in lines)
        {
            if (!line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                continue;

            var spec = line["Range:".Length..].Trim();
            if (!spec.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                continue;

            var bounds = spec["bytes=".Length..].Split('-');
            if (!long.TryParse(bounds[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset))
                continue;

            // The end of the range matters. Serving the whole file to a
            // "bytes=0-0" probe is what a real server never does, and a
            // loopback that did it decoded a track the probe alone had
            // already delivered - so the check passed and the phone, talking
            // to a server that honours the bound, played nothing.
            long? last = bounds.Length > 1
                && long.TryParse(bounds[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;

            return new Request(method, offset, last, HasRange: true, nonce);
        }

        return new Request(method, 0, null, HasRange: false, nonce);
    }

    private const string NonceName = "X-Flower-Nonce";

    private static string? NonceHeader(string[] lines)
    {
        foreach (var line in lines)
        {
            if (line.StartsWith(NonceName + ":", StringComparison.OrdinalIgnoreCase))
                return line[(NonceName.Length + 1)..].Trim();
        }

        return null;
    }

    private static string? NonceParam(string target)
    {
        var query = target.IndexOf('?');
        if (query < 0)
            return null;

        foreach (var pair in target[(query + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator > 0 && pair.AsSpan(0, separator).Equals(NonceName, StringComparison.OrdinalIgnoreCase))
                return pair[(separator + 1)..];
        }

        return null;
    }

    // First use is accepted, every repeat refused - NonceReplayGuard.TryRecord
    // is a TryAdd, and this is the same statement. A request with no nonce at
    // all is refused too: that is an unauthenticated caller, not a fresh one.
    private bool AcceptNonce(string? nonce)
    {
        if (string.IsNullOrEmpty(nonce))
            return false;

        lock (_nonceLock)
        {
            return _seenNonces.Add(nonce);
        }
    }

    private void Respond(NetworkStream stream, Request request)
    {
        var content = _content;

        // HTTP 200, not 401 - and that is the whole point of reproducing it.
        // The Subsonic protocol carries its errors in the body of a success,
        // so a client checking the status code learns nothing, and a client
        // reading the body as audio decodes an error message.
        if (RequiresFreshNonce && !AcceptNonce(request.Nonce))
        {
            var envelope = Encoding.UTF8.GetBytes(
                "{\"subsonic-response\":{\"status\":\"failed\",\"version\":\"1.16.1\","
                + "\"error\":{\"code\":40,\"message\":\"Wrong username or password.\"}}}");
            var refusal = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: application/json; charset=utf-8\r\n"
                + string.Create(CultureInfo.InvariantCulture, $"Content-Length: {envelope.Length}\r\n")
                + "Connection: close\r\n\r\n");
            stream.Write(refusal, 0, refusal.Length);
            if (request.Method != "HEAD")
                stream.Write(envelope, 0, envelope.Length);
            stream.Flush();
            return;
        }

        if (request.Method == "HEAD" && !AnswersHead)
        {
            var refusal = Encoding.ASCII.GetBytes("HTTP/1.1 405 Method Not Allowed\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            stream.Write(refusal, 0, refusal.Length);
            stream.Flush();
            return;
        }

        // A one-byte probe is not a body, and is deliberately let through -
        // see RefuseBodiesWith429.
        var isProbe = request.HasRange && request.RangeFrom == 0 && request.RangeTo == 0;
        if (request.Method == "GET" && !isProbe && _refusalsLeft > 0)
        {
            _refusalsLeft--;
            var retryAfter = RetryAfterSeconds > 0
                ? $"Retry-After: {RetryAfterSeconds}\r\n"
                : "";
            var throttled = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 429 Too Many Requests\r\n{retryAfter}Content-Length: 0\r\nConnection: close\r\n\r\n");
            stream.Write(throttled, 0, throttled.Length);
            stream.Flush();
            return;
        }

        var ranged = ServesRanges && request.HasRange;
        var from = ranged ? request.RangeFrom : 0;
        if (from > content.Length)
            from = content.Length;

        var last = ranged && request.RangeTo is { } bound ? Math.Min(bound, content.Length - 1) : content.Length - 1;
        var length = last - from + 1;
        if (length < 0)
            length = 0;

        var headers = new StringBuilder();
        headers.Append(ranged ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
        headers.Append("Content-Type: application/octet-stream\r\n");
        headers.Append(CultureInfo.InvariantCulture, $"Content-Length: {(request.Method == "HEAD" ? content.Length : length)}\r\n");
        if (ServesRanges)
            headers.Append("Accept-Ranges: bytes\r\n");
        if (ranged)
            headers.Append(CultureInfo.InvariantCulture, $"Content-Range: bytes {from}-{last}/{content.Length}\r\n");
        headers.Append("Connection: close\r\n\r\n");

        var encoded = Encoding.ASCII.GetBytes(headers.ToString());
        stream.Write(encoded, 0, encoded.Length);

        if (request.Method == "HEAD")
        {
            stream.Flush();
            return;
        }

        if (CutBodyAt is { } cut)
        {
            var cutLength = (int)Math.Max(0, Math.Min(length, cut - from));
            stream.Write(content, (int)from, cutLength);
            stream.Flush();
            return;
        }

        stream.Write(content, (int)from, (int)length);
        stream.Flush();
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _listener.Stop();
        _stopping.Dispose();
    }
}
