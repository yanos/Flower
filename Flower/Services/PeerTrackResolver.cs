using Flower.Models;

namespace Flower.Services;

// The single place a placeholder Track's origin peer gets resolved for any
// outbound request against it - a remote album art fetch (AlbumArtLoader), an
// on-demand stream URL, or a placeholder download (the latter two via
// MainViewModel). Every one of those calls Resolve() instead of separately
// looking the fingerprint up via NetworkDiscoveryService and separately
// checking SyncRolePolicy.MayRequestFrom - callers don't need to know "only
// the currently paired Server" is even a rule, just that a null result means
// "don't send this request."
//
// Deliberately NOT used by PeerLibraryViewModel, which browses an arbitrary
// trusted peer's library by design (not just the paired Server).
public class PeerTrackResolver
{
    private readonly PairedServerReachability _reachability;

    public PeerTrackResolver(PairedServerReachability reachability)
    {
        _reachability = reachability;
    }

    public DiscoveredDevice? Resolve(Track track) =>
        SyncRolePolicy.MayRequestFrom(_reachability.PairedServerFingerprint, track.OriginDeviceFingerprint)
            ? _reachability.PairedServerDevice
            : null;
}
