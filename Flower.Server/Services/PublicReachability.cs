using System.Net;
using System.Net.Sockets;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

using Flower.Server.Configuration;
using Flower.Services;

namespace Flower.Server.Services;

// Where the internet can reach this server, and whether it actually can.
//
// Turning on AllowPublicAccess used to open the door without telling anyone
// where it was: /info advertised only the addresses this machine holds, and a
// machine behind a router holds none that work from outside. So a phone paired
// on the LAN went on knowing only LAN addresses, and the moment it left WiFi it
// had nothing to dial - unless the operator also knew to copy the public
// address into AdvertisedHost, scheme and port included, which is what every
// operator got wrong at least once. This closes that gap for the common case:
// with the door open and no AdvertisedHost, the public address PublicAddressProbe
// finds is advertised as https://<it>:<https port>, on the assumption that the
// router forwards the same port it is forwarded to.
//
// That assumption is exactly what cannot be known from in here, so this also
// checks it: dial that origin's /info and see whether the fingerprint that
// answers is this server's own. A match proves the forward reaches *this*
// server, not merely something; any other answer, or none, is reported. One
// honest caveat, which the settings page repeats: the check leaves through the
// same router it comes back in by, and a router that cannot loop a connection
// back to itself ("hairpin NAT") fails it for a server the rest of the internet
// reaches fine. A failure is therefore a warning, never grounds to withhold the
// address - an address that turns out dead costs a client one failed probe.
//
// Not applied when AdvertisedHost is set. That operator has said where the
// server is reached (a tunnel, a proxy, a DNS name), and the home connection's
// address is at best a second way in they did not ask to publish and at worst
// not a way in at all.
public sealed class PublicReachability : BackgroundService
{
    // How often the address is looked up again while the door is open. The
    // same as the probe's own cache, so each pass is one lookup: a home
    // connection's address changes on the order of days, and a phone learns the
    // new one on its next /info - as long as it can still reach the server at
    // all, which is why this cannot usefully be slower.
    public static readonly TimeSpan RefreshEvery = PublicAddressProbe.CacheFor;

    // A failed check is worth repeating sooner than a passed one: the operator
    // looking at the warning is likely in the middle of fixing the router.
    private static readonly TimeSpan RecheckFailureAfter = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan DialTimeout = TimeSpan.FromSeconds(5);

    private readonly PublicAddressProbe _probe;
    private readonly IServer _server;
    private readonly DeviceSigningKey _signingKey;
    private readonly IOptionsMonitor<FlowerServerOptions> _options;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<PublicReachability> _logger;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private PublicReachabilityResult? _last;
    private DateTimeOffset _lastAt;

    public PublicReachability(
        PublicAddressProbe probe, IServer server, DeviceSigningKey signingKey,
        IOptionsMonitor<FlowerServerOptions> options, IHostApplicationLifetime lifetime,
        ILogger<PublicReachability> logger, HttpMessageHandler? handler = null)
    {
        _probe = probe;
        _server = server;
        _signingKey = signingKey;
        _options = options;
        _lifetime = lifetime;
        _logger = logger;

        // Any certificate at all: what is being checked is who answers, and
        // that is read from the fingerprint in the body, not from the chain. A
        // self-signed certificate does not name the public address anyway.
        _http = new HttpClient(handler ?? new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        })
        {
            Timeout = DialTimeout,
        };
    }

    // The origin to advertise, or null when there is none to: the door is
    // shut, an AdvertisedHost already says where the server is, TLS is off, or
    // the address has never been found out. Synchronous and never makes a
    // request, because /info answers every paired device every few seconds;
    // the lookup itself happens on this service's own timer.
    public string? OriginFor(FlowerServerOptions options)
    {
        if (!options.AllowPublicAccess || !string.IsNullOrWhiteSpace(options.AdvertisedHost))
            return null;

        if (HttpsPort() is not { } port || _probe.LastKnown is not { } address)
            return null;

        return Format(address, port);
    }

    // Whether OriginFor's origin answers as this server, for the settings page.
    // Null when there is nothing to check (OriginFor would say null). Cached,
    // so opening the page does not dial out each time; a failure is cached
    // briefly so a fixed router shows up on the next look.
    public async Task<PublicReachabilityResult?> CheckAsync(FlowerServerOptions options, CancellationToken ct = default)
    {
        if (!options.AllowPublicAccess || !string.IsNullOrWhiteSpace(options.AdvertisedHost))
            return null;

        if (HttpsPort() is not { } port)
            return null;

        if (await _probe.GetAsync(ct) is not { } address)
            return null;

        var origin = Format(address, port);

        await _gate.WaitAsync(ct);
        try
        {
            if (_last is { } cached && cached.Origin == origin &&
                DateTimeOffset.UtcNow - _lastAt < (cached.Status == PublicReachabilityStatus.Reachable ? RefreshEvery : RecheckFailureAfter))
            {
                return cached;
            }

            var result = new PublicReachabilityResult(origin, await DialAsync(origin, ct));
            if (result != _last)
                Log(result);

            _last = result;
            _lastAt = DateTimeOffset.UtcNow;
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The https port is only known once Kestrel has bound it.
        var started = new TaskCompletionSource();
        using (_lifetime.ApplicationStarted.Register(() => started.TrySetResult()))
        {
            try
            {
                await started.Task.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        // Woken early when the setting changes, so a server whose door was just
        // opened advertises its address on the next /info rather than a quarter
        // of an hour later.
        using var wake = new SemaphoreSlim(0);
        using var subscription = _options.OnChange(_ => wake.Release());

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // The only thing that makes this server contact a third party
                // on its own, and only once the operator has opened the door -
                // see PublicAddressProbe.
                await CheckAsync(_options.CurrentValue, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Checking whether this server is reachable from the internet failed.");
            }

            try
            {
                await wake.WaitAsync(RefreshEvery, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<PublicReachabilityStatus> DialAsync(string origin, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(origin + SyncProtocol.InfoPath, ct);
            if (!response.IsSuccessStatusCode)
                return PublicReachabilityStatus.OtherServerAnswered;

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var fingerprint = json.RootElement.TryGetProperty("fingerprint", out var value) ? value.GetString() : null;

            return fingerprint == _signingKey.Fingerprint
                ? PublicReachabilityStatus.Reachable
                : PublicReachabilityStatus.OtherServerAnswered;
        }
        catch (JsonException)
        {
            return PublicReachabilityStatus.OtherServerAnswered;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "No answer from {Origin}.", origin);
            return PublicReachabilityStatus.Unreachable;
        }
    }

    // Logged on a change only, so a server that stays reachable says so once.
    private void Log(PublicReachabilityResult result)
    {
        switch (result.Status)
        {
            case PublicReachabilityStatus.Reachable:
                _logger.LogInformation("Reachable from the internet at {Origin}; advertising it to clients.", result.Origin);
                break;
            case PublicReachabilityStatus.OtherServerAnswered:
                _logger.LogWarning(
                    "Something other than this server answered at {Origin}. The router may forward that port to another machine. Advertising it anyway.",
                    result.Origin);
                break;
            default:
                _logger.LogWarning(
                    "Could not reach this server at {Origin}. Check that the router forwards that port here - or it may not loop connections back to itself, in which case this is a false alarm. Advertising it anyway.",
                    result.Origin);
                break;
        }
    }

    private int? HttpsPort() =>
        MdnsAdvertiser.AdvertisablePort(
            _server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [], Uri.UriSchemeHttps);

    private static string Format(string address, int port) =>
        IPAddress.TryParse(address, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetworkV6
            ? $"https://[{address}]:{port}"
            : $"https://{address}:{port}";

    public override void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
        base.Dispose();
    }
}

public enum PublicReachabilityStatus
{
    Reachable,
    Unreachable,
    OtherServerAnswered,
}

public sealed record PublicReachabilityResult(string Origin, PublicReachabilityStatus Status);
