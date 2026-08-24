namespace Flower.Services;

// Pure decision logic for who this device is allowed to talk to - see
// AppSettings.PairedServerFingerprint. Kept separate from
// MainViewModel/PeerSyncCoordinator/PeerTrackResolver (which own the actual
// triggering and HTTP I/O) so the decision itself is unit-testable without
// those services' setup.
//
// It used to be a topology: every device could be flipped into Server mode and
// the rules decided which direction sync flowed. There is no topology left -
// only Flower.Server serves, and a client dials exactly one of them - so what
// survives is the single question every caller was really asking.
public static class SyncRolePolicy
{
    // Whether this device is currently allowed to dial a given peer at all for
    // an authenticated request - a bulk sync GET/POST, an on-demand stream URL,
    // a placeholder download, or an album-art fetch for a synced-but-not-local
    // track. "Exactly one paired server, nothing else", in one place, so
    // PeerTrackResolver (the one and only caller for every peer-directed
    // request - see its own doc comment) can consult it instead of each of its
    // callers re-deriving it: a stale Track.OriginDeviceFingerprint (left over
    // from before an Unpair, or a switch to a different server) can't quietly
    // resolve to some other peer just because that old fingerprint still
    // happens to be reachable on the LAN.
    public static bool MayRequestFrom(string? pairedServerFingerprint, string? peerFingerprint) =>
        !string.IsNullOrEmpty(peerFingerprint) &&
        !string.IsNullOrEmpty(pairedServerFingerprint) &&
        peerFingerprint == pairedServerFingerprint;
}
