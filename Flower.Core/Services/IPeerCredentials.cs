using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;

namespace Flower.Services;

// How this process proves who it is to a Flower server, for one specific
// request.
//
// This used to be five hand-rolled copies of the same identity block:
// LibrarySyncService's two calls, PlaylistSyncService.AddSignedIdentityHeaders,
// PeerOpenSubsonicClientFactory's PeerIdentityParamsBuilder delegate, and
// ServerAdminClient.SignWith - all building the same X-Flower-* params from the
// same DeviceIdentity/DeviceSigningKey pair, and each having drifted into a
// slightly different subset of them (see SignedDeviceCredentials on why the
// subsets did not actually matter, and why they are now one uniform set).
//
// Per-request rather than a fixed list computed once, which is the whole reason
// this is an interface and not a header dictionary: every call must carry a
// fresh timestamp and nonce, because the receiving end's NonceReplayGuard treats
// a repeated nonce as a replay attempt - see DeviceSigningKey.Sign.
//
// The method/path/query/body parameters are what gets signed over, not just what
// gets sent: SignedRequestCanonicalizer covers all four so a captured signature
// cannot be replayed against a different route, parameter or body.
//
// Asynchronous because one implementation genuinely cannot answer on the calling
// stack: the browser's key lives in WebCrypto, whose sign() is a promise, and it
// may additionally have to redeem a pairing code before it has an identity at
// all (see BrowserPeerCredentials). Every other head signs in-process and hands
// back an already-completed task. This is what the previous shape - a
// synchronous Authorize plus a server-minted bearer token for the browser - was
// avoiding, and paying for with a bearer credential in a URL; see
// docs/OPEN-INTERNET-REVIEW.md finding 7.
public interface IPeerCredentials
{
    // The params to attach to this request - identity, and whatever proves it.
    // Returned as key/value pairs rather than written onto anything, because
    // both transports are real: headers for an ordinary HttpClient call, query
    // params for a URL handed to something else to fetch that cannot carry
    // headers (LibVLC playing a stream URL - see OpenSubsonicClient.BuildUrlAsync).
    Task<IReadOnlyList<(string Key, string Value)>> AuthorizeAsync(
        string method, string absolutePath, IEnumerable<(string Key, string Value)> query, byte[] body);
}

public static class PeerCredentialsExtensions
{
    // The header-transport half, for the callers that build an
    // HttpRequestMessage directly. Reads the query back off the request's own
    // URI so a caller cannot sign a different query than it sends - the two
    // being assembled separately is the kind of drift this whole seam exists to
    // stop.
    //
    // Does not touch ConnectionClose: whether to force a fresh connection is a
    // property of who is being called (a peer's HttpListener, which may have
    // torn the pooled connection down - see PlaylistSyncService's note) rather
    // than of how the call is authenticated, and the admin client deliberately
    // does not set it.
    //
    // Values are percent-encoded on the way onto the request, because this is
    // the header transport and a header is ASCII - see IdentityHeaderEncoding,
    // and SignedRequest.Identity which decodes them again. The query transport
    // (OpenSubsonicClient.BuildUrlAsync) escapes for itself and must not be
    // encoded here as well.
    public static async Task AddPeerCredentialsAsync(
        this HttpRequestMessage request, IPeerCredentials credentials, byte[]? body = null)
    {
        var uri = request.RequestUri!;
        foreach (var (key, value) in await credentials.AuthorizeAsync(
                     request.Method.Method, uri.AbsolutePath, ParseQuery(uri.Query), body ?? []))
        {
            request.Headers.TryAddWithoutValidation(key, IdentityHeaderEncoding.Encode(value));
        }
    }

    // The canonical form signs the *decoded* key and value, which is what the
    // receiving end rebuilds from its own parsed query - so this has to decode
    // rather than split the raw string and leave the escapes in.
    private static List<(string Key, string Value)> ParseQuery(string query)
    {
        var parsed = new List<(string Key, string Value)>();
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            parsed.Add(separator < 0
                ? (Uri.UnescapeDataString(pair), "")
                : (Uri.UnescapeDataString(pair[..separator]), Uri.UnescapeDataString(pair[(separator + 1)..])));
        }

        return parsed;
    }
}
