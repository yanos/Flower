using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

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
    //
    // A task because one head genuinely cannot answer without going to the
    // network: the browser has to ask its server to mint a ticket for this
    // exact track. Everywhere else the answer is already in hand, the returned
    // task is already completed, and PlaylistControlViewModel.Play therefore
    // still starts playback on the same stack it was called on - see the
    // IsCompleted check there. That is deliberate: an unconditionally
    // asynchronous start would have turned every synchronous caller, and every
    // test that plays a track and asserts on the next line, into a timing
    // question.
    Task<string?> ResolveAsync(Track track);
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
    // Completes synchronously in practice: a signed stream URL is pure
    // computation over the peer's endpoint and this device's key, with nothing
    // to wait for (SignedDeviceCredentials hands back a finished task). The
    // network only gets involved when the audio pipeline opens the URL.
    public async Task<string?> ResolveAsync(Track track)
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

        var url = await PeerOpenSubsonicClientFactory
            .Create(peer, deviceIdentity, appSettings, signingKey)
            .GetStreamUrlAsync(track.OriginTrackId);
        logger.LogInformation("Streaming {Title} from {Alias} ({EndPoint})", track.Title, peer.Alias, peer.BaseUri);
        return url;
    }
}

// What the server hands back from POST /api/flower/v1/stream-tickets - see
// Flower.Server/Endpoints/StreamTicketEndpoints.cs, which assembles Url itself
// so that every caller doesn't concatenate the same thing slightly differently.
public sealed record StreamTicketDto(string Ticket, DateTimeOffset ExpiresAt, string Url);

// The browser head's implementation: ask the server for a short-lived ticket
// for this exact track, and hand back the media URL it returns.
//
// The browser cannot use PeerStreamUrlResolver, and not for the reason it
// originally could not. It signs perfectly well now (BrowserPeerCredentials) -
// but it has no mDNS discovery, so there is no DiscoveredDevice to resolve
// against, and more to the point the thing that opens the URL is an <audio>
// element, which presents nothing at all. So the tab signs a request for a
// ticket and hands the element a URL that carries one.
//
// This is the one place in the app where resolving a track for playback really
// does go to the network, which is why IStreamUrlResolver.ResolveAsync is a
// task at all. The cache below is what keeps that from being felt: a URL
// already minted for a track is returned on the spot (a completed task, so
// playback starts synchronously), and PlaylistControlViewModel primes the
// upcoming track while the current one is still playing, so auto-advance
// normally finds one waiting.
public sealed class StreamTicketUrlResolver(
    HttpClient http, Uri baseAddress, IPeerCredentials credentials,
    ILogger<StreamTicketUrlResolver> logger) : IStreamUrlResolver
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { TypeInfoResolver = FlowerJsonContext.Default };

    // Re-mint rather than reuse once a ticket is inside this of its expiry. A
    // media element issues many requests for one playback - the initial probe,
    // then a range request per seek - so a ticket has to outlive the *track*,
    // not just the request that started it. This margin does not save a track
    // longer than StreamTicketService's whole 15-minute lifetime, whose later
    // seeks will be refused; that is a known limit of ticketed playback and the
    // fix belongs on the server side (a ticket that renews while it is being
    // read), not here.
    private static readonly TimeSpan RenewWithin = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, (string Url, DateTimeOffset ExpiresAt)> _minted = new();

    public async Task<string?> ResolveAsync(Track track)
    {
        // The id the origin server itself gave this track - see
        // Track.OriginTrackId. A track that never came from a server has no
        // business on this path.
        if (track.OriginTrackId == null)
        {
            logger.LogWarning("Cannot mint a stream ticket for {Title}: it carries no origin track id", track.Title);
            return null;
        }

        if (_minted.TryGetValue(track.OriginTrackId, out var cached) &&
            cached.ExpiresAt - DateTimeOffset.UtcNow > RenewWithin)
        {
            return cached.Url;
        }

        try
        {
            var path = $"/api/flower/v1/stream-tickets?id={Uri.EscapeDataString(track.OriginTrackId)}";
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseAddress, path));
            await request.AddPeerCredentialsAsync(credentials);

            using var response = await http.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var ticket = JsonSerializer.Deserialize<StreamTicketDto>(
                await response.Content.ReadAsStringAsync(), Json);
            if (ticket == null)
            {
                logger.LogWarning("Cannot stream {Title}: the server returned an empty stream ticket", track.Title);
                return null;
            }

            // Absolute, because the URL is handed to a media element rather than
            // to this HttpClient - the server returns it origin-relative.
            var url = new Uri(baseAddress, ticket.Url).ToString();
            _minted[track.OriginTrackId] = (url, ticket.ExpiresAt);
            logger.LogInformation("Streaming {Title} on a ticket valid until {ExpiresAt}", track.Title, ticket.ExpiresAt);
            return url;
        }
        catch (Exception ex)
        {
            // Same contract as the peer resolver: a track that cannot be
            // resolved is simply not played, and the reason is logged here
            // rather than thrown at a caller that has no way to act on it.
            logger.LogWarning(ex, "Cannot stream {Title}: minting a stream ticket failed", track.Title);
            return null;
        }
    }
}
