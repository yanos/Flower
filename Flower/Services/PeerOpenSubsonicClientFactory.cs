using Flower.Persistence;

namespace Flower.Services;

// Builds an OpenSubsonicClient pointed at a peer's embedded SyncHttpServer
// host, with the same plain-HTTP identity headers every other peer-to-peer
// call uses (see PlaylistSyncService.AddIdentityHeaders) rather than real
// OpenSubsonic credentials - both LibraryDownloadService (the download-button
// feature) and PeerLibraryViewModel (live browsing/streaming, unrestricted by
// role - see SyncRolePolicy) go through this one factory rather than each
// duplicating the same construction.
public static class PeerOpenSubsonicClientFactory
{
    public static OpenSubsonicClient Create(DiscoveredDevice peer, DeviceIdentity identity, AppSettings appSettings) =>
        new($"http://{peer.EndPoint}", username: "", password: "",
            extraHeaders:
            [
                ("X-Flower-Fingerprint", identity.Fingerprint),
                ("X-Flower-Alias", identity.Alias),
                ("X-Flower-Role", appSettings.IsServer ? "server" : "client"),
            ]);
}
