namespace Flower.Services;

// Pure decision logic for the Client/Server bulk-sync topology - see
// AppSettings.IsServer/PairedServerFingerprint. Kept separate from
// MainViewModel/SyncHttpServer (which own the actual triggering/HTTP I/O) so
// the decisions themselves are unit-testable without those services' setup.
public static class SyncRolePolicy
{
    // A Server never initiates outbound bulk sync (see MainViewModel's two
    // trigger paths, TriggerSyncIfReady/DebouncedContentSyncAsync); a Client
    // bulk-syncs only with its one paired Server, never any other
    // discovered/trusted peer - unlike the pre-role mesh model where every
    // trusted peer synced with every other.
    public static bool ShouldInitiateSync(bool isServer, string? pairedServerFingerprint, string? peerFingerprint) =>
        !isServer &&
        !string.IsNullOrEmpty(peerFingerprint) &&
        peerFingerprint == pairedServerFingerprint;

    // Defense-in-depth for SyncHttpServer's bulk-sync endpoints only (see
    // AuthorizeAsync) - a correctly-behaving Server never initiates outbound
    // bulk sync at all (see ShouldInitiateSync above), so this should never
    // actually need to trigger, but a receiving Server should still refuse an
    // incoming bulk-sync request from a caller that also claims to be a
    // Server. Deliberately NOT applied to the browse/stream endpoints (/rest/*
    // other than the bulk manifest ones) - those stay open to any trusted
    // peer regardless of role, see SYNC-PLAN.md's browsing feature.
    public static bool ShouldRejectPeerAsServer(bool weAreServer, bool callerIsServer) =>
        weAreServer && callerIsServer;
}
