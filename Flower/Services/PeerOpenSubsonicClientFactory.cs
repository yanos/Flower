using Flower.Persistence;

namespace Flower.Services;

// Builds an OpenSubsonicClient pointed at a peer's embedded SyncHttpServer
// host, with the same signed peer-identity credentials every other peer-to-
// peer call uses (see SignedDeviceCredentials) rather than real OpenSubsonic
// credentials - both LibraryDownloadService (the download-button feature) and
// PeerLibraryViewModel (live browsing/streaming, unrestricted by role - see
// SyncRolePolicy) go through this one factory rather than each duplicating the
// same construction.
public static class PeerOpenSubsonicClientFactory
{
    public static OpenSubsonicClient Create(DiscoveredDevice peer, DeviceIdentity identity, AppSettings appSettings, DeviceSigningKey signingKey) =>
        new(peer.Origin, username: "", password: "",
            credentials: new SignedDeviceCredentials(identity, signingKey, appSettings));
}
