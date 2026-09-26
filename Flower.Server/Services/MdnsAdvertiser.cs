using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

using Flower.Server.Configuration;
using Flower.Services;

namespace Flower.Server.Services;

// Announces this server as _flowersync._tcp so a Flower client's sidebar can
// find it without anyone typing an address (SYNC-PLAN.md). Discovery is
// convenience only: appearing here gets the server a row and an address, and
// nothing else - the row is untrusted until a device redeems a pairing code,
// which is what actually carries the fingerprint pin. See PairingInvite.
//
// It uses the same MakaretuMdnsBackend the client browses with (moved into
// Flower.Core for exactly this), so the two cannot drift on the record shape.
//
// The name is claimed, not just announced. A client keys the servers it
// discovers by instance name (NetworkDiscoveryService), so two servers
// announcing the same one - a forgotten dev instance, two boxes left on their
// defaults, a rename onto a name already in use - showed as a single row that
// flipped between the two machines, and a pairing code typed into it went to
// whichever one it pointed at just then. Makaretu, unlike Bonjour, does no
// conflict resolution of its own. So before announcing, this listens for a
// moment, and if a live Flower server with a different fingerprint already
// answers to the name, takes "Name (2)" instead - the Bonjour convention - and
// /info reports that name too (Name), so a client's label agrees with its row.
//
// Only a server that answers counts. A record nothing answers for is stale, and
// one that answers with this server's own fingerprint is this server - its
// previous run, or a sleep proxy speaking for it - and neither is a reason to
// give up the name.
//
// Claimed again whenever the name or AdvertiseOnLan changes, so both apply
// without a restart - including a rename onto a name another server already
// has, which is the case a claim made only at startup would miss. And it keeps
// listening while announced: a server that starts later under the same name
// without yielding (an older build, or two started at the same moment) is
// noticed, and exactly one of the pair yields - the one with the greater
// fingerprint - so they cannot both rename or both stay.
//
// IHostedLifecycleService rather than IHostedService: the port has to come from
// the server's actually-bound address, and that is only populated once Kestrel
// has started. StartedAsync runs after it has.
public sealed class MdnsAdvertiser : IHostedLifecycleService, IDisposable
{
    // How long to listen before claiming a name. Answers to a query come back
    // within a few hundred milliseconds on a LAN; this leaves room for a busy
    // one and still announces well before anyone has picked up a phone.
    public static readonly TimeSpan ListenBeforeClaiming = TimeSpan.FromSeconds(2);

    // After this many taken names, give up numbering and announce the name as
    // asked. Twenty servers of one name on one network is not a case worth an
    // unbounded loop.
    private const int MaxSuffix = 20;

    private readonly IServer _server;
    private readonly IOptionsMonitor<FlowerServerOptions> _options;
    private readonly string _ownFingerprint;
    private readonly Func<IMdnsBackend> _backendFactory;
    private readonly Func<IPEndPoint, CancellationToken, Task<string?>> _fingerprintAt;
    private readonly TimeSpan _listenFor;
    private readonly ILogger<MdnsAdvertiser> _logger;

    // One claim at a time: a rename landing mid-claim waits, then claims again
    // with whatever the settings say by then.
    private readonly SemaphoreSlim _claiming = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();

    private IDisposable? _optionsSubscription;
    private IMdnsBackend? _backend;
    private int? _port;

    // What is being asked for and what was actually announced. They differ
    // only when the asked-for name was taken.
    private string? _desired;
    private volatile string? _claimed;

    // Endpoints already asked who they are while announced, so the steady
    // stream of re-announcements does not become a request each.
    private readonly ConcurrentDictionary<IPEndPoint, string?> _checked = new();

    public MdnsAdvertiser(
        IServer server, IOptionsMonitor<FlowerServerOptions> options,
        DeviceSigningKey signingKey, ILogger<MdnsAdvertiser> logger)
        : this(server, options, signingKey.Fingerprint,
            () => PlatformMdns.Current ?? new MakaretuMdnsBackend(),
            FingerprintAtAsync, ListenBeforeClaiming, logger)
    {
    }

    // The seams a test drives: the backend (a fake network) and how an
    // endpoint's fingerprint is asked for (a fake /info).
    public MdnsAdvertiser(
        IServer server, IOptionsMonitor<FlowerServerOptions> options, string ownFingerprint,
        Func<IMdnsBackend> backendFactory, Func<IPEndPoint, CancellationToken, Task<string?>> fingerprintAt,
        TimeSpan listenFor, ILogger<MdnsAdvertiser> logger)
    {
        _server = server;
        _options = options;
        _ownFingerprint = ownFingerprint;
        _backendFactory = backendFactory;
        _fingerprintAt = fingerprintAt;
        _listenFor = listenFor;
        _logger = logger;
    }

    // The name this server goes by on the network: the one it claimed, or,
    // while it announces nothing, the one it was given. /info reports this, so
    // a client's label matches the row it found.
    public string Name => _claimed ?? InstanceName(_options.CurrentValue);

    // The name that was asked for, when a clash meant announcing another. Null
    // when the two are the same. For the settings page, which edits the former
    // and has to explain the latter.
    public string? ClaimedInsteadOf => _claimed is { } claimed && _desired is { } desired && claimed != desired ? desired : null;

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        var addresses = (_server.Features.Get<IServerAddressesFeature>()?.Addresses ?? []).ToArray();
        _port = AdvertisablePort(addresses);
        if (_port is null && _options.CurrentValue.AdvertiseOnLan)
        {
            // Neither case is actionable for the operator and nothing else
            // breaks, so neither is a startup failure: the server still serves
            // every request, it just has to be reached by address rather than
            // found. Bound-to-loopback is a deliberate choice often enough
            // (a dev instance, a reverse proxy in front) that it is not even a
            // warning.
            if (addresses.Length == 0)
                _logger.LogWarning("Could not determine the bound port; skipping mDNS advertisement.");
            else
                _logger.LogInformation(
                    "Bound only to loopback ({Addresses}); skipping mDNS advertisement, since nothing off this machine could reach it.",
                    string.Join(", ", addresses));
        }

        _optionsSubscription = _options.OnChange(_ => ScheduleClaim());
        ScheduleClaim();
        return Task.CompletedTask;
    }

    // In the background: listening first costs a couple of seconds, and
    // neither startup nor the settings save that set off a rename should wait
    // on it.
    private void ScheduleClaim() => _ = ClaimAsync(force: false);

    // Brings what is announced into line with the settings. Returns once the
    // new name is announced, or nothing is.
    public async Task ClaimAsync(bool force)
    {
        try
        {
            await _claiming.WaitAsync(_stopping.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var settings = _options.CurrentValue;
            var desired = settings.AdvertiseOnLan && _port is not null ? InstanceName(settings) : null;
            if (!force && desired == _desired && (desired is null || _backend is not null))
                return;

            Withdraw();
            _desired = desired;
            if (desired is null || _port is not { } port)
                return;

            var backend = _backendFactory();
            var seen = new ConcurrentDictionary<string, ConcurrentBag<IPEndPoint>>(StringComparer.OrdinalIgnoreCase);
            backend.InstanceFound += (_, found) => seen.GetOrAdd(found.InstanceName, _ => []).Add(found.EndPoint);

            try
            {
                backend.Browse(SyncProtocol.ServiceType);
                await Task.Delay(_listenFor, _stopping.Token);

                var name = await ChooseNameAsync(desired, seen, _stopping.Token);
                backend.Advertise(name, SyncProtocol.ServiceType, port);
                backend.InstanceFound += (_, found) => _ = OnAnnouncedWhileClaimedAsync(found);

                _backend = backend;
                _claimed = name;
                _checked.Clear();

                if (name == desired)
                    _logger.LogInformation("Advertising {Instance} as {ServiceType} on port {Port}.", name, SyncProtocol.ServiceType, port);
                else
                    _logger.LogWarning(
                        "Another Flower server on this network is already called {Desired}, so this one is advertising as {Instance} instead. Rename one of them to tell them apart.",
                        desired, name);
            }
            catch (OperationCanceledException)
            {
                backend.Dispose();
            }
            catch (Exception ex)
            {
                // Multicast is routinely unavailable - a container without host
                // networking, a locked-down VLAN, a firewall. Not being
                // discoverable is a degraded experience, not a broken server,
                // so it must not take the process down.
                backend.Dispose();
                _logger.LogWarning(ex, "Could not advertise on the local network; the server is still reachable by address.");
            }
        }
        finally
        {
            _claiming.Release();
        }
    }

    // The asked-for name if it is free, else the first free "Name (n)".
    private async Task<string> ChooseNameAsync(
        string desired, ConcurrentDictionary<string, ConcurrentBag<IPEndPoint>> seen, CancellationToken ct)
    {
        for (var n = 1; n <= MaxSuffix; n++)
        {
            var candidate = n == 1 ? desired : $"{desired} ({n})";
            if (!await IsTakenAsync(candidate, seen, ct))
                return candidate;
        }

        return desired;
    }

    // Taken: something announced this name, and at least one of the
    // addresses it came from answers /info as a server other than this one.
    private async Task<bool> IsTakenAsync(
        string candidate, ConcurrentDictionary<string, ConcurrentBag<IPEndPoint>> seen, CancellationToken ct)
    {
        if (!seen.TryGetValue(FullInstanceName(candidate), out var endpoints))
            return false;

        foreach (var endpoint in endpoints.Distinct())
        {
            if (await _fingerprintAt(endpoint, ct) is { } fingerprint && fingerprint != _ownFingerprint)
                return true;
        }

        return false;
    }

    // Someone announcing the name this server already holds. Checked once per
    // endpoint while this name is held.
    private async Task OnAnnouncedWhileClaimedAsync(MdnsInstanceFound found)
    {
        if (_claimed is not { } claimed || !string.Equals(found.InstanceName, FullInstanceName(claimed), StringComparison.OrdinalIgnoreCase))
            return;

        if (!_checked.TryAdd(found.EndPoint, null))
            return;

        try
        {
            var other = await _fingerprintAt(found.EndPoint, _stopping.Token);
            if (other is null || other == _ownFingerprint)
                return;

            if (string.CompareOrdinal(_ownFingerprint, other) > 0)
            {
                _logger.LogWarning(
                    "Another Flower server at {EndPoint} is also advertising as {Instance}; this one is taking another name.",
                    found.EndPoint, claimed);
                await ClaimAsync(force: true);
            }
            else
            {
                _logger.LogWarning(
                    "Another Flower server at {EndPoint} is also advertising as {Instance}. Clients will see one row switch between the two until one of them is renamed.",
                    found.EndPoint, claimed);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not check who else is advertising as {Instance}.", claimed);
        }
        catch (OperationCanceledException)
        {
        }
    }

    // Stops announcing, which sends the goodbye so a client drops the row now
    // rather than waiting out its own poll failures.
    private void Withdraw()
    {
        _backend?.Dispose();
        _backend = null;
        _claimed = null;
    }

    private static string FullInstanceName(string name) => $"{name}.{SyncProtocol.ServiceType}.local";

    private static readonly HttpClient InfoClient = new() { Timeout = TimeSpan.FromSeconds(2) };

    // Who answers at an announced endpoint: the fingerprint from its /info, or
    // null when nothing answers or what answers is not a Flower server. The
    // plain port a record carries, over plain HTTP - the same way a client
    // resolves a sighting before it holds anything to pin with.
    private static async Task<string?> FingerprintAtAsync(IPEndPoint endpoint, CancellationToken ct)
    {
        var host = endpoint.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{endpoint.Address.ToString().Replace("%", "%25")}]"
            : endpoint.Address.ToString();

        try
        {
            using var response = await InfoClient.GetAsync($"http://{host}:{endpoint.Port}{SyncProtocol.InfoPath}", ct);
            if (!response.IsSuccessStatusCode)
                return null;

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return json.RootElement.TryGetProperty("fingerprint", out var value) ? value.GetString() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public static string InstanceName(FlowerServerOptions options) =>
        string.IsNullOrWhiteSpace(options.Alias) ? Environment.MachineName : options.Alias.Trim();

    // The port to advertise, or null if there is nothing worth advertising.
    //
    // Only a non-loopback bind is announced. The mDNS record resolves to this
    // machine's LAN addresses regardless of what Kestrel actually bound, so a
    // server started on --urls http://localhost:5599 would otherwise publish
    // itself as reachable at <lan-ip>:5599, where every client that found it
    // gets a connection refused it can do nothing about - and, advertising
    // under the machine name, collides with the row of whichever server on the
    // box is real. That is a dev-instance mistake rather than a deployment
    // one, which is exactly why it is worth catching here: the symptom shows
    // up on someone else's screen, a hop away from the cause.
    // The port of the first non-loopback listener, optionally restricted to one
    // scheme. Loopback-only is excluded because the question this answers is
    // always "what would someone else dial", and nobody else can dial 127.0.0.1.
    //
    // The scheme filter is what lets /info report the https and the plain
    // listener separately now that there are two (see DiscoveryEndpoints
    // .ReachableOrigins). mDNS itself still asks without one and so keeps
    // getting the plain port, which is right: a sighting is dialled at a bare
    // IP with no name for a certificate to be issued for, and pairing happens
    // over it before this device holds anything to pin with.
    public static int? AdvertisablePort(IEnumerable<string> boundAddresses, string? scheme = null) =>
        boundAddresses
            .Select(Parse)
            .FirstOrDefault(uri => uri is { IsLoopback: false } && (scheme == null || uri.Scheme == scheme))
            ?.Port;

    private static Uri? Parse(string address)
    {
        // Kestrel reports wildcard binds as http://[::]:4533, http://+:4533 or
        // http://*:4533, none of which Uri will parse - substituting 0.0.0.0
        // preserves both things read here, the port and whether the bind is
        // loopback-only, which a wildcard bind is not.
        var normalized = address.Replace("[::]", "0.0.0.0").Replace("//+", "//0.0.0.0").Replace("//*", "//0.0.0.0");
        return Uri.TryCreate(normalized, UriKind.Absolute, out var uri) && uri.Port > 0 ? uri : null;
    }

    public Task StoppingAsync(CancellationToken cancellationToken)
    {
        // Unannounce on the way out so a client sees the goodbye and prunes
        // the row immediately, rather than waiting out its own poll failures.
        _stopping.Cancel();
        _optionsSubscription?.Dispose();
        Withdraw();
        return Task.CompletedTask;
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose()
    {
        _stopping.Cancel();
        _optionsSubscription?.Dispose();
        Withdraw();
    }
}
