using System;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Flower.Persistence;

namespace Flower.Services;

// Plain-HTTP identity endpoint for the sync protocol phase 1 (see SYNC-PLAN.md):
// GET /api/localsend/v2/info returns this device's alias/fingerprint, mirroring
// LocalSend's own protocol shape (deliberately plain HTTP for now, not HTTPS - see
// the plan doc for why). No file transfer yet; that needs more endpoints and TLS,
// both deferred.
public class SyncHttpServer : IDisposable
{
    public const int DefaultPort = 53317;
    private const int MaxPortAttempts = 10;

    private HttpListener? _listener;
    private readonly string _alias;
    private readonly string _fingerprint;
    private CancellationTokenSource? _cts;

    // The port actually bound once Start() succeeds - may differ from DefaultPort,
    // see Start(). Null if binding failed on every port tried (sync unavailable).
    public int? BoundPort { get; private set; }

    public SyncHttpServer(string alias)
    {
        _alias = alias;
        _fingerprint = new DeviceIdentityStore().Load().Fingerprint;
    }

    // Tries DefaultPort first, then a handful of ports after it. Covers testing
    // desktop + the iOS Simulator side by side on the same Mac, where the Simulator
    // shares the host's actual TCP port namespace (same root cause as it sharing the
    // hostname - see NetworkDiscoveryService) and a fixed port would always collide.
    // On real separate devices the first attempt always succeeds. The actual bound
    // port is advertised via mDNS (see NetworkDiscoveryService.Start), so peers never
    // need to assume DefaultPort - they read it from the discovery answer instead.
    //
    // Wildcard binding ("+") needs no admin rights on macOS/Linux/mobile, but on
    // Windows it requires a one-time "netsh http add urlacl" reservation (or running
    // elevated) - a known gap, not yet handled here.
    public void Start()
    {
        _cts = new CancellationTokenSource();

        for (var port = DefaultPort; port < DefaultPort + MaxPortAttempts; port++)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://+:{port}/");
            try
            {
                listener.Start();
            }
            catch (HttpListenerException)
            {
                continue;
            }

            _listener = listener;
            BoundPort = port;
            _ = ListenLoopAsync(_cts.Token);
            return;
        }

        Console.WriteLine(
            $"[SyncHttpServer] Could not bind any port in {DefaultPort}..{DefaultPort + MaxPortAttempts - 1}, sync endpoint disabled.");
    }

    private async Task ListenLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener!.GetContextAsync();
            }
            catch (Exception)
            {
                return; // Listener stopped/disposed.
            }

            _ = HandleRequestAsync(context);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        try
        {
            if (context.Request.Url?.AbsolutePath == "/api/localsend/v2/info" && context.Request.HttpMethod == "GET")
            {
                var deviceType = OperatingSystem.IsIOS() || OperatingSystem.IsAndroid() ? "mobile" : "desktop";
                var body = JsonSerializer.Serialize(new
                {
                    alias = _alias,
                    version = "2.0",
                    deviceModel = (string?)null,
                    deviceType,
                    fingerprint = _fingerprint,
                    download = false
                });
                var bytes = Encoding.UTF8.GetBytes(body);
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
            }
            else
            {
                context.Response.StatusCode = 404;
            }
        }
        catch
        {
            // Client disconnected mid-response or similar - nothing to recover.
        }
        finally
        {
            context.Response.Close();
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        if (_listener?.IsListening == true)
            _listener.Stop();
    }

    public void Dispose()
    {
        Stop();
        _listener?.Close();
        _cts?.Dispose();
    }
}
