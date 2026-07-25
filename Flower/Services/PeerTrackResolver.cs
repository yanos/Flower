using Flower.Models;
using Flower.Persistence;

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
// trusted peer's library by design (not just the paired Server), nor by
// MainViewModel.RefreshRowReachability's IsPeerReachable, which answers "is
// this row's origin device online at all" for display purposes rather than
// "may this device actually be asked for it right now."
public class PeerTrackResolver
{
    private readonly NetworkDiscoveryService _networkDiscovery;
    private readonly AppSettings _appSettings;

    public PeerTrackResolver(NetworkDiscoveryService networkDiscovery, AppSettings appSettings)
    {
        _networkDiscovery = networkDiscovery;
        _appSettings = appSettings;
    }

    public DiscoveredDevice? Resolve(Track track)
    {
        if (track.OriginDeviceFingerprint is not { } fingerprint)
            return null;
        if (!SyncRolePolicy.MayRequestFrom(_appSettings.PairedServerFingerprint, fingerprint))
            return null;
        return _networkDiscovery.FindByFingerprint(fingerprint);
    }
}
