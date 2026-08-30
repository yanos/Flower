using System.Collections.Generic;

namespace Flower.Services;

// The one place that decides which address to dial for a peer.
//
// A single server is legitimately known at several addresses at once - seen on
// this link by mDNS, remembered at its LAN address, at its tailnet address, at
// whatever it reports for itself from outside - and every one of those arrives
// as its own DiscoveredDevice under the same Fingerprint. Something has to
// choose, and for a long time three different things did: the ranked pick in
// NetworkDiscoveryService.KnownDevices, an unranked FirstOrDefault in
// FindByFingerprint, and DeviceSidebarSection overwriting a row's endpoint with
// whichever sighting reported last. Streaming, downloads, art, playlist sync
// and the log push each reached the peer through one of those, so which address
// a request went out on depended on which door it happened to come through -
// and the sidebar's door could pin the whole app to a public address while the
// server sat on the same WiFi.
//
// Implemented by NetworkDiscoveryService, which owns the sightings. Every
// caller that needs an endpoint asks here; nobody else ranks addresses.
public interface IPeerEndpointResolver
{
    // The address to use for this peer right now, or null if it is not known
    // at any address at all. Ranked, so it is the same answer every caller
    // gets at the same moment - see NetworkDiscoveryService.ReachRank.
    DiscoveredDevice? EndpointFor(string fingerprint);

    // The same decision applied to every peer known at all: one entry each,
    // already resolved to the address to dial.
    IReadOnlyCollection<DiscoveredDevice> KnownDevices { get; }
}
