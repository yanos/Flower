namespace Flower.Services;

// Pure decision logic for the Client/Server pairing topology - see
// AppSettings.IsServer/PairedServerFingerprint. Kept separate from
// MainViewModel/SyncHttpServer/PeerTrackResolver (which own the actual
// triggering/HTTP I/O) so the decisions themselves are unit-testable without
// those services' setup.
public static class SyncRolePolicy
{
    // A Server never initiates outbound bulk sync (see MainViewModel's two
    // trigger paths, TriggerSyncIfReady/DebouncedContentSyncAsync); a Client
    // bulk-syncs only with its one paired Server, never any other
    // discovered/trusted peer - unlike the pre-role mesh model where every
    // trusted peer synced with every other.
    public static bool ShouldInitiateSync(bool isServer, string? pairedServerFingerprint, string? peerFingerprint) =>
        !isServer && MayRequestFrom(pairedServerFingerprint, peerFingerprint);

    // Whether this device is currently allowed to dial a given peer at all for
    // an authenticated request - a bulk sync GET/POST, an on-demand stream URL,
    // a placeholder download, or an album-art fetch for a synced-but-not-local
    // track. Same "exactly one paired Server, nothing else" rule
    // ShouldInitiateSync applies for bulk sync, generalized so PeerTrackResolver
    // (the one and only caller for every other peer-directed request - see its
    // own doc comment) can consult this single place instead of each of its
    // callers re-deriving it - a stale Track.OriginDeviceFingerprint (left over
    // from before an Unpair, or a switch to a different Server) can't quietly
    // resolve to some other peer just because that old fingerprint still
    // happens to be reachable on the LAN. A Server never has a
    // pairedServerFingerprint of its own (see MainViewModel.IsServer's
    // setter), so this is naturally always false for one.
    public static bool MayRequestFrom(string? pairedServerFingerprint, string? peerFingerprint) =>
        !string.IsNullOrEmpty(peerFingerprint) &&
        !string.IsNullOrEmpty(pairedServerFingerprint) &&
        peerFingerprint == pairedServerFingerprint;

    // Defense-in-depth for SyncHttpServer's bulk-sync endpoints only (see
    // IsAuthorized) - a correctly-behaving Server never initiates outbound
    // bulk sync at all (see ShouldInitiateSync above), so this should never
    // actually need to trigger, but a receiving Server should still refuse an
    // incoming bulk-sync request from a caller that also claims to be a
    // Server. Deliberately NOT applied to the browse/stream endpoints (/rest/*
    // other than the bulk manifest ones) - those stay open to any trusted
    // peer regardless of role, see SYNC-PLAN.md's browsing feature.
    public static bool ShouldRejectPeerAsServer(bool weAreServer, bool callerIsServer) =>
        weAreServer && callerIsServer;
}
