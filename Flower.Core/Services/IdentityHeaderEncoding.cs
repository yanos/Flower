using System;

namespace Flower.Services;

// How an X-Flower-* identity param survives the header transport.
//
// HTTP header values are bytes, and every stack in this codebase's path treats
// them as ASCII: HttpClient throws "Request headers must contain only ASCII
// characters." before the socket write, the browser's fetch() throws on the
// same input, and Kestrel answers 400 Bad Request to a raw high byte before
// any endpoint sees the request. So a device called "Mr Téléphone" could not
// talk to its server at all - not sync, not /info, not a stream - and the
// failure surfaced only as "Server not reachable", because ResolveAliasAsync
// cannot tell a request that never left from a server that never answered.
// The alias is user-typed (see DeviceIdentity.Alias, which has to be, since
// iOS will not tell an app the device's real name), so non-ASCII is ordinary
// input, not an edge case.
//
// Percent-encoding is what makes it ASCII: the value is UTF-8 bytes, escaped,
// and unescaped back to exactly the same string on the other side. Applied to
// every identity param uniformly rather than to the alias alone - the rest are
// base64 or hex and escape to themselves, and "which params are user text" is
// not a question a new call site should have to get right.
//
// Two things this deliberately does not touch:
//
//   - The signature. SignedRequestCanonicalizer excludes every X-Flower-*
//     param from the canonical string (see its IsTransportParam), precisely so
//     the header and query transports sign identical bytes, so changing the
//     wire form of one of them cannot invalidate anything.
//   - The query transport. A URL handed to something else to fetch (LibVLC
//     playing a stream URL - OpenSubsonicClient.BuildUrlAsync) carries the
//     same params as query params, where Uri.EscapeDataString has always been
//     applied and the receiving stack decodes them itself. Encoding there too
//     would double-encode, and decoding a query value here would strip an
//     escape the user actually typed. Both transports must yield the same raw
//     string, which is why only the header half is encoded - and why
//     SignedRequest.Identity decodes only its header branch.
public static class IdentityHeaderEncoding
{
    // Applied when writing an X-Flower-* param as a header.
    public static string Encode(string value) => Uri.EscapeDataString(value);

    // Applied when reading one back. Malformed input is not an error: an
    // escape this did not produce ("100%") comes back unchanged rather than
    // throwing, which matters because this runs on unauthenticated requests -
    // /info answers anyone, and PeerSignatureAuth reads the claimed
    // fingerprint before it has verified a thing.
    public static string Decode(string value) => Uri.UnescapeDataString(value);
}
