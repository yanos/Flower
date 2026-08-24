using System;
using System.Collections.Generic;
using System.Net.Http;

using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests;

// The gap that let server-hosted playback break in a way every other test
// still passed.
//
// PlaylistPlaybackIntegrationTests proves the app *decides* to stream a
// placeholder and hands a URL to the audio pipeline, but its decoder is a fake
// - it never opens the URL, so a URL a real server refuses looks identical to
// one it serves. That is exactly what happened: cover art was fetched with an
// unsigned request, Flower.Server answered each one with a Subsonic error
// envelope and charged its FailedAuthLimiter, and after ten album tiles the
// whole /rest surface - /rest/stream included - answered 429. Nothing in the
// suite noticed, because nothing in the suite checked that what this app sends
// is what a server accepts.
//
// So these tests take the URL and the headers the app really produces and run
// them through PeerSignatureAuth - the *same* verification Flower.Server's
// /rest gate uses. No fake auth, no restating the
// rules: if this passes, a trusted device's request authenticates.
public class StreamAuthEndToEndTests
{
    private static readonly DeviceSigningKey Key = TestSigningKey.Create();

    private static IPeerCredentials Credentials() => new SignedDeviceCredentials(
        new DeviceIdentity { Fingerprint = Key.Fingerprint, Alias = "Us" }, Key);

    // What a server does with an incoming request: look the caller's public key
    // up by the fingerprint it claims, then verify. A key on file means paired.
    private static string? Authenticate(SignedRequest request) =>
        PeerSignatureAuth.VerifyTrustedPeer(
            request, fingerprint => fingerprint == Key.Fingerprint ? Key.PublicKeyBase64 : null,
            new NonceReplayGuard(), DateTimeOffset.UtcNow);

    private static List<(string Key, string Value)> ParseQuery(Uri uri)
    {
        var parsed = new List<(string Key, string Value)>();
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            parsed.Add(separator < 0
                ? (Uri.UnescapeDataString(pair), "")
                : (Uri.UnescapeDataString(pair[..separator]), Uri.UnescapeDataString(pair[(separator + 1)..])));
        }

        return parsed;
    }

    [Fact]
    public async Task The_stream_url_handed_to_the_decoder_authenticates_against_a_real_server()
    {
        // The URL really goes out this way: LibVLC opens it itself, so it
        // cannot carry headers and every credential travels in the query
        // string. The server therefore sees no headers at all - hence the
        // header lookup below returns null for everything, which is what makes
        // this the honest reproduction of the playback path rather than a
        // friendlier one.
        var client = new OpenSubsonicClient(
            "http://server.local:4533", username: "", password: "", credentials: Credentials());

        var uri = new Uri(await client.GetStreamUrlAsync("sg-1"));

        Assert.Equal(Key.Fingerprint, Authenticate(
            new SignedRequest("GET", uri.AbsolutePath, ParseQuery(uri), [], _ => null)));
    }

    [Fact]
    public async Task A_stream_url_replayed_against_a_different_track_is_refused()
    {
        // The signature covers the query, so a URL captured for one track must
        // not become a key to the whole library.
        var client = new OpenSubsonicClient(
            "http://server.local:4533", username: "", password: "", credentials: Credentials());

        var uri = new Uri(await client.GetStreamUrlAsync("sg-1"));
        var tampered = ParseQuery(uri);
        tampered[tampered.FindIndex(p => p.Key == "id")] = ("id", "sg-2");

        Assert.Null(Authenticate(new SignedRequest("GET", uri.AbsolutePath, tampered, [], _ => null)));
    }

    [Fact]
    public async Task A_cover_art_request_authenticates_the_same_way_a_stream_does()
    {
        // The one that actually broke playback. Cover art goes out as an
        // ordinary HttpClient call with header credentials (AlbumArtLoader),
        // not as a URL for something else to open - a different transport, but
        // it has to clear the very same gate, because /rest is gated as a
        // whole and a refusal here costs the whole surface.
        using var request = new HttpRequestMessage(
            HttpMethod.Get, "http://server.local:4533/rest/getCoverArt?id=al-1");
        await request.AddPeerCredentialsAsync(Credentials());

        var headers = new Dictionary<string, string>();
        foreach (var header in request.Headers)
            headers[header.Key] = string.Join(",", header.Value);

        Assert.Equal(Key.Fingerprint, Authenticate(new SignedRequest(
            "GET", "/rest/getCoverArt", [("id", "al-1")], [], name => headers.GetValueOrDefault(name))));
    }

    [Fact]
    public void An_unsigned_request_is_refused_which_is_what_used_to_poison_the_rate_limiter()
    {
        // Pins the failure mode itself, so "we send credentials now" cannot
        // quietly regress into "we send a name". A bare fingerprint identifies
        // but proves nothing, and the server counts each such attempt against a
        // limiter that gates streaming too.
        Assert.Null(Authenticate(new SignedRequest(
            "GET", "/rest/getCoverArt", [("id", "al-1")], [],
            name => name == "X-Flower-Fingerprint" ? Key.Fingerprint : null)));
    }
}
