using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Flower.Tests.TestSupport;

// Minimal real HTTP server standing in for a peer's embedded SyncHttpServer/
// OpenSubsonic host (LibraryDownloadServiceTests) or a plain network audio
// stream (StreamingNetworkOutageTests). Exists because those tests need real
// socket-level failure modes - a connection that's flat-out refused, or one
// that opens and then drops mid-response - which OpenSubsonicClientTests'
// fake HttpMessageHandler can't produce, since that never touches a real
// socket at all.
//
// Binds a free port from a small range, skipping any already taken by
// something else on the test machine rather than failing the test outright.
//
// Where it walks the range from matters. It used to start at BasePort every
// time, which meant the lowest port was re-bound the instant the previous
// server released it - so consecutive servers were usually handed the *same*
// port. A request built against a disposed server's port (a retry still in
// flight, a stream URL handed to something that fetched it late) then landed
// on the next test's server and was recorded as if that test had made it,
// which is exactly how StreamTicketUrlResolverTests saw a stray "/rest/stream"
// arrive at a server only ever asked for tickets. Rotating the starting point
// means a released port is not offered again until the whole range has been
// walked, by which time the stragglers are long gone.
public sealed class FakePeerHttpServer : IDisposable
{
    private const int BasePort = 48700;
    private const int MaxPortAttempts = 50;
    private const int UnboundBasePort = BasePort + MaxPortAttempts;

    // How many servers have been created. Shared across every instance and
    // only ever moved forward, so the search starts at a different port each
    // time and ports are handed out round-robin rather than lowest-first. See
    // the class remarks.
    private static int s_created;

    private readonly HttpListener _listener;
    private readonly Func<HttpListenerContext, Task> _handle;
    private readonly CancellationTokenSource _cts = new();

    public int Port { get; }

    public FakePeerHttpServer(Func<HttpListenerContext, Task> handle)
    {
        _handle = handle;

        var start = Interlocked.Increment(ref s_created);
        for (var attempt = 0; attempt < MaxPortAttempts; attempt++)
        {
            var port = BasePort + (start + attempt) % MaxPortAttempts;
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
            }
            catch (HttpListenerException)
            {
                continue;
            }

            _listener = listener;
            Port = port;
            _ = AcceptLoopAsync();
            return;
        }

        throw new InvalidOperationException($"Could not bind any port in {BasePort}..{BasePort + MaxPortAttempts - 1}");
    }

    // A port nothing is listening on - used by tests simulating a peer that's
    // simply not there (connection refused), as opposed to one that's there
    // but misbehaving.
    //
    // Deliberately drawn from a range disjoint from the one live servers bind
    // (BasePort..BasePort+MaxPortAttempts): tests run in parallel, so handing
    // back a port a live server could bind the instant we release it made the
    // "connection refused" tests intermittently hit another test's server and
    // parse its response body instead.
    public static int GetUnboundPort()
    {
        for (var port = UnboundBasePort; port < UnboundBasePort + MaxPortAttempts; port++)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
            }
            catch (HttpListenerException)
            {
                continue;
            }

            // Held only long enough to prove the port is free, then released
            // so a connect to it is refused.
            listener.Close();
            return port;
        }

        throw new InvalidOperationException($"Could not find a free port in {UnboundBasePort}..{UnboundBasePort + MaxPortAttempts - 1}");
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch
            {
                return;
            }

            _ = _handle(context);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _listener.Close();
    }
}
