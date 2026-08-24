using System.Collections.Generic;

namespace Flower.Services;

// The three facts both ends of LAN discovery have to agree on, in the one place
// neither end owns privately. Before this they lived in the Flower app project,
// which Flower.Server cannot reference (it is the Avalonia one), so a server
// that wanted to be discoverable had no way to advertise the name the client
// browses for or answer the handshake the client makes - which is exactly why
// it never appeared in the sidebar.
public static class SyncProtocol
{
    // What NetworkDiscoveryService browses for and every discoverable Flower
    // server advertises itself as, and the client browses for.
    public const string ServiceType = "_flowersync._tcp";

    // LocalSend's own default, kept for the protocol lineage. Discovery never
    // assumes it - the real port arrives in the mDNS SRV record - and
    // Flower.Server binds its own port (4533) rather than this one.
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
// CallerIsAdmin follows TrustsCaller's shape and its reasoning: null when the
// caller did not prove who it is, so an anonymous probe never reads as a
// refusal. It is what lets a client decide whether to offer its holder the
// server's administrator-only controls at all - handing out pairing codes,
// most of it - instead of showing a button whose only outcome is the server's
// own 403. Never a permission in itself: the server re-checks
// TrustedPeer.IsAdmin on every admin route regardless of what a client
// believes about itself.
public sealed record SyncInfoResponseDto(
    string Alias, string Version, string? DeviceModel, string DeviceType,
    string Fingerprint, string PublicKey, bool Download, bool? TrustsCaller,
    string LibraryToken, List<string>? Addresses = null, bool? CallerIsAdmin = null);
