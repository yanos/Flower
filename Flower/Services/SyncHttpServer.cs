using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Flower.Models;
using Flower.Persistence;

namespace Flower.Services;

// Plain-HTTP sync endpoints (see SYNC-PLAN.md):
//   GET  /api/localsend/v2/info        - device identity (phase 1)
//   GET  /api/flower/v1/playlists      - this device's current playlist manifest
//   POST /api/flower/v1/playlists/apply - adopt a peer-merged manifest wholesale
// (phase 2, playlist metadata sync). Deliberately plain HTTP for now, not HTTPS -
// see the plan doc for why. No audio file transfer yet; playlists can only
// reference tracks already present on both sides (see PlaylistSyncMapper).
public class SyncHttpServer : IDisposable
{
    public const int DefaultPort = 53317;
    private const int MaxPortAttempts = 10;

    private HttpListener? _listener;
    private readonly string _alias;
    private readonly string _fingerprint;
    private readonly Library _library;
    private readonly PlaylistStore _playlistStore = new();
    private CancellationTokenSource? _cts;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // The port actually bound once Start() succeeds - may differ from DefaultPort,
    // see Start(). Null if binding failed on every port tried (sync unavailable).
    public int? BoundPort { get; private set; }

    public SyncHttpServer(string alias, Library library)
    {
        _alias = alias;
        _library = library;
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
            var path = context.Request.Url?.AbsolutePath;
            var method = context.Request.HttpMethod;

            if (path == "/api/localsend/v2/info" && method == "GET")
                await HandleInfoAsync(context);
            else if (path == "/api/flower/v1/playlists" && method == "GET")
                await HandleGetPlaylistsAsync(context);
            else if (path == "/api/flower/v1/playlists/apply" && method == "POST")
                await HandleApplyPlaylistsAsync(context);
            else
                context.Response.StatusCode = 404;
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

    private async Task HandleInfoAsync(HttpListenerContext context)
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
        await WriteJsonAsync(context, body);
    }

    private async Task HandleGetPlaylistsAsync(HttpListenerContext context)
    {
        var manifest = PlaylistSyncMapper.ToManifest(_fingerprint, _library.Playlists);
        await WriteJsonAsync(context, JsonSerializer.Serialize(manifest, JsonOptions));
    }

    // The initiator of a sync session (see PlaylistSyncService) has already resolved
    // every conflict by the time it POSTs here, so this side just replaces its whole
    // playlist collection to match - no merge logic runs on this end.
    private async Task HandleApplyPlaylistsAsync(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        var json = await reader.ReadToEndAsync();
        var manifest = JsonSerializer.Deserialize<PlaylistSyncManifestDto>(json, JsonOptions);
        if (manifest == null)
        {
            context.Response.StatusCode = 400;
            return;
        }

        var playlists = manifest.Playlists
            .Select(dto => PlaylistSyncMapper.ToPlaylist(dto, _library.Tracks))
            .ToList();
        _library.ReplacePlaylists(playlists);
        await _playlistStore.SaveAsync(playlists);

        context.Response.StatusCode = 204;
    }

    private static async Task WriteJsonAsync(HttpListenerContext context, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
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
