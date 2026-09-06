using Flower.Persistence;

namespace Flower.Services;

// Builds a PeerMediaClient pointed at a paired server, with the signed
// device-identity credentials every call to it uses (see
// SignedDeviceCredentials). Both LibraryDownloadService (the download button)
// and PeerStreamUrlResolver (playback) go through this one factory rather than
// each duplicating the same construction.
//
// It used to hand the client a username and password too - empty strings, since
// a paired device authenticates by signature and holds no Subsonic credential.
// Those went with /rest: the media routes are Flower's own now, and their gate
// has never accepted anything but a signature or a stream ticket.
public static class PeerMediaClientFactory
{
    public static PeerMediaClient Create(DiscoveredDevice peer, DeviceIdentity identity, AppSettings appSettings, DeviceSigningKey signingKey) =>
        new(peer.Origin, credentials: new SignedDeviceCredentials(identity, signingKey));
}
