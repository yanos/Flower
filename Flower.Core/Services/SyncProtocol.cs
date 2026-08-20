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
public sealed record SyncInfoResponseDto(
    string Alias, string Version, string? DeviceModel, string DeviceType,
    string Fingerprint, string PublicKey, bool IsServer, bool Download, bool? TrustsCaller,
    string LibraryToken);
