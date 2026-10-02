using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Flower.Services;

// Signs every request an HttpClient makes, freshly, one signature per request.
//
// The audio pipeline is why this exists. A streamed track's URL is built once
// - PeerStreamUrlResolver asks PeerMediaClient.BuildUrlAsync for a URL with
// the whole signed credential set baked into its query string - and then
// handed onward as a plain string in Track.Path. SeekableHttpStream reuses
// that one string for every request it makes: the HEAD probe, the bytes=0-0
// length probe, the body GET, and each reopen after a cut connection.
//
// One URL means one nonce, and a nonce is single-use by design: the server
// records it through NonceReplayGuard.TryRecord, which is a TryAdd. So the
// first request on that URL was accepted and every later one was rejected as a
// replay - the exact case SignatureVerifier's own comment says cannot happen,
// because "a legitimate caller always generates a fresh nonce per attempt".
// That is true of PeerMediaClient.SendAsync, which signs per call. It was
// never true of a URL handed to something else to fetch repeatedly.
//
// What made it invisible was the order. The bytes=0-0 probe went first, so it
// succeeded and the track got a correct length; the body GET that followed was
// refused, and a refusal on this surface is Subsonic's "Wrong username or
// password" on an HTTP 200, per the protocol. SeekableHttpStream read those
// ~130 bytes of JSON as the track's audio, saw the body end far short of the
// length it had just been told, reopened three times - replaying the same
// spent nonce each time - faulted the decoder, and the queue skipped to the
// next track, which failed identically. Five in a row stopped playback. An
// entire album was unplayable while the server's log showed nothing but
// successful one-byte probes.
//
// Signing at the client rather than in the URL fixes it for every request any
// caller makes, without the pipeline below having to know it is authenticated
// at all. The second half of the fix - a refusal must never be mistaken for
// audio - is that Flower.Server refuses with a status and a problem document
// (FlowerProblem) rather than the adapter's error envelope on a 200, and
// SeekableHttpStream already treats any non-success status as a failure.
public sealed class PeerCredentialsHandler(Func<IPeerCredentials?> credentials) : DelegatingHandler
{
    public PeerCredentialsHandler(Func<IPeerCredentials?> credentials, HttpMessageHandler inner)
        : this(credentials) => InnerHandler = inner;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Null is the ordinary state for a head that has no signing key - the
        // browser, which authenticates its media requests with a stream ticket
        // in the URL instead (see StreamTicketService). Sending the request
        // untouched is right there: it is already carrying what it needs.
        if (credentials() is not { } peer || request.RequestUri is not { } uri)
            return await base.SendAsync(request, cancellationToken);

        request.RequestUri = WithoutIdentityParams(uri);
        await request.AddPeerCredentialsAsync(peer);
        var response = await base.SendAsync(request, cancellationToken);

        // Once more, signed in the server's time, when the server said this
        // device's clock is out (see SignatureClock). Only here, because only
        // here is the signing this handler's own to redo; and only for a
        // request with no body, which is every one this handler signs - a
        // track's probe, body and reopens - and the only kind that can be
        // sent twice without re-reading its content.
        if (request.Content == null && await SignatureClockHandler.ObserveAsync(response, cancellationToken))
        {
            response.Dispose();
            foreach (var name in request.Headers.Select(h => h.Key).Where(IsCredential).ToList())
                request.Headers.Remove(name);
            await request.AddPeerCredentialsAsync(peer);
            response = await base.SendAsync(request, cancellationToken);
        }

        return response;
    }

    private static bool IsCredential(string header) =>
        header.StartsWith("X-Flower-", StringComparison.OrdinalIgnoreCase);

    // Drops any credential set the URL was built with before signing it again.
    //
    // Provably inert for the signature itself: SignedRequestCanonicalizer
    // excludes every X-Flower-* param from the canonical query precisely so the
    // header and query transports sign identical bytes, so removing them
    // changes nothing about what either end computes. What it does change is
    // that a spent nonce and a used signature stop riding along on a request
    // that is now signed properly - which matters beyond tidiness, because a
    // stream URL's query is also what gets pushed to the server's device log
    // and sits there at rest (see LogPath, which shortens it for the same
    // reason).
    //
    // The Subsonic params (u/t/s/v/c/f/id) are deliberately kept: they are what
    // the server falls back to when signature auth is not in play, and they are
    // part of the signed query on both sides.
    internal static Uri WithoutIdentityParams(Uri uri)
    {
        if (!uri.Query.Contains("X-Flower-", StringComparison.OrdinalIgnoreCase))
            return uri;

        var kept = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(pair => !pair.StartsWith("X-Flower-", StringComparison.OrdinalIgnoreCase));

        return new UriBuilder(uri) { Query = string.Join("&", kept) }.Uri;
    }
}
