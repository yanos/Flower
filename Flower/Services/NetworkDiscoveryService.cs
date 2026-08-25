using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Flower.Persistence;

namespace Flower.Services;

// A Flower instance found on the LAN. Alias starts out as the raw mDNS
// instance name and is replaced once the /info handshake resolves - see
// NetworkDiscoveryService.ResolveAliasAsync.
public class DiscoveredDevice
{
    public required string InstanceName { get; init; }

    // Where this peer is dialled: scheme, host and port, no path. A Uri rather
    // than an IPEndPoint because the scheme is not always http and the host is
    // not always a literal - a server behind a tunnel or a real certificate is
    // reached by name, and https is the whole point of naming it.
    //
    // The host is kept exactly as it was written or announced, and deliberately
    // not replaced by its resolved address: a certificate is issued for a name,
    // so dialling the IP behind that name would fail validation even though the
    // connection is the one intended. Resolution is HttpClient's job, per
    // request, which also means a name that moves is followed for free.
    public required Uri BaseUri { get; init; }

    // The address behind BaseUri when it is known - an mDNS sighting always has
    // one, a remembered address has whatever it resolved to at the time, and a
    // name that has never resolved has none.
    //
    // Only ever used to *classify* a route, never to dial one: ReachRank asks
    // which network an address belongs to, and the link-local checks ask whether
    // it is worth retrying. Both are questions about the address itself, which
    // is why the resolved value is kept alongside the name rather than replacing
    // it.
    public IPAddress? Ip { get; init; }

    // The absolute URL of a path on this peer. Every caller goes through this
    // rather than interpolating a scheme of its own - there used to be eleven
    // places hardcoding "http://", which is precisely why none of them could
    // talk to a server behind TLS.
    public Uri Url(string pathAndQuery) => new(BaseUri, pathAndQuery);

    // The same thing as a string with no trailing slash, for the two consumers
    // that take a base URL rather than build one (OpenSubsonicClient and
    // RemoteLibraryImporter).
    public string Origin => BaseUri.GetLeftPart(UriPartial.Authority);

    public string Alias { get; set; } = "";

    // Resolved alongside Alias via the /info handshake - see ResolveAliasAsync.
    // Empty until that resolves; PlaylistSyncService treats an empty fingerprint
    // as "not ready to sync yet" rather than guessing an identity for the peer.
    public string Fingerprint { get; set; } = "";

    // The peer's self-reported kind, resolved via the same /info handshake as
    // the rest. Only ever "server" now that a client no longer advertises
    // itself at all (see NetworkDiscoveryService.Start) - kept because it is
    // what a sighting says about itself, and a peer that answers with anything
    // else is a Flower this one does not understand rather than something to
    // guess about.
    public string DeviceType { get; set; } = "";

    // The peer's signing public key, as it reports on /info, in
    // DeviceSigningKey.PublicKeyBase64's encoding. Empty until resolved.
    // Recorded into this device's own TrustedPeerStore when pairing succeeds,
    // which is what lets PeerHttpClient pin the server's TLS certificate -
    // see PeerSyncCoordinator.PairWithServer.
    public string PublicKey { get; set; } = "";

    // Whether this peer currently trusts *us* (the trustsCaller field of
    // Flower.Server's DiscoveryEndpoints /info answer, from its own
    // TrustedPeerStore) - resolved alongside the rest via the same /info handshake, since
    // ResolveAliasAsync now identifies us on that request too. Defaults true
    // so a not-yet-resolved device, or one running old code with no
    // trustsCaller field at all, isn't mistaken for an active rejection -
    // MainViewModel only ever acts on this flipping to false. Meaningless for
    // a peer that isn't our paired Server (every peer answers it, but only
    // MainViewModel.PairedServerFingerprint's own trust status matters).
    public bool TrustsUs { get; set; } = true;

    // Whether this peer counts *us* as one of its administrators (the
    // callerIsAdmin field of the same /info answer). Defaults false, the
    // opposite way round from TrustsUs, and for the same reason: TrustsUs
    // guards against wrongly reading silence as a revocation, while this one
    // guards against offering a control that only the server can actually
    // authorise. An unresolved peer, or one that says nothing, is treated as
    // "not an administrator here" - the admin-only buttons stay hidden until
    // the server says otherwise. See MainViewModel.CanInviteDeviceToPairedServer.
    public bool WeAreAdmin { get; set; }

    // This peer's Library.ChangeToken as of its last /info answer - the same
    // opaque token GET /api/flower/v1/library serves as its ETag. Empty until
    // resolved. Changing means the peer's catalog changed, which is how a
    // Client notices a *server-side* edit at all: sync otherwise fires only on
    // first mDNS contact or a debounced local change, so a track added on the
    // Server went unnoticed for as long as both apps stayed running (see
    // ARCHITECTURE-REVIEW Tier 1.4, MainViewModel.HandleDeviceDiscovered).
    public string LibraryToken { get; set; } = "";

    // Every address this peer says it can be reached on, from the same /info
    // handshake as the rest (SyncInfoResponseDto.Addresses). Empty for a peer
    // that predates the field. A client persists these for the server it paired
    // with, which is what lets it keep hold of that server after leaving the
    // network it discovered it on - see PairedServerReachability and
    // REMOTE-ACCESS-PLAN.md.
    public IReadOnlyList<string> Addresses { get; set; } = [];

    // Whether this entry came from a remembered address rather than an mDNS
    // sighting. Two things turn on it.
    //
    // It must not be pruned: MaxConsecutiveResolveFailures exists because a
    // discovered peer that stops answering will be rediscovered when it
    // re-announces, so dropping it costs nothing. A remembered peer has no
    // announcement to come back on, so pruning it destroys the only record of
    // how to reach it. It reads as unreachable instead - see IsResponding.
    //
    // And it ranks below a live sighting: if the same server is both visible on
    // this link and remembered from somewhere else, the sighting is the better
    // route by definition.
    public bool IsRemembered { get; init; }

    // Whether the last /info attempt actually got an answer. For a discovered
    // peer this is nearly always true, since a peer that stops answering is
    // pruned outright; it carries the weight for remembered peers, which are
    // not, and where "we know an address for this server" and "that address
    // works from where we are standing" are genuinely different facts.
    public bool IsResponding { get; set; } = true;
}

// See SYNC-PLAN.md: mDNS discovery (proven working macOS <-> iOS Simulator, and -
// via Flower.iOS's Bonjour-API backend, see PlatformMdns.cs - real iOS hardware
// too) plus the /info identity handshake against everything it finds.
//
// Browse-only. A client does not advertise itself, because a client is not a
// server: the only thing on the network worth finding is a Flower.Server, and
// it is the one advertising (Flower.Server/Services/MdnsAdvertiser.cs). This
// used to be symmetric, back when every app hosted its own listener - see
// SYNC-PLAN.md's "Peer-to-peer, built and removed".
public class NetworkDiscoveryService : IDisposable
{
    private const string ServiceType = SyncProtocol.ServiceType;

    // How often an already-known peer's /info is re-fetched, independent of
    // any fresh mDNS announcement - see PollKnownDevicesAsync. A peer that
    // renames itself (DeviceIdentityStore.Alias, MainViewModel.DeviceAlias)
    // while both ends are already running and connected would otherwise not be
    // noticed here until something else naturally re-triggers discovery (the
    // server restarting, dropping off and rejoining the network, etc.) - mDNS's own
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

    private readonly HttpClient _http;

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

    public event EventHandler<DiscoveredDevice>? DeviceDiscovered;
    public event EventHandler<string>? DeviceLost;

    // Sent as X-Flower-Fingerprint/X-Flower-Alias on every /info request (see
    // ResolveAliasAsync) so a peer that's also a Server can answer whether it
    // trusts us - see DiscoveredDevice.TrustsUs.
    private readonly DeviceIdentity _deviceIdentity;

    // Signs those /info requests. Optional only so the test seam below and the
    // browser head (which registers no signing credentials at all - see
    // App.axaml.cs) can still construct this; in the app it is always the same
    // SignedDeviceCredentials every other outbound peer call uses. Null means
    // the poll goes out identifying itself but unsigned, which a peer answers
    // with the unauthenticated half of /info - no addresses, no trustsCaller.
    private readonly IPeerCredentials? _credentials;

    // backend/httpClient are test-only seams (NetworkDiscoveryServiceTests):
    // production always goes through the other two constructor args alone,
    // getting the real Makaretu-backed mDNS and a real HttpClient exactly as
    // before - a fake IMdnsBackend lets a test drive InstanceFound/
    // InstanceLost directly instead of needing a real LAN, and a fake
    // HttpMessageHandler behind the HttpClient lets a test control
    // ResolveAliasAsync's /info response (or make it fail) without a real
    // socket.
    public NetworkDiscoveryService(DeviceIdentity deviceIdentity, ILogger<NetworkDiscoveryService> logger, IMdnsBackend? backend = null, HttpClient? httpClient = null, IPeerCredentials? credentials = null)
    {
        _deviceIdentity = deviceIdentity;
        _credentials = credentials;
        _logger = logger;
        _backend = backend ?? PlatformMdns.Current ?? new MakaretuMdnsBackend();
        // PeerHttpClient, because this is the client that probes an https
        // origin a server just advertised - so it is where a certificate this
        // device cannot validate has to present as "did not answer" rather than
        // as an exception. See KnownDevices' scheme tie-break.
        _http = httpClient ?? PeerHttpClient.Create(TimeSpan.FromSeconds(3));
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

    // Nothing to advertise and no port to advertise it on - this device is
    // only ever the one looking. A server's own port arrives in the SRV record
    // of the answer it sends back (see OnInstanceFound), so it never has to be
    // assumed either.
    public void Start()
    {
        _backend.Browse(ServiceType);

        _pollCts = new CancellationTokenSource();
        _ = PollKnownDevicesAsync(_pollCts.Token);
    }

    // Re-issues Browse() - meant to be called when an app returns to the
    // foreground after being backgrounded (see Flower.iOS's
    // AppDelegate.WillEnterForeground). The poll loop above just pauses under
    // iOS suspension and resumes ticking on its own once unsuspended, needing
    // no explicit restart, but a browse issued before suspension is not
    // reliably still live afterwards - and a phone that comes back from sleep
    // having quietly stopped seeing its server is indistinguishable, to its
    // owner, from the server being gone. Harmless to call when nothing was
    // actually stale (e.g. on desktop, which has no such quirk) - Browse is a
    // re-query, not a state reset.
    public void Restart()
    {
        _logger.LogInformation("Re-browsing for peers");
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
                // Excludes devices currently stuck on a link-local address -
                // see ResolveAliasAsync's own comment on why polling those on
                // a fixed timer is pointless noise rather than useful retry:
                // a link-local endpoint doesn't get more reachable by trying
                // it again a few seconds later, only by a fresh mDNS
                // announcement (handled by OnInstanceFound instead) actually
                // replacing it with something routable.
                var devices = _knownDevices.Values.Where(d => d.Ip?.IsIPv6LinkLocal != true).ToList();
                _logger.LogDebug("Polling /info for {Count} known device(s)", devices.Count);
                foreach (var device in devices)
                {
                    // A remembered peer that is not answering gets its address
                    // resolved again rather than merely retried, because the
                    // name may now point somewhere else - a server that moved
                    // on its LAN, or one whose tailnet address changed. Retrying
                    // the resolved IP alone would keep dialling an address the
                    // name no longer means, forever. Only when it is already
                    // failing: a working peer needs no lookup.
                    if (device is { IsRemembered: true, IsResponding: false }
                        && RememberedAddressOf(device) is { } address)
                    {
                        _ = AddRememberedAsync(address, token);
                        continue;
                    }

                    _ = ResolveAliasAsync(device);
                }

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
        //
        // Also skips a *repeat* link-local announcement for an instance
        // already recorded under that same link-local address - confirmed on
        // a real device: a burst of several identical link-local
        // announcements for the same peer can arrive within well under a
        // second (multiple raw mDNS packets for one browse), and without
        // this each one built a fresh DiscoveredDevice and kicked off its
        // own concurrent ResolveAliasAsync call, so 3 (already-doomed)
        // resolve attempts could fail back-to-back fast enough to trip
        // MaxConsecutiveResolveFailures and prune the peer within under a
        // second of first seeing it - which then let the very next
        // (also-link-local) announcement repeat the entire cycle, over and
        // over, as long as nothing routable happened to arrive in between.
        if (_knownDevices.TryGetValue(found.InstanceName, out var existing) &&
            found.EndPoint.Address.IsIPv6LinkLocal &&
            (existing.Ip?.IsIPv6LinkLocal != true || existing.Ip.Equals(found.EndPoint.Address)))
        {
            _logger.LogDebug("Ignoring link-local (re-)announcement for {InstanceName} at {EndPoint} - already have {Existing}",
                found.InstanceName, found.EndPoint, existing.BaseUri);
            return;
        }

        // A re-announcement of the exact same instance at the exact same
        // address we already have isn't a new discovery - it's just Browse()
        // re-hearing something still-advertising (every RebrowseInterval, see
        // PollKnownDevicesAsync). Without this, an unreachable-but-still-
        // advertised peer (confirmed in practice on iOS: mDNSResponder can go
        // on reporting a peer that dropped off the actual LAN, e.g. after
        // switching networks) got "rediscovered" here on every rebrowse,
        // replacing its DiscoveredDevice and firing a redundant
        // ResolveAliasAsync on top of PollKnownDevicesAsync's own independent
        // per-peer poll - two-plus concurrent failing attempts per cycle
        // instead of one, which raced MaxConsecutiveResolveFailures up far
        // faster than its 3-strikes design intends, pruned the peer, and then
        // rediscovered it again next rebrowse - an indefinite fail/prune/
        // rediscover loop rather than a steady, bounded one. The periodic
        // poll already re-resolves every known peer on its own cadence, so
        // there is nothing useful left for a repeat announcement to do here.
        var announced = HttpOrigin(found.EndPoint);
        if (existing != null && existing.BaseUri == announced)
        {
            _logger.LogDebug("Ignoring re-announcement for {InstanceName} - already have {EndPoint}, the periodic poll will re-resolve it",
                found.InstanceName, found.EndPoint);
            return;
        }

        var device = new DiscoveredDevice
        {
            InstanceName = found.InstanceName,
            BaseUri = announced,
            Ip = found.EndPoint.Address,
            Alias = found.InstanceName,
        };
        _knownDevices[found.InstanceName] = device;
        _logger.LogInformation("Discovered peer {InstanceName} at {EndPoint}", found.InstanceName, found.EndPoint);
        DeviceDiscovered?.Invoke(this, device);

        _ = ResolveAliasAsync(device);
    }

    // Fetches the peer's real alias and fingerprint via the /info handshake
    // (Flower.Server's DiscoveryEndpoints), replacing the raw mDNS name shown until this
    // resolves. Best-effort: a peer that is not yet listening, or never will be,
    // just keeps the fallback (and PlaylistSyncService won't attempt to sync with
    // it, since Fingerprint stays empty). Also called periodically for
    // already-known peers (see PollKnownDevicesAsync), so this only re-fires
    // DeviceDiscovered when something actually changed - otherwise every poll
    // of every peer would needlessly re-trigger MainViewModel's sidebar
    // refresh even when nothing did.
    private async Task ResolveAliasAsync(DiscoveredDevice device)
    {
        string json;
        try
        {
            // Signed the same way every gated endpoint's calls are, even though
            // /info itself stays ungated: a
            // peer has to be able to learn our fingerprint and public key here
            // before either side can evaluate trust at all, so the route stays
            // open - what the signature buys is the half of the answer that is
            // only for peers who can prove who they are, trustsCaller and the
            // address list (see DiscoveredDevice.TrustsUs/Addresses and
            // docs/OPEN-INTERNET-REVIEW.md).
            //
            // An unsigned X-Flower-Fingerprint used to be enough for that.
            // It never proved anything: fingerprints are public - they are in
            // this very response and in every pairing invite - so anyone could
            // claim one and be told the server's tailnet address.
            using var request = new HttpRequestMessage(HttpMethod.Get, device.Url(SyncProtocol.InfoPath));
            if (_credentials != null)
            {
                await request.AddPeerCredentialsAsync(_credentials);
            }
            else
            {
                request.Headers.Add("X-Flower-Fingerprint", _deviceIdentity.Fingerprint);
                request.Headers.Add("X-Flower-Alias", _deviceIdentity.Alias);
            }
            using var response = await _http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            json = await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            HandleUnreachable(device, ex);
            return;
        }

        // The peer answered, so it's definitely alive and reachable from here
        // on - a failure past this point (malformed JSON, unexpected shape) is
        // a real bug in one side's /info handling, not a connectivity problem,
        // so it must not feed the same "unreachable" failure counter above
        // (that would eventually prune a peer that is very much still there).
        try
        {
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
            // The key this device pins the peer's TLS certificate against once
            // pairing records it - see DiscoveredDevice.PublicKey.
            if (doc.RootElement.TryGetProperty("publicKey", out var keyProp) &&
                keyProp.GetString() is { } publicKey && publicKey != device.PublicKey)
            {
                device.PublicKey = publicKey;
                changed = true;
            }
            // What kind of peer this is, straight from the handshake's
            // deviceType field. Only "server" answers now that clients no
            // longer advertise - see DiscoveredDevice.DeviceType.
            if (doc.RootElement.TryGetProperty("deviceType", out var deviceTypeProp) &&
                deviceTypeProp.GetString() is { } deviceType && deviceType != device.DeviceType)
            {
                device.DeviceType = deviceType;
                changed = true;
            }
            // Present-and-boolean only - absent (older peer) or explicit JSON
            // null (this peer didn't recognize our identity headers) both leave
            // TrustsUs at its current value rather than defaulting to a
            // rejection - see DiscoveredDevice.TrustsUs.
            if (doc.RootElement.TryGetProperty("trustsCaller", out var trustsCallerProp) &&
                trustsCallerProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                var trustsUs = trustsCallerProp.ValueKind == JsonValueKind.True;
                if (trustsUs != device.TrustsUs)
                {
                    device.TrustsUs = trustsUs;
                    changed = true;
                }
            }
            // Same present-and-boolean-only treatment as trustsCaller, with the
            // opposite resting state: absent or null leaves WeAreAdmin as it
            // was, which starts out false.
            if (doc.RootElement.TryGetProperty("callerIsAdmin", out var adminProp) &&
                adminProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                var weAreAdmin = adminProp.ValueKind == JsonValueKind.True;
                if (weAreAdmin != device.WeAreAdmin)
                {
                    device.WeAreAdmin = weAreAdmin;
                    changed = true;
                }
            }
            if (doc.RootElement.TryGetProperty("libraryToken", out var tokenProp) &&
                tokenProp.GetString() is { } libraryToken && libraryToken != device.LibraryToken)
            {
                device.LibraryToken = libraryToken;
                changed = true;
            }
            // Where this peer says it can be reached. Replaced wholesale rather
            // than merged: an address the peer has stopped reporting is one it
            // no longer has, and merging would leave a client probing a stale
            // one forever. Absent (an older peer) leaves what we had, since
            // "didn't say" is not "no longer reachable there".
            if (doc.RootElement.TryGetProperty("addresses", out var addressesProp) &&
                addressesProp.ValueKind == JsonValueKind.Array)
            {
                var addresses = addressesProp.EnumerateArray()
                    .Select(a => a.GetString())
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .Select(a => a!)
                    .ToList();
                if (!addresses.SequenceEqual(device.Addresses))
                {
                    device.Addresses = addresses;
                    changed = true;
                }
            }
            // It answered, so whatever it was before, it is reachable now. Only
            // ever false for a remembered peer (a discovered one that stops
            // answering is pruned instead), which is exactly the case where the
            // flip back to true is the interesting event.
            if (!device.IsResponding)
            {
                device.IsResponding = true;
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
            // The peer definitely answered (we got past GetStringAsync above),
            // so this is a real bug - our JSON parsing or its /info response
            // shape, not a reachability problem - and must not count towards
            // MaxConsecutiveResolveFailures/pruning a peer that is still there.
            // Loud on purpose: unlike a flaky connection, this won't clear up
            // on its own on the next poll.
            _logger.LogWarning(ex, "Peer {InstanceName} at {EndPoint} answered /info but the response could not be parsed",
                device.InstanceName, device.BaseUri);
        }
    }

    // A GetStringAsync failure against /info: covers both a genuinely
    // unreachable peer (down, out of range, stale mDNS-cached address) and a
    // peer that is alive but didn't answer within Http's timeout (busy,
    // overloaded, transient network stall) - the two aren't distinguishable
    // from here, and the retry/eventual-prune handling below is the right
    // response to either.
    // Whether a failed /info call is just "nothing there" - the peer is off, out
    // of range, sitting behind an address mDNS cached before it moved, or alive
    // and refusing us - rather than something that needs looking into. This is
    // the normal steady state of polling a peer that has gone away, and it is
    // not an error: attaching the exception makes the Log window render a stack
    // trace for a connection refused, which reads like a crash and says nothing
    // the message doesn't already. Anything else - a malformed response, a bug
    // in this method - still gets the full exception.
    internal static bool IsRoutineUnreachable(Exception ex) => ex switch
    {
        // HttpClient's own timeout surfaces as a cancellation, not a
        // TimeoutException.
        OperationCanceledException or TimeoutException or SocketException => true,
        // A status code means the peer answered and said no (chiefly a 403 from
        // a server that doesn't trust this device yet) - a real answer, and
        // nothing a stack trace explains.
        HttpRequestException { StatusCode: not null } => true,
        HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError } => true,
        HttpRequestException { InnerException: SocketException } => true,
        // A TLS handshake this side refused. Routine because it is what a
        // still-starting server looks like from here: PeerHttpClient pins the
        // peer's certificate against the public key /info itself is on its way
        // to fetch, so the first poll after a restart - or the first ever, for
        // a remembered server whose key is not learned yet - is rejected by our
        // own RemoteCertificateValidationCallback and then succeeds on a later
        // one. The stack trace is always the same six frames of SslStream and
        // explains nothing; which certificate was refused, and why, is in the
        // inner message that Describe pulls out below.
        HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } => true,
        AuthenticationException => true,
        _ => false,
    };

    // HttpRequestException's own message for a TLS failure is the useless "The
    // SSL connection could not be established, see inner exception." - so for
    // the one-line form, follow the chain and say what actually went wrong.
    // Only the innermost message is appended: the intermediate ones are the
    // same generic wrapper text at every level.
    internal static string Describe(Exception ex)
    {
        var innermost = ex;
        while (innermost.InnerException is { } inner)
            innermost = inner;

        return ReferenceEquals(innermost, ex) ? ex.Message : $"{ex.Message} -> {innermost.Message}";
    }

    private void HandleUnreachable(DiscoveredDevice device, Exception ex)
    {
        // Peer unreachable or not running the /info endpoint yet - keep the
        // mDNS-name fallback alias rather than failing the discovery, unless
        // this is its MaxConsecutiveResolveFailures'th miss in a row, in
        // which case treat it the same as an mDNS goodbye - see
        // MaxConsecutiveResolveFailures's own doc comment.
        //
        // Full exception (with stack trace) only for a failure that is worth
        // diagnosing, and then only on the first miss for this peer - a
        // still-unreachable peer fails again every AliasPollInterval until it's
        // pruned, and logging the same multi-line stack trace on every one of
        // those is exactly the kind of log flood that made the Log window
        // sluggish to render (see LogViewModel). A one-line message carries the
        // same "still failing" information for the repeats.
        var attempt = _consecutiveResolveFailures.GetValueOrDefault(device.InstanceName);
        if (attempt == 0 && !IsRoutineUnreachable(ex))
            _logger.LogDebug(ex, "Could not resolve /info for {InstanceName} at {EndPoint}", device.InstanceName, device.BaseUri);
        else if (attempt == 0)
            _logger.LogDebug("Could not resolve /info for {InstanceName} at {EndPoint}: {Message}",
                device.InstanceName, device.BaseUri, Describe(ex));
        else
            _logger.LogDebug("Still could not resolve /info for {InstanceName} at {EndPoint}: {Message}",
                device.InstanceName, device.BaseUri, Describe(ex));

        // A link-local address failing doesn't mean the peer is gone -
        // see OnInstanceFound's own comment - it just means this
        // particular address was never going to work, and pruning the
        // peer over it would only force the exact same discover/fail/
        // prune cycle to repeat the next time it re-announces the same
        // way. Left un-pruned, it just waits quietly for a routable
        // address to actually replace it (OnInstanceFound already
        // handles that half).
        if (device.Ip?.IsIPv6LinkLocal == true)
            return;

        // A remembered peer is never pruned - see DiscoveredDevice.IsRemembered.
        // It goes quiet instead, and any consumer that cares (chiefly
        // PairedServerReachability) is told so it can fall back to another
        // candidate. Pruning it would delete the only record of how to reach a
        // server the user is not currently near.
        if (device.IsRemembered)
        {
            if (device.IsResponding)
            {
                device.IsResponding = false;
                _logger.LogInformation(
                    "Remembered peer {InstanceName} at {EndPoint} is not answering; keeping it and marking it unreachable",
                    device.InstanceName, device.BaseUri);
                DeviceDiscovered?.Invoke(this, device);
            }

            return;
        }

        var failures = _consecutiveResolveFailures.AddOrUpdate(device.InstanceName, 1, (_, count) => count + 1);
        if (failures >= MaxConsecutiveResolveFailures && _knownDevices.TryRemove(device.InstanceName, out _))
        {
            _consecutiveResolveFailures.TryRemove(device.InstanceName, out _);
            _logger.LogInformation("Peer {InstanceName} unreachable after {Failures} consecutive /info attempts - treating as gone",
                device.InstanceName, failures);
            DeviceLost?.Invoke(this, device.InstanceName);
        }
    }

    // Resolves a peer by its stable Fingerprint (not the mDNS instance name keying
    // _knownDevices above) - used wherever a placeholder Track's OriginDeviceFingerprint
    // needs turning into an actual reachable endpoint, e.g. LibraryDownloadService's
    // audio download and AlbumArtLoader's synced-art fetch.
    public DiscoveredDevice? FindByFingerprint(string fingerprint) =>
        _knownDevices.Values.FirstOrDefault(d => d.Fingerprint == fingerprint);

    // Every server currently known on the LAN, regardless of trust or
    // pairing - what MainViewModel.AvailableServers offers. Snapshot, not a
    // live view - callers that need to
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
    //
    // Which entry of a group wins is no longer arbitrary now that the same
    // server can legitimately appear twice - once seen on this link, once
    // remembered from an address it told us about. Answering beats not
    // answering, and among those that answer the route is picked by rank (see
    // ReachRank): being here on this link beats the LAN address, which beats
    // the tailnet, which may be relayed.
    public IReadOnlyCollection<DiscoveredDevice> KnownDevices =>
        _knownDevices.Values
            .GroupBy(d => string.IsNullOrEmpty(d.Fingerprint) ? $"instance:{d.InstanceName}" : $"fingerprint:{d.Fingerprint}")
            .Select(g => g
                .OrderByDescending(d => d.IsResponding)
                .ThenBy(ReachRank)
                // Same peer, same network, two schemes: prefer TLS. ReachRank
                // deliberately asks only which network an address is on, so
                // without this the https and http origins of one server tie and
                // whichever the server happened to list first wins.
                //
                // Below IsResponding on purpose. A server whose certificate this
                // device cannot validate - a real one that expired, a self-signed
                // one from a key this device never paired with - fails the probe
                // rather than answering it, so it is the plain origin that gets
                // used, not nothing. See PeerHttpClient.
                .ThenByDescending(d => d.BaseUri.Scheme == Uri.UriSchemeHttps)
                .First())
            .ToList();

    // How good a route to a peer this entry is, lowest first. Only meaningful
    // between entries for the same peer.
    //
    // The tailnet ranks below the LAN rather than beside it because the two are
    // not equivalent when both work: a 100.64/10 address may be carried by a
    // DERP relay rather than a direct WireGuard path, so at home the LAN
    // address is the one to use. This is what makes walking back through the
    // front door quietly restore the direct route.
    internal static int ReachRank(DiscoveredDevice device)
    {
        if (!device.IsRemembered)
            return 0;

        // A name that has never resolved cannot be classified, and guessing
        // would rank it as though it were on the LAN. Unknown sorts last.
        if (device.Ip is not { } address)
            return 3;

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            if (b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168))
                return 1;
            if (b[0] == 100 && b[1] is >= 64 and <= 127) // Tailscale's CGNAT range
                return 2;
        }
        else if (address.AddressFamily == AddressFamily.InterNetworkV6 && (address.GetAddressBytes()[0] & 0xFE) == 0xFC)
        {
            return 1; // fc00::/7, a unique-local address - someone's own network
        }

        return 3;
    }

    // Adds a peer by address rather than by sighting: an address a paired
    // server reported for itself (the normal case, see
    // PairedServerReachability) or one a user typed for a server they have
    // never shared a network with (the bootstrap case).
    //
    // Returns the entry once /info has been attempted, so a caller can tell a
    // working address from a typo immediately rather than leaving a dead row in
    // the UI. Null only when the address cannot be parsed or resolved at all -
    // a resolvable address that does not answer still yields an entry, since
    // "my server, currently unreachable" is a state worth holding on to.
    public async Task<DiscoveredDevice?> AddRememberedAsync(string address, CancellationToken token = default)
    {
        var route = await ResolveOriginAsync(address, token);
        if (route == null)
        {
            _logger.LogInformation("Could not resolve remembered address {Address}", address);
            return null;
        }

        var (baseUri, ip) = route.Value;

        // Keyed by the address as written, not by the resolved endpoint, so
        // re-adding after a DHCP or tailnet address change updates the entry in
        // place instead of accumulating one per address the name ever had.
        var instanceName = RememberedInstancePrefix + address;
        var device = _knownDevices.AddOrUpdate(
            instanceName,
            _ => NewRemembered(instanceName, address, baseUri, ip),

            // Replaced rather than mutated when the address behind the name
            // moves, matching how OnInstanceFound handles the same situation -
            // BaseUri is init-only, and a peer at a new address is a new route
            // to it rather than an edit of the old one. Identity carries over,
            // since it is the same server; IsResponding does not, because
            // whether the *new* address answers is not yet known.
            //
            // Compared on the resolved address as well as the origin: the name
            // is what we dial, but a name that now points somewhere else is a
            // different route even though the URL is character-for-character
            // the same.
            (_, existing) => existing.BaseUri == baseUri && Equals(existing.Ip, ip)
                ? existing
                : new DiscoveredDevice
                {
                    InstanceName = instanceName,
                    BaseUri = baseUri,
                    Ip = ip,
                    Alias = existing.Alias,
                    Fingerprint = existing.Fingerprint,
                    PublicKey = existing.PublicKey,
                    DeviceType = existing.DeviceType,
                    TrustsUs = existing.TrustsUs,
                    WeAreAdmin = existing.WeAreAdmin,
                    LibraryToken = existing.LibraryToken,
                    Addresses = existing.Addresses,
                    IsRemembered = true,
                    IsResponding = false,
                });

        await ResolveAliasAsync(device);
        return device;
    }

    private static DiscoveredDevice NewRemembered(string instanceName, string address, Uri baseUri, IPAddress? ip) =>
        new()
        {
            InstanceName = instanceName,
            BaseUri = baseUri,
            Ip = ip,

            // The address stands in as the display name until /info supplies
            // the real one, exactly as the mDNS instance name does for a
            // discovered peer.
            Alias = address,
            IsRemembered = true,

            // Until /info answers, this is an address and nothing more -
            // claiming otherwise would let a consumer act on a server that may
            // not be there.
            IsResponding = false,
        };

    public void RemoveRemembered(string address)
    {
        var instanceName = RememberedInstancePrefix + address;
        if (_knownDevices.TryRemove(instanceName, out _))
            DeviceLost?.Invoke(this, instanceName);
    }

    // Distinguishes a remembered entry's key from an mDNS instance name, which
    // is whatever the peer advertised.
    private const string RememberedInstancePrefix = "remembered:";

    // The address as the user or the server originally wrote it, recovered from
    // the key. Kept rather than the resolved endpoint precisely so a name can be
    // looked up again later - see the poll loop.
    private static string? RememberedAddressOf(DiscoveredDevice device) =>
        device.InstanceName.StartsWith(RememberedInstancePrefix, StringComparison.Ordinal)
            ? device.InstanceName[RememberedInstancePrefix.Length..]
            : null;

    // An origin as a server reported it or a user typed it, in any of the forms
    // either produces: "host", "host:port", "1.2.3.4:port", "[::1]:port",
    // "https://name" or "http://1.2.3.4:4533".
    //
    // Two defaults, and they differ on purpose. Without a scheme this is a
    // hand-typed LAN address, so it means http on Flower.Server's port - not
    // SyncProtocol.DefaultPort (53317), which is the app-to-app one, because
    // what a user types an address for is overwhelmingly the headless server
    // they cannot discover. *With* a scheme, the scheme's own default port
    // applies, so "https://music.example.com" means 443 and needs no ":443"
    // spelled out - that address came from a tunnel or a certificate, where the
    // standard port is the expected one.
    //
    // Returns the origin to dial and, separately, the address it resolved to.
    // The origin keeps the host as written so TLS validates against the name;
    // the resolved address is for ranking only. See DiscoveredDevice.BaseUri.
    private async Task<(Uri BaseUri, IPAddress? Ip)?> ResolveOriginAsync(string address, CancellationToken token)
    {
        var trimmed = address.Trim();
        if (trimmed.Length == 0)
            return null;

        if (!Uri.TryCreate(HasScheme(trimmed) ? trimmed : $"http://{trimmed}", UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return null;

        // IsDefaultPort distinguishes "no port was written" from one that was,
        // and only the former gets Flower.Server's port - a scheme the user
        // spelled out keeps its own default instead.
        var baseUri = !HasScheme(trimmed) && uri.IsDefaultPort
            ? new UriBuilder(uri) { Port = FlowerServerPort }.Uri
            : uri;

        var host = uri.Host.Trim('[', ']');
        if (IPAddress.TryParse(host, out var literal))
            return (baseUri, literal);

        try
        {
            var resolved = await Dns.GetHostAddressesAsync(host, token);
            return resolved.Length == 0 ? null : (baseUri, resolved[0]);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return null;
        }
    }

    private static bool HasScheme(string address) =>
        address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        address.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    // An mDNS sighting is always plain http at a literal address: the peer is on
    // this link, there is no name for a certificate to be issued for, and the
    // app-to-app server does not serve TLS.
    internal static Uri HttpOrigin(IPEndPoint endPoint) =>
        new($"http://{endPoint}");

    // Flower.Server's default listening port - deliberately not
    // SyncProtocol.DefaultPort (53317), which is what the app-to-app listener
    // used to bind before it was removed.
    private const int FlowerServerPort = 4533;

    private static bool IsOurServiceType(string instanceName) =>
        instanceName.EndsWith($"{ServiceType}.local", StringComparison.OrdinalIgnoreCase);

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
        _http.Dispose();
    }
}
