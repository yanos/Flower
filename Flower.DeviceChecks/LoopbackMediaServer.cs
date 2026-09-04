using System;
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

    public string UrlFor(string path) => $"http://127.0.0.1:{Port}/{path}";

    public string Serve(byte[] content, string path = "rest/stream?id=abc")
    {
        _content = content;
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

    private sealed record Request(string Method, long RangeFrom, long? RangeTo, bool HasRange);

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
        var method = lines[0].Split(' ')[0];

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

            return new Request(method, offset, last, HasRange: true);
        }

        return new Request(method, 0, null, HasRange: false);
    }

    private void Respond(NetworkStream stream, Request request)
    {
        var content = _content;

        if (request.Method == "HEAD" && !AnswersHead)
        {
            var refusal = Encoding.ASCII.GetBytes("HTTP/1.1 405 Method Not Allowed\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            stream.Write(refusal, 0, refusal.Length);
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
