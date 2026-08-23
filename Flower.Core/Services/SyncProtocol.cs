using System.Collections.Generic;

namespace Flower.Services;

// The three facts both ends of LAN discovery have to agree on, in the one place
// neither end owns privately. Before this they lived in the Flower app project:
// the service type inside NetworkDiscoveryService, the port and the /info
// response shape inside SyncHttpServer. Flower.Server cannot reference that
// project (it is the Avalonia one), so a server that wanted to be discoverable
// had no way to advertise the name the client browses for or answer the handshake
// the client makes - which is exactly why it never appeared in the sidebar.
public static class SyncProtocol
{
    // What NetworkDiscoveryService browses for and every discoverable Flower
    // instance - app or server - advertises itself as.
    public const string ServiceType = "_flowersync._tcp";

    // LocalSend's own default. Only a starting point: SyncHttpServer walks
    // upward from here when it is taken, and discovery never assumes it, since
    // the real port arrives in the mDNS SRV record.
    public const int DefaultPort = 53317;

    // LocalSend's identity handshake, which this protocol borrows wholesale.
    // A peer is not usable until this resolves - see ResolveAliasAsync.
    public const string InfoPath = "/api/localsend/v2/info";
}

// Wire shape for the InfoPath response - camelCase field names come from the
// serializer context's naming policy on each side. NetworkDiscoveryService also
// reads this JSON by raw lowercase property name (e.g. "alias"/"trustsCaller"),
// so the casing is load-bearing, not cosmetic.
//
// TrustsCaller is null rather than false when the caller did not identify
// itself, so a plain unauthenticated probe cannot be misread as a rejection.
//
// Addresses is how a client keeps hold of a server it can no longer discover.
// mDNS is link-local, so a client off the home network cannot see the server at
// all - and before this, reachability *was* discovery, which made a paired
// server simply vanish the moment its client left the house. The server reports
// every address it believes it can be reached on (see LocalAddresses), the
// client remembers them for the server it paired with, and probes them in rank
// order later. Empty on a peer that could not enumerate its interfaces, which
// reads as "discover me the old way".
//
// Each entry is a full origin - "http://192.168.1.40:4533",
// "https://music.example.com" - not a bare host:port. The scheme has to travel
// with the address because only the server knows whether it is behind TLS, and
// a client that assumed http could never reach one that is.
public sealed record SyncInfoResponseDto(
    string Alias, string Version, string? DeviceModel, string DeviceType,
    string Fingerprint, string PublicKey, bool IsServer, bool Download, bool? TrustsCaller,
    string LibraryToken, List<string>? Addresses = null);
