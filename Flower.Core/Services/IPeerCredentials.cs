using System;
using System.Collections.Generic;
using System.Net.Http;

namespace Flower.Services;

// How this process proves who it is to a Flower host - a peer's embedded
// SyncHttpServer or a headless Flower.Server - for one specific request.
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
// Deliberately says nothing about *how* the caller is authenticated. The one
// implementation today signs with this device's keypair; the browser cannot sign
// at all (.NET-for-WebAssembly has no asymmetric crypto - see App.axaml.cs) and
// will present a server-minted session token through this same interface
// instead. That is exactly why the seam exists: see SYNC-PLAN.md's "The
// browser's library".
public interface IPeerCredentials
{
    // The params to attach to this request - identity, and whatever proves it.
    // Returned as key/value pairs rather than written onto anything, because
    // both transports are real: headers for an ordinary HttpClient call, query
    // params for a URL handed to something else to fetch that cannot carry
    // headers (LibVLC playing a stream URL - see OpenSubsonicClient.BuildUrl).
    IEnumerable<(string Key, string Value)> Authorize(
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
    public static void AddPeerCredentials(
        this HttpRequestMessage request, IPeerCredentials credentials, byte[]? body = null)
    {
        var uri = request.RequestUri!;
        foreach (var (key, value) in credentials.Authorize(
                     request.Method.Method, uri.AbsolutePath, ParseQuery(uri.Query), body ?? []))
        {
            request.Headers.TryAddWithoutValidation(key, value);
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
