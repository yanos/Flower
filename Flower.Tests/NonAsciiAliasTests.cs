using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests;

// A device whose name is not ASCII, which is an ordinary device: the alias is
// typed by whoever owns the phone (DeviceIdentity.Alias has to be user-typed,
// since iOS will not tell an app the real device name), and "Mr Téléphone" is
// a name a person picks without thinking twice about it.
//
// What that used to cost was the whole connection, silently. Every request a
// client makes carries the alias as an X-Flower-Alias header, and a header is
// ASCII on all three stacks in the path: HttpClient throws "Request headers
// must contain only ASCII characters." before the socket write, fetch() throws
// a TypeError on the same input, and Kestrel answers 400 to a raw high byte
// before any endpoint runs. So renaming a paired phone broke /info, sync and
// streaming at once, and because NetworkDiscoveryService.ResolveAliasAsync
// cannot tell a request that never left from a server that never answered, the
// only thing the user saw was "Server not reachable" - next to the green tick
// that says the pairing is fine, because the pairing was fine.
//
// These pin the fix (IdentityHeaderEncoding) at the level the bug lived at:
// what the app actually puts on a request, and what a server reading it gets
// back.
public class NonAsciiAliasTests
{
    private const string Alias = "Mr Téléphone";

    private static readonly DeviceSigningKey Key = TestSigningKey.Create();

    private static IPeerCredentials Credentials() => new SignedDeviceCredentials(
        new DeviceIdentity { Fingerprint = Key.Fingerprint, Alias = Alias }, Key);

    private static Dictionary<string, string> HeadersOf(HttpRequestMessage request)
    {
        var headers = new Dictionary<string, string>();
        foreach (var header in request.Headers)
            headers[header.Key] = string.Join(",", header.Value);
        return headers;
    }

    // The invariant that makes the request sendable at all. Asserted on the
    // header values rather than by sending one, because the ASCII check lives
    // in the socket writer - a stub handler would happily accept what a real
    // connection refuses, which is the kind of test that would have passed
    // throughout this bug.
    [Fact]
    public async Task Every_header_a_non_ascii_device_sends_is_ascii()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, "http://server.local:4533/rest/getCoverArt?id=al-1");
        await request.AddPeerCredentialsAsync(Credentials());

        var values = HeadersOf(request).Values.ToList();
        Assert.NotEmpty(values);
        foreach (var value in values)
            Assert.All(value, c => Assert.InRange(c, (char)0x20, (char)0x7E));
    }

    // And the other half: ASCII on the wire is worth nothing if the server
    // cannot get the name back out. This is the real
    // Flower.Core/Flower.Server read path (SignedRequest.Identity, which
    // DeviceSignatureAuth.GetIdentityValue is a one-line wrapper over).
    [Fact]
    public async Task The_server_reads_back_the_name_the_user_typed()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, "http://server.local:4533/rest/getCoverArt?id=al-1");
        await request.AddPeerCredentialsAsync(Credentials());

        var headers = HeadersOf(request);
        var received = new SignedRequest(
            "GET", "/rest/getCoverArt", [("id", "al-1")], [], name => headers.GetValueOrDefault(name));

        Assert.Equal(Alias, received.Identity("X-Flower-Alias"));
    }

    // Encoding the identity params must not disturb what they are *for*. The
    // canonical string excludes every X-Flower-* param exactly so the header
    // and query transports sign the same bytes (SignedRequestCanonicalizer.
    // IsTransportParam), so this passes for a structural reason rather than a
    // lucky one - but it is the assertion that would fail loudest if that ever
    // changed, so it is worth making against the real verifier.
    [Fact]
    public async Task An_accented_alias_does_not_break_the_signature()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, "http://server.local:4533/rest/getCoverArt?id=al-1");
        await request.AddPeerCredentialsAsync(Credentials());

        var headers = HeadersOf(request);
        var authenticated = PeerSignatureAuth.VerifyTrustedPeer(
            new SignedRequest("GET", "/rest/getCoverArt", [("id", "al-1")], [],
                name => headers.GetValueOrDefault(name)),
            fingerprint => fingerprint == Key.Fingerprint ? Key.PublicKeyBase64 : null,
            new NonceReplayGuard(), DateTimeOffset.UtcNow);

        Assert.Equal(Key.Fingerprint, authenticated);
    }

    // The two transports have to name the same device the same thing. A stream
    // URL is handed to LibVLC, which cannot carry headers, so the identity
    // travels as query params there - already escaped by the URL builder and
    // already unescaped by whatever parsed it. Decoding that branch as well
    // would take a second layer off, and a device called "100%" or "%41" would
    // be a different device depending on which call was asking.
    [Fact]
    public async Task The_stream_url_transport_carries_the_same_name()
    {
        var client = new OpenSubsonicClient(
            "http://server.local:4533", username: "", password: "", credentials: Credentials());

        var uri = new Uri(await client.GetStreamUrlAsync("sg-1"));

        var query = new List<(string Key, string Value)>();
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            query.Add((Uri.UnescapeDataString(pair[..separator]), Uri.UnescapeDataString(pair[(separator + 1)..])));
        }

        Assert.Equal(Alias, new SignedRequest("GET", uri.AbsolutePath, query, [], _ => null)
            .Identity("X-Flower-Alias"));
    }

    [Theory]
    [InlineData("Mr Téléphone")]
    [InlineData("太郎's iPhone")]   // beyond Latin-1, so not merely a charset choice
    [InlineData("Café 100% Löud")]  // a literal % the user typed, which must survive as one
    [InlineData("Nick's Pixel")]
    public void Any_name_round_trips_through_the_header_transport(string alias)
    {
        var encoded = IdentityHeaderEncoding.Encode(alias);

        Assert.All(encoded, c => Assert.InRange(c, (char)0x20, (char)0x7E));
        Assert.Equal(alias, IdentityHeaderEncoding.Decode(encoded));
    }

    // Reading identity params happens before anything has been verified -
    // /info answers unauthenticated callers, and the claimed fingerprint is
    // read in order to decide which key to check the signature against. So a
    // header nobody encoded, or encoded wrongly, has to come back as itself
    // rather than as a 500.
    [Fact]
    public void A_header_that_was_never_encoded_is_left_alone()
    {
        Assert.Equal("Nick's Pixel", IdentityHeaderEncoding.Decode("Nick's Pixel"));
        Assert.Equal("100%", IdentityHeaderEncoding.Decode("100%"));
        Assert.Equal("%zz", IdentityHeaderEncoding.Decode("%zz"));
        Assert.Equal("%", IdentityHeaderEncoding.Decode("%"));
    }
}
