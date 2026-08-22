using Microsoft.Extensions.Logging;

using Flower.Models;
using Flower.Persistence;

namespace Flower.Services;

// Turns a placeholder Track - one this device knows about from a peer's
// catalog but has never downloaded, so Path is null (see SYNC-PLAN.md Phase
// 3's data model) - into something IAudioManager can actually open.
//
// This exists because the resolution used to live *above*
// PlaylistControlViewModel, in MainViewModel.PlayResolvingPlaceholder, which
// meant only the callers that remembered to go through MainViewModel got it.
// Everything inside PlaylistControlViewModel - auto-advance on EndReached, the
// skip-on-failure handler, Next, Previous, PlayOrPause's own first-track
// fallback - called Play() directly and handed the raw placeholder to the
// decoder, which throws (TrackDecoder.EnsureMedia). Manual play worked and
// auto-advance onto an un-downloaded track did not, which is the "next song
// doesn't always play" report in docs/todo.txt. Resolution now happens inside
// Play() itself, so there is no entry point left that can skip it.
//
// An interface rather than a method on PeerSyncCoordinator because the browser
// head resolves the same question completely differently: it has no mDNS peer
// to discover (its server is its own origin) and no signing key to build an
// authenticated URL with, so it mints a short-lived stream ticket instead - see
// SYNC-PLAN.md's "The browser's library", seam 4.
public interface IStreamUrlResolver
{
    // Null when this track cannot be streamed right now - no origin peer, the
    // peer is unreachable, or this device is not in a position to ask. The
    // caller's job is then to not play it, not to substitute anything.
    string? Resolve(Track track);
}

// The app's implementation: an on-demand OpenSubsonic stream URL from whichever
// peer currently holds the track, so it plays without being downloaded first.
// Moved here wholesale from PeerSyncCoordinator.GetStreamUrl, which had no
// business being on a sync coordinator except that MainViewModel was the only
// caller that could reach it.
public sealed class PeerStreamUrlResolver(
    PeerTrackResolver peerTrackResolver,
    DeviceIdentity deviceIdentity,
    DeviceSigningKey signingKey,
    AppSettings appSettings,
    ILogger<PeerStreamUrlResolver> logger) : IStreamUrlResolver
{
    public string? Resolve(Track track)
    {
        // The id the origin peer itself gave this track, not one recomputed
        // here - see Track.OriginTrackId. Absent only for a track that never
        // came from a peer at all, which has no business on this path.
        if (track.OriginTrackId == null)
        {
            logger.LogWarning("Cannot build a stream URL for {Title}: it carries no origin track id", track.Title);
            return null;
        }

        // PeerTrackResolver owns the "only the currently paired Server" gating
        // - see its own doc comment. The warning is worth logging here because
        // a play attempt failing is user-visible, unlike AlbumArtLoader's much
        // more frequent per-row calls into the same resolver.
        var peer = peerTrackResolver.Resolve(track);
        if (peer == null)
        {
            logger.LogWarning("Cannot resolve a peer for {Title}: no currently paired, reachable origin device", track.Title);
            return null;
        }

        var url = PeerOpenSubsonicClientFactory
            .Create(peer, deviceIdentity, appSettings, signingKey)
            .GetStreamUrl(track.OriginTrackId);
        logger.LogInformation("Streaming {Title} from {Alias} ({EndPoint})", track.Title, peer.Alias, peer.EndPoint);
        return url;
    }
}
