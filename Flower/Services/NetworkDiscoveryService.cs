using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

    // Whether this peer currently advertises itself as a Server (see
    // AppSettings.IsServer, SyncHttpServer.HandleInfoAsync) - resolved
    // alongside Alias/Fingerprint via the same /info handshake. Drives
    // MainViewModel.AvailableServers; unrelated to trust/pairing.
    public bool IsServer { get; set; }
}

// Default IMdnsBackend (see PlatformMdns.cs): raw multicast via
// Makaretu.Dns.Multicast. Works everywhere except real iOS hardware - see
// PlatformMdns's own doc comment for why - where Flower.iOS overrides
// PlatformMdns.Current with a Bonjour-API-backed implementation instead.
internal sealed class MakaretuMdnsBackend : IMdnsBackend
{
    private readonly MulticastService _mdns = new();
    private readonly ServiceDiscovery _serviceDiscovery;

    public event EventHandler<MdnsInstanceFound>? InstanceFound;
    public event EventHandler<string>? InstanceLost;

    public MakaretuMdnsBackend()
    {
        _serviceDiscovery = new ServiceDiscovery(_mdns);
        _serviceDiscovery.ServiceInstanceDiscovered += (_, e) =>
        {
            var name = e.ServiceInstanceName.ToString();

            // No separate resolve round-trip needed: the discovery answer already
            // carries the sender's real address (RemoteEndPoint) and, per DNS-SD
            // convention, the SRV record with the service port in AdditionalRecords.
            var srv = e.Message.AdditionalRecords.OfType<SRVRecord>().FirstOrDefault();
            var port = srv?.Port ?? (ushort)SyncHttpServer.DefaultPort;
            var endpoint = new IPEndPoint(e.RemoteEndPoint.Address, port);
            InstanceFound?.Invoke(this, new MdnsInstanceFound { InstanceName = name, EndPoint = endpoint });
        };
        _serviceDiscovery.ServiceInstanceShutdown += (_, e) =>
            InstanceLost?.Invoke(this, e.ServiceInstanceName.ToString());
    }

    public void Advertise(string instanceName, string serviceType, int port)
    {
        _serviceDiscovery.Advertise(new ServiceProfile(instanceName, serviceType, (ushort)port));
        _mdns.Start();
    }

    public void Browse(string serviceType) => _serviceDiscovery.QueryServiceInstances(serviceType);

    public void Stop()
    {
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

// See SYNC-PLAN.md: mDNS discovery (proven working macOS <-> iOS Simulator, and -
// via Flower.iOS's Bonjour-API backend, see PlatformMdns.cs - real iOS hardware
// too) plus the start of the real sync protocol - device identity exchange over
// plain HTTP (see SyncHttpServer). File transfer itself is a later phase.
public class NetworkDiscoveryService : IDisposable
{
    private const string ServiceType = "_flowersync._tcp";

    // How often an already-known peer's /info is re-fetched, independent of
    // any fresh mDNS announcement - see PollKnownDevicesAsync. A peer that
    // renames itself (DeviceIdentityStore.Alias, MainViewModel.DeviceAlias)
    // or changes role (AppSettings.IsServer) while both apps are already
    // running and connected would otherwise not be noticed here until
    // something else naturally re-triggers discovery (that peer's app
    // relaunching, dropping off and rejoining the network, etc.) - mDNS's own
    // passive re-announcement cadence isn't something this codebase controls
    // or can rely on for a timely update. Only a tiny /info GET per known
    // peer and only while the app is foregrounded (not a background
    // service), so a short interval is cheap.
    private static readonly TimeSpan AliasPollInterval = TimeSpan.FromSeconds(5);

    // How often Browse() itself is re-issued, independent of AliasPollInterval
    // - see PollKnownDevicesAsync. Start() only calls Browse() once; a peer
    // that gets pruned as stale (MaxConsecutiveResolveFailures, a transient
    // Wi-Fi hiccup rather than a real goodbye) or was simply missed the first
    // time otherwise has no way back into _knownDevices short of its own
    // spontaneous mDNS re-announcement, which this codebase doesn't control
    // and can be a long, OS-determined interval - observed in practice as a
    // still-reachable peer permanently vanishing from the sidebar after both
    // apps had been running a while. Matches AliasPollInterval's cadence - a
    // peer can be pruned as little as ~15s after going quiet
    // (MaxConsecutiveResolveFailures), so re-browsing any slower than that
    // leaves a gap where it stays missing longer than it needed to. Just a
    // tiny multicast query, not a per-peer unicast request, so there's no
    // real cost to matching the faster cadence.
    private static readonly TimeSpan RebrowseInterval = TimeSpan.FromSeconds(5);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    private readonly IMdnsBackend _backend;

    // Every peer currently known on the LAN, keyed by its raw mDNS instance
    // name - see PollKnownDevicesAsync. Not the same identity key
    // MainViewModel's sidebar uses once a Fingerprint resolves (see
    // MainViewModel.FindDeviceSidebarItem) - at this layer, the mDNS instance
    // name is the only thing that actually identifies "which record" to
    // re-poll.
    private readonly ConcurrentDictionary<string, DiscoveredDevice> _knownDevices = new();
    private CancellationTokenSource? _pollCts;
    private readonly ILogger _logger;

    // How many consecutive failed /info polls (see ResolveAliasAsync,
    // PollKnownDevicesAsync) a peer gets before it's treated as gone and
    // pruned the same way an actual mDNS goodbye would remove it. A device
    // that goes offline without a clean goodbye - backgrounding/locking on
    // iOS doesn't send one, and neither does a hard kill - would otherwise
    // sit in _knownDevices (and the sidebar) forever, unreachable but never
    // removed. Three misses (~15s at AliasPollInterval's cadence) is
    // deliberately more forgiving than a single miss, since a transient
    // Wi-Fi hiccup or one slow response shouldn't drop a peer that's
    // actually still there.
    private const int MaxConsecutiveResolveFailures = 3;
    private readonly ConcurrentDictionary<string, int> _consecutiveResolveFailures = new();

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
        _backend = PlatformMdns.Current ?? new MakaretuMdnsBackend();
        _backend.InstanceFound += OnInstanceFound;
        _backend.InstanceLost += (_, name) =>
        {
            if (!IsOurServiceType(name))
                return;

            _knownDevices.TryRemove(name, out DiscoveredDevice? _);
            _logger.LogInformation("Peer {InstanceName} went away", name);
            DeviceLost?.Invoke(this, name);
        };
    }

    // port is whatever SyncHttpServer actually bound (see SyncHttpServer.Start) - not
    // necessarily SyncHttpServer.DefaultPort, since that can be taken by another
    // Flower instance on the same machine (e.g. the iOS Simulator). Advertising the
    // real port means peers never need to assume a fixed one; they read it from the
    // SRV record in the discovery answer instead (see OnInstanceFound).
    public void Start(int port)
    {
        _backend.Advertise(OwnInstanceName, ServiceType, port);
        _backend.Browse(ServiceType);

        _pollCts = new CancellationTokenSource();
        _ = PollKnownDevicesAsync(_pollCts.Token);
    }

    // Re-publishes this device's own advertisement and re-issues Browse() -
    // meant to be called when an app returns to the foreground after being
    // backgrounded (see Flower.iOS's AppDelegate.WillEnterForeground). The
    // poll loop above just pauses under iOS suspension and resumes ticking
    // on its own once unsuspended, needing no explicit restart - but
    // NSNetService's own Bonjour publication (BonjourMdnsBackend, real iOS
    // hardware only) is a known exception: it can silently drop while
    // backgrounded and does not automatically resume just because the
    // process becomes active again. Without this, a phone that locks for a
    // while stops being discoverable by any peer for the rest of the app's
    // lifetime, even though the process itself never died - observed in
    // practice as the phone never reappearing in the desktop's sidebar after
    // being brought back from sleep. Harmless to call when nothing was
    // actually stale (e.g. on desktop, which has no such quirk) - Advertise/
    // Browse are just a re-publish/re-query, not a state reset.
    public void Restart(int port)
    {
        _logger.LogInformation("Restarting mDNS advertise/browse");
        _backend.Advertise(OwnInstanceName, ServiceType, port);
        _backend.Browse(ServiceType);
    }

    // See AliasPollInterval for why this exists alongside the event-driven
    // discovery path above.
    private async Task PollKnownDevicesAsync(CancellationToken token)
    {
        var lastBrowse = DateTime.UtcNow;
        try
        {
            while (true)
            {
                await Task.Delay(AliasPollInterval, token);
                var devices = _knownDevices.Values.ToList();
                _logger.LogDebug("Polling /info for {Count} known device(s)", devices.Count);
                foreach (var device in devices)
                    _ = ResolveAliasAsync(device);

                // See RebrowseInterval for why this re-query exists alongside
                // the one-shot Browse() call in Start().
                if (DateTime.UtcNow - lastBrowse >= RebrowseInterval)
                {
                    lastBrowse = DateTime.UtcNow;
                    _logger.LogDebug("Re-browsing for {ServiceType} peers", ServiceType);
                    _backend.Browse(ServiceType);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stop() was called.
        }
    }

    private void OnInstanceFound(object? sender, MdnsInstanceFound found)
    {
        // The backend reports any service instance seen on the LAN matching what
        // we asked it to browse for - filter to our own service type in case a
        // platform backend (or a stray non-Flower responder on the same type,
        // unlikely but cheap to guard) reports something else.
        if (!IsOurServiceType(found.InstanceName))
            return;

        if (found.InstanceName.StartsWith(OwnInstanceName + ".", StringComparison.OrdinalIgnoreCase))
            return; // mDNS reflects our own advertisement back to us - not a peer.

        // A dual-stack peer (the common case on Wi-Fi) can answer the same
        // multicast query from more than one of its own addresses - observed
        // in practice as a link-local IPv6 one (fe80::/10) alongside a normal
        // IPv4 one for the exact same instance name. The link-local address
        // is scope-bound to whatever interface happened to receive that
        // particular packet, which does not reliably accept a follow-up
        // unicast HTTP connection from this process even though it's the
        // same reachable peer - confirmed on a real device: HttpClient throws
        // "Connection refused" against it while the very next announcement
        // for the identical InstanceName, arriving over IPv4, connects fine.
        // Every RebrowseInterval re-issues the multicast query, so without
        // this a peer already known via a working address would otherwise
        // keep flip-flopping back onto the unreliable one - not just noisy
        // logging, but real sync requests (LibraryDownloadService,
        // PlaylistSyncService) can land on whichever endpoint happens to be
        // stored at that moment and fail the same way. A routable address,
        // once recorded, is never downgraded back to a link-local one for
        // the same instance name; a link-local address is still recorded if
        // it's the only thing seen so far, and gets replaced the moment a
        // routable one shows up.
        if (_knownDevices.TryGetValue(found.InstanceName, out var existing) &&
            !existing.EndPoint.Address.IsIPv6LinkLocal &&
            found.EndPoint.Address.IsIPv6LinkLocal)
        {
            _logger.LogDebug("Ignoring link-local re-announcement for {InstanceName} at {EndPoint} - already have a routable address {Existing}",
                found.InstanceName, found.EndPoint, existing.EndPoint);
            return;
        }

        var device = new DiscoveredDevice { InstanceName = found.InstanceName, EndPoint = found.EndPoint, Alias = found.InstanceName };
        _knownDevices[found.InstanceName] = device;
        _logger.LogInformation("Discovered peer {InstanceName} at {EndPoint}", found.InstanceName, found.EndPoint);
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
            _consecutiveResolveFailures.TryRemove(device.InstanceName, out _);
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
            if (doc.RootElement.TryGetProperty("isServer", out var isServerProp))
            {
                // JsonElement has no TryGetBoolean - a JSON literal true/false
                // parses to ValueKind.True/False directly.
                var isServer = isServerProp.ValueKind == JsonValueKind.True;
                if (isServer != device.IsServer)
                {
                    device.IsServer = isServer;
                    changed = true;
                }
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
            // mDNS-name fallback alias rather than failing the discovery, unless
            // this is its MaxConsecutiveResolveFailures'th miss in a row, in
            // which case treat it the same as an mDNS goodbye - see
            // MaxConsecutiveResolveFailures's own doc comment.
            _logger.LogDebug(ex, "Could not resolve /info for {InstanceName} at {EndPoint}", device.InstanceName, device.EndPoint);

            var failures = _consecutiveResolveFailures.AddOrUpdate(device.InstanceName, 1, (_, count) => count + 1);
            if (failures >= MaxConsecutiveResolveFailures && _knownDevices.TryRemove(device.InstanceName, out _))
            {
                _consecutiveResolveFailures.TryRemove(device.InstanceName, out _);
                _logger.LogInformation("Peer {InstanceName} unreachable after {Failures} consecutive /info attempts - treating as gone",
                    device.InstanceName, failures);
                DeviceLost?.Invoke(this, device.InstanceName);
            }
        }
    }

    // Resolves a peer by its stable Fingerprint (not the mDNS instance name keying
    // _knownDevices above) - used wherever a placeholder Track's OriginDeviceFingerprint
    // needs turning into an actual reachable endpoint, e.g. LibraryDownloadService's
    // audio download and AlbumArtLoader's synced-art fetch.
    public DiscoveredDevice? FindByFingerprint(string fingerprint) =>
        _knownDevices.Values.FirstOrDefault(d => d.Fingerprint == fingerprint);

    // Every peer currently known on the LAN, regardless of trust/role - used
    // by MainViewModel.AvailableServers to filter down to just the ones
    // advertising IsServer. Snapshot, not a live view - callers that need to
    // react to changes should also subscribe to DeviceDiscovered/DeviceLost.
    //
    // Deduped by Fingerprint: the same physical device can end up as more
    // than one entry in _knownDevices under different mDNS instance names
    // (a prior run's advertisement re-registering under an auto-renamed name
    // after Bonjour's own collision avoidance, or a stale record surfaced
    // again by a fresh Browse() - see PollKnownDevicesAsync). MainViewModel's
    // sidebar has its own separate reconciliation for this (see
    // RemoveDuplicateDeviceSidebarItems), built from individual
    // DeviceDiscovered events rather than this snapshot, so it needed its
    // own fix; every other consumer (AvailableServers in particular) reads
    // straight from here and had no such dedup, hence a server appearing
    // twice in the picker. Entries with no resolved Fingerprint yet are kept
    // as-is (grouped by instance name instead) since they cannot yet be
    // proven to be duplicates of anything.
    public IReadOnlyCollection<DiscoveredDevice> KnownDevices =>
        _knownDevices.Values
            .GroupBy(d => string.IsNullOrEmpty(d.Fingerprint) ? $"instance:{d.InstanceName}" : $"fingerprint:{d.Fingerprint}")
            .Select(g => g.First())
            .ToList();

    private static bool IsOurServiceType(string instanceName) =>
        instanceName.EndsWith($"{ServiceType}.local", StringComparison.OrdinalIgnoreCase);

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
        _backend.Stop();
    }

    public void Dispose()
    {
        Stop();
        _backend.Dispose();
    }
}
