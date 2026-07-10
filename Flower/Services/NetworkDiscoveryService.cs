using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Makaretu.Dns;

namespace Flower.Services;

// A Flower instance found on the LAN. Alias starts out as the raw mDNS
// instance name and is replaced once the /info handshake resolves - see
// NetworkDiscoveryService.ResolveAliasAsync.
public class DiscoveredDevice
{
    public required string InstanceName { get; init; }
    public required IPEndPoint EndPoint { get; init; }
    public string Alias { get; set; } = "";

    // Resolved alongside Alias via the /info handshake - see ResolveAliasAsync.
    // Empty until that resolves; PlaylistSyncService treats an empty fingerprint
    // as "not ready to sync yet" rather than guessing an identity for the peer.
    public string Fingerprint { get; set; } = "";
}

// See SYNC-PLAN.md: mDNS discovery (proven working macOS <-> iOS Simulator) plus
// the start of the real sync protocol - device identity exchange over plain HTTP
// (see SyncHttpServer). File transfer itself is a later phase.
public class NetworkDiscoveryService : IDisposable
{
    private const string ServiceType = "_flowersync._tcp";

    // How often an already-known peer's /info is re-fetched, independent of
    // any fresh mDNS announcement - see PollKnownDevicesAsync. A peer that
    // renames itself (DeviceIdentityStore.Alias, MainViewModel.DeviceAlias)
    // while both apps are already running and connected would otherwise not
    // be noticed here until something else naturally re-triggers discovery
    // (that peer's app relaunching, dropping off and rejoining the network,
    // etc.) - mDNS's own passive re-announcement cadence isn't something this
    // codebase controls or can rely on for a timely update.
    private static readonly TimeSpan AliasPollInterval = TimeSpan.FromSeconds(30);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    private readonly MulticastService _mdns = new();
    private readonly ServiceDiscovery _serviceDiscovery;

    // Every peer currently known on the LAN, keyed by its raw mDNS instance
    // name - see PollKnownDevicesAsync. Not the same identity key
    // MainViewModel's sidebar uses once a Fingerprint resolves (see
    // MainViewModel.FindDeviceSidebarItem) - at this layer, the mDNS instance
    // name is the only thing that actually identifies "which record" to
    // re-poll.
    private readonly ConcurrentDictionary<string, DiscoveredDevice> _knownDevices = new();
    private CancellationTokenSource? _pollCts;
    private readonly ILogger _logger;

    // Env.MachineName alone collides between desktop and the iOS Simulator, since
    // the simulator shares the host Mac's actual hostname rather than having a
    // network identity of its own - tag with the platform so the two are
    // distinguishable when testing that way. Also used as the alias SyncHttpServer
    // reports over /info, so both sides of the sync protocol agree on our identity.
    public string OwnInstanceName { get; } = $"{Environment.MachineName}-{PlatformTag()}";

    public event EventHandler<DiscoveredDevice>? DeviceDiscovered;
    public event EventHandler<string>? DeviceLost;

    public NetworkDiscoveryService(ILogger<NetworkDiscoveryService> logger)
    {
        _logger = logger;
        _serviceDiscovery = new ServiceDiscovery(_mdns);
        _serviceDiscovery.ServiceInstanceDiscovered += OnServiceInstanceDiscovered;
        _serviceDiscovery.ServiceInstanceShutdown += (_, e) =>
        {
            if (!IsOurServiceType(e.ServiceInstanceName))
                return;

            var name = e.ServiceInstanceName.ToString();
            _knownDevices.TryRemove(name, out DiscoveredDevice? _);
            _logger.LogInformation("Peer {InstanceName} went away", name);
            DeviceLost?.Invoke(this, name);
        };
    }

    // port is whatever SyncHttpServer actually bound (see SyncHttpServer.Start) - not
    // necessarily SyncHttpServer.DefaultPort, since that can be taken by another
    // Flower instance on the same machine (e.g. the iOS Simulator). Advertising the
    // real port means peers never need to assume a fixed one; they read it from the
    // SRV record in the discovery answer instead (see OnServiceInstanceDiscovered).
    public void Start(int port)
    {
        var profile = new ServiceProfile(OwnInstanceName, ServiceType, (ushort)port);
        _serviceDiscovery.Advertise(profile);
        _mdns.Start();
        _serviceDiscovery.QueryServiceInstances(ServiceType);

        _pollCts = new CancellationTokenSource();
        _ = PollKnownDevicesAsync(_pollCts.Token);
    }

    // See AliasPollInterval for why this exists alongside the event-driven
    // discovery path above.
    private async Task PollKnownDevicesAsync(CancellationToken token)
    {
        try
        {
            while (true)
            {
                await Task.Delay(AliasPollInterval, token);
                var devices = _knownDevices.Values.ToList();
                _logger.LogDebug("Polling /info for {Count} known device(s)", devices.Count);
                foreach (var device in devices)
                    _ = ResolveAliasAsync(device);
            }
        }
        catch (OperationCanceledException)
        {
            // Stop() was called.
        }
    }

    private void OnServiceInstanceDiscovered(object? sender, ServiceInstanceDiscoveryEventArgs e)
    {
        // ServiceInstanceDiscovered fires for any service instance seen on the LAN,
        // not just ones matching what we queried for (mDNS is a shared multicast
        // channel) - e.g. printers, Chromecasts, or Apple's own device-pairing
        // traffic show up here too. Filter to our own service type.
        if (!IsOurServiceType(e.ServiceInstanceName))
            return;

        var name = e.ServiceInstanceName.ToString();
        if (name.StartsWith(OwnInstanceName + ".", StringComparison.OrdinalIgnoreCase))
            return; // mDNS reflects our own advertisement back to us - not a peer.

        // No separate resolve round-trip needed: the discovery answer already
        // carries the sender's real address (RemoteEndPoint) and, per DNS-SD
        // convention, the SRV record with the service port in AdditionalRecords.
        var srv = e.Message.AdditionalRecords.OfType<SRVRecord>().FirstOrDefault();
        var port = srv?.Port ?? (ushort)SyncHttpServer.DefaultPort;
        var endpoint = new IPEndPoint(e.RemoteEndPoint.Address, port);

        var device = new DiscoveredDevice { InstanceName = name, EndPoint = endpoint, Alias = name };
        _knownDevices[name] = device;
        _logger.LogInformation("Discovered peer {InstanceName} at {EndPoint}", name, endpoint);
        DeviceDiscovered?.Invoke(this, device);

        _ = ResolveAliasAsync(device);
    }

    // Fetches the peer's real alias and fingerprint via the /info handshake
    // (SyncHttpServer), replacing the raw mDNS instance name shown until this
    // resolves. Best-effort: a peer that is not yet listening, or never will be,
    // just keeps the fallback (and PlaylistSyncService won't attempt to sync with
    // it, since Fingerprint stays empty). Also called periodically for
    // already-known peers (see PollKnownDevicesAsync), so this only re-fires
    // DeviceDiscovered when something actually changed - otherwise every poll
    // of every peer would needlessly re-trigger MainViewModel's sidebar
    // refresh even when nothing did.
    private async Task ResolveAliasAsync(DiscoveredDevice device)
    {
        try
        {
            var json = await Http.GetStringAsync($"http://{device.EndPoint}/api/localsend/v2/info");
            using var doc = JsonDocument.Parse(json);
            var changed = false;
            if (doc.RootElement.TryGetProperty("alias", out var aliasProp) &&
                aliasProp.GetString() is { } alias && alias != device.Alias)
            {
                device.Alias = alias;
                changed = true;
            }
            if (doc.RootElement.TryGetProperty("fingerprint", out var fpProp) &&
                fpProp.GetString() is { } fingerprint && fingerprint != device.Fingerprint)
            {
                device.Fingerprint = fingerprint;
                changed = true;
            }
            if (changed)
            {
                _logger.LogInformation("Peer {InstanceName} info updated: alias={Alias}, fingerprint={Fingerprint}",
                    device.InstanceName, device.Alias, device.Fingerprint);
                DeviceDiscovered?.Invoke(this, device);
            }
        }
        catch (Exception ex)
        {
            // Peer unreachable or not running the /info endpoint yet - keep the
            // mDNS-name fallback alias rather than failing the discovery.
            _logger.LogDebug(ex, "Could not resolve /info for {InstanceName} at {EndPoint}", device.InstanceName, device.EndPoint);
        }
    }

    // Resolves a peer by its stable Fingerprint (not the mDNS instance name keying
    // _knownDevices above) - used wherever a placeholder Track's OriginDeviceFingerprint
    // needs turning into an actual reachable endpoint, e.g. LibraryDownloadService's
    // audio download and AlbumArtLoader's synced-art fetch.
    public DiscoveredDevice? FindByFingerprint(string fingerprint) =>
        _knownDevices.Values.FirstOrDefault(d => d.Fingerprint == fingerprint);

    private static bool IsOurServiceType(DomainName name) =>
        name.ToString().EndsWith($"{ServiceType}.local", StringComparison.OrdinalIgnoreCase);

    private static string PlatformTag()
    {
        if (OperatingSystem.IsIOS())
            return "iOS";
        if (OperatingSystem.IsAndroid())
            return "Android";
        if (OperatingSystem.IsMacOS())
            return "macOS";
        if (OperatingSystem.IsWindows())
            return "Windows";
        if (OperatingSystem.IsLinux())
            return "Linux";
        return "Unknown";
    }

    public void Stop()
    {
        _pollCts?.Cancel();
        _pollCts = null;
        _serviceDiscovery.Unadvertise();
        _mdns.Stop();
    }

    public void Dispose()
    {
        Stop();
        _serviceDiscovery.Dispose();
        _mdns.Dispose();
    }
}
