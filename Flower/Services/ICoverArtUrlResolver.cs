using System;

using Flower.Models;

namespace Flower.Services;

// Where to fetch a placeholder track's album art from - the art half of the
// question IStreamUrlResolver answers for playback, and split out for the same
// reason: the two heads resolve it completely differently, and AlbumArtLoader
// has no business knowing which one it is running on.
//
// Synchronous, unlike IStreamUrlResolver.ResolveAsync. That interface is a task
// because the browser genuinely has to go to the network to mint a stream
// ticket for an <audio> element that cannot carry credentials of its own. Art
// is fetched by AlbumArtLoader's own HttpClient, which signs like any other
// call, so there is nothing to mint and nothing to await here - both
// implementations below are pure string building. (The signing itself is
// asynchronous now, but that happens in AlbumArtLoader, not in a resolver.)
public interface ICoverArtUrlResolver
{
    // Null when this track's art cannot be fetched right now - no origin peer,
    // the peer is not one this device may still talk to, or the track carries
    // nothing to address the request with. The caller's job is then to show the
    // placeholder icon, not to substitute anything.
    string? Resolve(Track track);

    // Whether to ask for a fresh connection per request. True against a peer's
    // embedded SyncHttpServer, whose HttpListener (or the OS) may have torn a
    // pooled keep-alive connection down without saying so - the same hazard
    // RemoteLibraryImporter's closeConnection covers. False in the browser,
    // where the fetch stack owns connection reuse and the header is not ours
    // to set.
    bool ClosesConnection => false;
}

// The app's implementation: whichever peer currently holds the track, asked
// over the OpenSubsonic surface every other peer call goes through.
public sealed class PeerCoverArtUrlResolver(PeerTrackResolver peerTrackResolver) : ICoverArtUrlResolver
{
    public bool ClosesConnection => true;

    public string? Resolve(Track track)
    {
        // PeerTrackResolver owns the "only the currently paired Server" rule -
        // see its own doc comment. Deliberately silent on a miss, unlike
        // PeerStreamUrlResolver's logged warning: a failed play attempt is
        // user-visible and worth a line, whereas this is called once per row of
        // a scrolling list and a peer being absent is routine.
        var peer = peerTrackResolver.Resolve(track);
        if (peer == null)
            return null;

        var albumId = LibraryOpenSubsonicMapper.AlbumIdFor(track);
        return peer.Url($"/rest/getCoverArt?id={Uri.EscapeDataString(albumId)}").ToString();
    }
}

// The browser head's implementation: the origin server the page was served
// from, over the Flower sync surface rather than /rest.
//
// /rest is the wrong door here for the same reason it was for the catalog: it
// authenticates with the classic Subsonic credential scheme, and a tab has no
// Subsonic credential. GET /api/flower/v1/cover-art sits behind the same signed
// gate as GET /library, so the key that fetched the catalog also fetches the art
// for it - no ticket, no second credential.
//
// Addressed by the album id recomputed from this track's own tags rather than
// by the CoverArt value the manifest carried. The two agree today (see
// SubsonicIdentity.AlbumIdFor, which is what both sides compute), and deriving
// it keeps this resolver symmetric with the peer one above instead of quietly
// depending on what a particular server chose to put in that field.
public sealed class OriginCoverArtUrlResolver(Uri baseAddress) : ICoverArtUrlResolver
{
    public string? Resolve(Track track)
    {
        if (string.IsNullOrEmpty(track.Album) && string.IsNullOrEmpty(track.EffectiveAlbumArtist))
            return null;

        var albumId = LibraryOpenSubsonicMapper.AlbumIdFor(track);
        return new Uri(baseAddress, $"/api/flower/v1/cover-art?id={Uri.EscapeDataString(albumId)}").ToString();
    }
}
