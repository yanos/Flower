using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Models;
using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;

namespace Flower.Tests;

// The real thing over a real socket: an actual SyncHttpServer bound to an
// actual port, driven by an actual HttpClient. Everything else that touches
// this class substitutes FakePeerHttpServer for the listener, which means the
// dispatcher in HandleRequestAsync - route table, LanGuard check, rate-limit
// categories, body cap, and the three AuthMode branches - was previously
// validated only by hand. That dispatcher is where all of the security
// behaviour lives, so it is exactly the part that should not be faked.
//
// Requests go over 127.0.0.1, which LanGuard accepts as loopback; the
// non-LAN rejection branch can't be produced from a test on one machine and
// is covered by LanGuardTests against the predicate itself instead. The 20 MB
// body cap is likewise not exercised here - RequestBodyReaderTests covers the
// cap logic directly, and asserting it through the socket would mean actually
// uploading 20 MB into a server that closes the connection partway, which
// races the client's own send.
//
// Pinned to an isolated PlatformDataDirectory (see StoreRoundTripTests' own
// comment): TrustedPeerStore writes real files, and pairing tests here
// approve/deny peers. This used to matter for playlists.json too - the
// harness handed SyncHttpServer a real PlaylistStore - but the server no
// longer persists playlists itself (Library.PlaylistsChanged does, wired in
// App.axaml.cs), so that store is gone from the harness entirely.
[Collection("PlatformDataDirectory")]
public class SyncHttpServerRoundTripTests : IDisposable
{
    // The wire format is camelCase on both sides (the app's source-generated
    // contexts are internal to Flower, so tests re-parse with the equivalent
    // web defaults rather than reaching into them).
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string? _originalHome;
    private readonly string _tempHome;

    public SyncHttpServerRoundTripTests()
    {
        _originalHome = Environment.GetEnvironmentVariable("HOME");
        _tempHome = Path.Combine(Path.GetTempPath(), "flower-synchttp-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempHome);
        Environment.SetEnvironmentVariable("HOME", _tempHome);
        PlatformDataDirectory.Current = _tempHome;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HOME", _originalHome);
        PlatformDataDirectory.Current = AssemblySetup.DefaultDataDirectory;
        try { Directory.Delete(_tempHome, recursive: true); } catch { /* best effort */ }
    }

    // Everything a live server needs, plus the peer-side signing key the
    // tests present to it. Start() binds "http://+:{port}/", which on Windows
    // needs a one-time netsh urlacl reservation (see SyncHttpServer.Start's
    // own comment) - without it every port attempt throws and BoundPort stays
    // null. Tests early-return in that case rather than failing: the gap is
    // real, known, and about the host's HTTP.sys ACLs, not about this code.
    private sealed class Harness : IDisposable
    {
        public SyncHttpServer Server { get; }
        public DeviceIdentity Identity { get; }
        public DeviceSigningKey OwnKey { get; }
        public DeviceSigningKey PeerKey { get; }
        public TrustedPeerStore TrustedPeers { get; }
        public ClientLogStore ClientLogs { get; }
        public Library Library { get; }
        public AppSettings Settings { get; }
        public HttpClient Http { get; }

        public int? Port => Server.BoundPort;
        public string PeerFingerprint => PeerKey.Fingerprint;

        public Harness(List<Track>? tracks = null, bool isServer = false)
        {
            OwnKey = TestSigningKey.Create();
            PeerKey = TestSigningKey.Create();
            Identity = new DeviceIdentity { Fingerprint = OwnKey.Fingerprint, Alias = "Test Device" };
            Settings = new AppSettings { IsServer = isServer };
            Library = new Library(tracks ?? new List<Track>());
            TrustedPeers = new TrustedPeerStore(NullLogger<TrustedPeerStore>.Instance);
            ClientLogs = new ClientLogStore();

            Server = new SyncHttpServer(
                Identity, OwnKey, Settings, Library,
                TrustedPeers, ClientLogs,
                NullLogger<SyncHttpServer>.Instance);
            Server.Start();

            // Matches the app's own clients (see PlaylistSyncService) - the
            // server sets KeepAlive = false on every response, so pooling
            // would just mean reusing a connection it already tore down.
            Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            Http.DefaultRequestHeaders.ConnectionClose = true;
        }

        public string Url(string path) => $"http://127.0.0.1:{Port}{path}";

        public Task ApprovePeerAsync() =>
            TrustedPeers.ApproveAsync(PeerFingerprint, "Peer", PeerKey.PublicKeyBase64);

        // Builds the request exactly the way PlaylistSyncService/
        // LibrarySyncService do: identity headers plus a signature over
        // method + path + query + body.
        public HttpRequestMessage Signed(
            HttpMethod method, string path,
            IEnumerable<(string Key, string Value)>? query = null,
            byte[]? body = null,
            DeviceSigningKey? signer = null,
            string? claimedFingerprint = null,
            string? role = null)
        {
            var key = signer ?? PeerKey;
            var pairs = (query ?? []).ToList();
            var bytes = body ?? [];
            var (signature, timestamp, nonce) = key.Sign(method.Method, path, pairs, bytes);

            var queryString = pairs.Count == 0
                ? ""
                : "?" + string.Join("&", pairs.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
            var request = new HttpRequestMessage(method, Url(path + queryString));
            request.Headers.Add("X-Flower-Fingerprint", claimedFingerprint ?? key.Fingerprint);
            request.Headers.Add("X-Flower-Alias", "Peer");
            request.Headers.Add("X-Flower-PublicKey", key.PublicKeyBase64);
            request.Headers.Add("X-Flower-Signature", signature);
            request.Headers.Add("X-Flower-Timestamp", timestamp);
            request.Headers.Add("X-Flower-Nonce", nonce);
            if (role != null)
                request.Headers.Add("X-Flower-Role", role);
            request.Headers.ConnectionClose = true;
            if (body != null)
                request.Content = new ByteArrayContent(bytes) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") } };
            return request;
        }

        public void Dispose()
        {
            Http.Dispose();
            Server.Dispose();
            OwnKey.Dispose();
            PeerKey.Dispose();
        }
    }

    private static Track TrackWithFile(string directory, string title, string album = "Album", string artist = "Artist")
    {
        var path = Path.Combine(directory, title + ".mp3");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("fake audio bytes for " + title));
        return new Track
        {
            Title = title,
            Album = album,
            Artists = artist,
            Path = path,
            Duration = TimeSpan.FromSeconds(180),
        };
    }

    // ── The open endpoint ────────────────────────────────────────────────────

    [Fact]
    public async Task Info_is_served_without_any_credentials_and_advertises_this_devices_public_key()
    {
        using var harness = new Harness();
        if (harness.Port == null)
            return;

        var response = await harness.Http.GetAsync(harness.Url("/api/localsend/v2/info"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var info = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(harness.Identity.Fingerprint, info.RootElement.GetProperty("fingerprint").GetString());
        Assert.Equal(harness.OwnKey.PublicKeyBase64, info.RootElement.GetProperty("publicKey").GetString());
        // Omitted, not false - an anonymous probe must not read as a rejection.
        Assert.Equal(JsonValueKind.Null, info.RootElement.GetProperty("trustsCaller").ValueKind);
    }

    [Fact]
    public async Task Info_tells_an_identified_caller_whether_this_device_currently_trusts_it()
    {
        using var harness = new Harness();
        if (harness.Port == null)
            return;

        async Task<bool?> AskAsync()
        {
            // Signed, not merely claimed: a fingerprint is public, so asserting
            // one proves nothing (see docs/OPEN-INTERNET-REVIEW.md).
            using var request = harness.Signed(HttpMethod.Get, "/api/localsend/v2/info");
            using var response = await harness.Http.SendAsync(request);
            using var info = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var trustsCaller = info.RootElement.GetProperty("trustsCaller");
            return trustsCaller.ValueKind == JsonValueKind.Null ? null : trustsCaller.GetBoolean();
        }

        Assert.False(await AskAsync());
        await harness.ApprovePeerAsync();
        Assert.True(await AskAsync());
    }

    // An unsigned caller claiming a fingerprint this device does trust learns
    // nothing from it - not the trust status, and not the addresses below.
    [Fact]
    public async Task Info_tells_a_caller_that_only_claims_a_trusted_fingerprint_nothing()
    {
        using var harness = new Harness();
        if (harness.Port == null)
            return;
        await harness.ApprovePeerAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, harness.Url("/api/localsend/v2/info"));
        request.Headers.Add("X-Flower-Fingerprint", harness.PeerFingerprint);
        using var response = await harness.Http.SendAsync(request);
        using var info = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // A key is on file, so this is a failed signature rather than a
        // revocation - which is "unknown", not "no".
        Assert.Equal(JsonValueKind.Null, info.RootElement.GetProperty("trustsCaller").ValueKind);
        Assert.True(!info.RootElement.TryGetProperty("addresses", out var addresses)
                    || addresses.ValueKind == JsonValueKind.Null);
    }

    // Where this device can be reached is for paired peers only, and only when
    // it is acting as a Server at all - see REMOTE-ACCESS-PLAN.md for why the
    // list exists and OPEN-INTERNET-REVIEW.md for why it is gated.
    [Fact]
    public async Task Info_reports_where_a_server_can_be_reached_only_to_a_verified_peer()
    {
        using var harness = new Harness(isServer: true);
        if (harness.Port == null)
            return;

        async Task<JsonElement> AskAsync(bool signed)
        {
            using var request = signed
                ? harness.Signed(HttpMethod.Get, "/api/localsend/v2/info")
                : new HttpRequestMessage(HttpMethod.Get, harness.Url("/api/localsend/v2/info"));
            using var response = await harness.Http.SendAsync(request);
            using var info = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return info.RootElement.Clone();
        }

        // Signed, but not yet approved.
        var beforePairing = await AskAsync(signed: true);
        Assert.True(!beforePairing.TryGetProperty("addresses", out var none)
                    || none.ValueKind == JsonValueKind.Null);

        await harness.ApprovePeerAsync();

        var anonymous = await AskAsync(signed: false);
        Assert.True(!anonymous.TryGetProperty("addresses", out var stillNone)
                    || stillNone.ValueKind == JsonValueKind.Null);

        var paired = await AskAsync(signed: true);
        Assert.Equal(JsonValueKind.Array, paired.GetProperty("addresses").ValueKind);
    }

    [Fact]
    public async Task An_unknown_route_is_404_not_403()
    {
        using var harness = new Harness();
        if (harness.Port == null)
            return;

        var response = await harness.Http.GetAsync(harness.Url("/api/flower/v1/does-not-exist"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── The trust gate ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_gated_endpoint_refuses_a_request_carrying_no_signature_at_all()
    {
        using var harness = new Harness();
        if (harness.Port == null)
            return;
        await harness.ApprovePeerAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, harness.Url("/api/flower/v1/library"));
        request.Headers.Add("X-Flower-Fingerprint", harness.PeerFingerprint);
        var response = await harness.Http.SendAsync(request);

        // Trusted fingerprint, no proof of possession - being on the trust
        // list is not itself a credential. 401 rather than 403 because the
        // peer *is* on the list: 403 is reserved for a caller with no key on
        // file, since a client acts on that one by unpairing itself (see
        // PeerSignatureAuth.AuthenticateTrustedPeer).
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_correctly_signed_request_from_an_untrusted_peer_is_still_refused()
    {
        using var harness = new Harness();
        if (harness.Port == null)
            return;

        using var request = harness.Signed(HttpMethod.Get, "/api/flower/v1/library");
        var response = await harness.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_trusted_signed_peer_gets_the_library_manifest()
    {
        var directory = Path.Combine(_tempHome, "music");
        Directory.CreateDirectory(directory);
        using var harness = new Harness([TrackWithFile(directory, "Song One")]);
        if (harness.Port == null)
            return;
        await harness.ApprovePeerAsync();

        using var request = harness.Signed(HttpMethod.Get, "/api/flower/v1/library");
        var response = await harness.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var manifest = JsonSerializer.Deserialize<LibrarySyncManifestDto>(
            await response.Content.ReadAsStringAsync(), JsonOptions)!;
        Assert.Equal(harness.Identity.Fingerprint, manifest.DeviceFingerprint);
        Assert.Equal("Song One", Assert.Single(manifest.Songs).Title);
    }

    [Fact]
    public async Task A_signature_cannot_be_replayed_even_seconds_later()
    {
        using var harness = new Harness();
        if (harness.Port == null)
            return;
        await harness.ApprovePeerAsync();

        // Same signature/timestamp/nonce sent twice, which is precisely what
        // an attacker who captured one request off the wire has.
        var (signature, timestamp, nonce) = harness.PeerKey.Sign("GET", "/api/flower/v1/library", [], []);

        async Task<HttpStatusCode> SendAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, harness.Url("/api/flower/v1/library"));
            request.Headers.Add("X-Flower-Fingerprint", harness.PeerFingerprint);
            request.Headers.Add("X-Flower-Signature", signature);
            request.Headers.Add("X-Flower-Timestamp", timestamp);
            request.Headers.Add("X-Flower-Nonce", nonce);
            request.Headers.ConnectionClose = true;
            using var response = await harness.Http.SendAsync(request);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await SendAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync());
    }

    [Fact]
    public async Task A_signature_is_bound_to_the_query_it_was_made_for()
    {
        var directory = Path.Combine(_tempHome, "music-query");
        Directory.CreateDirectory(directory);
        var track = TrackWithFile(directory, "Song One");
        using var harness = new Harness([track]);
        if (harness.Port == null)
            return;
        await harness.ApprovePeerAsync();

        // Signed for one id, sent for another - without the query in the
        // canonical string, a captured stream URL would work for any song.
        var (signature, timestamp, nonce) = harness.PeerKey.Sign(
            "GET", "/rest/stream", [("id", track.Id.ToString("N"))], []);
        using var request = new HttpRequestMessage(
            HttpMethod.Get, harness.Url("/rest/stream?id=some-other-song"));
        request.Headers.Add("X-Flower-Fingerprint", harness.PeerFingerprint);
        request.Headers.Add("X-Flower-Signature", signature);
        request.Headers.Add("X-Flower-Timestamp", timestamp);
        request.Headers.Add("X-Flower-Nonce", nonce);
        request.Headers.ConnectionClose = true;

        var response = await harness.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── Pairing (AuthMode.SelfSigned) ────────────────────────────────────────

    [Fact]
    public async Task An_approved_pair_request_captures_the_peers_public_key_and_returns_204()
    {
        using var harness = new Harness();
        if (harness.Port == null)
            return;

        string? promptedAlias = null;
        harness.Server.PeerApprovalRequested += (_, e) =>
        {
            promptedAlias = e.Alias;
            e.Resolution.TrySetResult(true);
        };

        using var request = harness.Signed(HttpMethod.Post, "/api/flower/v1/pair-request");
        var response = await harness.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("Peer", promptedAlias);
        Assert.True(harness.TrustedPeers.IsTrusted(harness.PeerFingerprint));
        // The key on file has to be the one that will be checked later - a
        // trusted peer with no usable key would fail every gated request.
        Assert.Equal(harness.PeerKey.PublicKeyBase64, harness.TrustedPeers.GetPublicKey(harness.PeerFingerprint));
    }

    [Fact]
    public async Task A_denied_pair_request_returns_403_and_records_the_refusal()
    {
        using var harness = new Harness();
        if (harness.Port == null)
            return;

        harness.Server.PeerApprovalRequested += (_, e) => e.Resolution.TrySetResult(false);

        using var request = harness.Signed(HttpMethod.Post, "/api/flower/v1/pair-request");
        var response = await harness.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(harness.TrustedPeers.IsTrusted(harness.PeerFingerprint));
        Assert.Equal(harness.PeerFingerprint, Assert.Single(harness.TrustedPeers.LoadDenied()).Fingerprint);
    }

    [Fact]
    public async Task A_pair_request_with_no_UI_listening_fails_closed()
    {
        using var harness = new Harness();
        if (harness.Port == null)
            return;

        // No PeerApprovalRequested subscriber at all - a headless/backgrounded
        // instance must not silently trust a stranger.
        using var request = harness.Signed(HttpMethod.Post, "/api/flower/v1/pair-request");
        var response = await harness.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(harness.TrustedPeers.IsTrusted(harness.PeerFingerprint));
    }

    [Fact]
    public async Task A_pair_request_claiming_a_fingerprint_that_is_not_its_public_keys_is_rejected_unauthenticated()
    {
        using var harness = new Harness();
        if (harness.Port == null)
            return;

        var raised = false;
        harness.Server.PeerApprovalRequested += (_, e) => { raised = true; e.Resolution.TrySetResult(true); };

        // Valid signature, valid key, but a fingerprint belonging to someone
        // else - the impersonation attempt the whole scheme exists to stop.
        using var request = harness.Signed(
            HttpMethod.Post, "/api/flower/v1/pair-request",
            claimedFingerprint: harness.Identity.Fingerprint);
        var response = await harness.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(raised); // Never even reached the prompt.
    }

    [Fact]
    public async Task Re_pairing_an_already_trusted_peer_is_idempotent_and_prompts_nobody()
    {
        using var harness = new Harness();
        if (harness.Port == null)
            return;
        await harness.ApprovePeerAsync();

        var raised = false;
        harness.Server.PeerApprovalRequested += (_, e) => { raised = true; e.Resolution.TrySetResult(true); };

        using var request = harness.Signed(HttpMethod.Post, "/api/flower/v1/pair-request");
        var response = await harness.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(raised);
    }

    [Fact]
    public async Task Pair_requests_are_rate_limited_per_source_address()
    {
        using var harness = new Harness();
        if (harness.Port == null)
            return;

        harness.Server.PeerApprovalRequested += (_, e) => e.Resolution.TrySetResult(false);

        // The pair limiter is 5/60s keyed by source IP, and a fresh keypair
        // per attempt is exactly what an attacker would use - so the budget
        // must not be per-fingerprint here.
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 7; i++)
        {
            using var attacker = TestSigningKey.Create();
            using var request = harness.Signed(HttpMethod.Post, "/api/flower/v1/pair-request", signer: attacker);
            using var response = await harness.Http.SendAsync(request);
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(5, statuses.Count(s => s == HttpStatusCode.Forbidden));
        Assert.Equal(2, statuses.Count(s => s == (HttpStatusCode)429));
    }

    [Fact]
    public async Task An_unpair_notification_is_reported_to_the_UI_and_always_answers_204()
    {
        using var harness = new Harness();
        if (harness.Port == null)
            return;

        string? notified = null;
        harness.Server.PeerUnpairNotified += (_, e) => notified = e.Fingerprint;

        using var request = harness.Signed(HttpMethod.Post, "/api/flower/v1/unpair-notify");
        var response = await harness.Http.SendAsync(request);

        // 204 whether or not this device had a pairing for that fingerprint -
        // the response must not leak local trust state.
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(harness.PeerFingerprint, notified);
    }

    // ── Body-carrying endpoints ──────────────────────────────────────────────

    [Fact]
    public async Task A_pushed_log_snapshot_is_stored_under_the_verified_header_identity()
    {
        using var harness = new Harness();
        if (harness.Port == null)
            return;
        await harness.ApprovePeerAsync();

        var entries = new List<LogEntryDto>
        {
            new(DateTimeOffset.UtcNow, "Warning", "Flower.Services.Thing", "something happened", null),
        };
        // The body claims a different fingerprint than the headers the
        // signature actually covers - the store must key by the verified one.
        var report = new LogReportDto("some-other-fingerprint", "Body Alias", DateTimeOffset.UtcNow, entries);
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(report, JsonOptions));

        using var request = harness.Signed(HttpMethod.Post, "/api/flower/v1/log/report", body: body);
        var response = await harness.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var stored = harness.ClientLogs.Get(harness.PeerFingerprint);
        Assert.NotNull(stored);
        Assert.Equal("something happened", Assert.Single(stored!.Entries).Message);
        Assert.Null(harness.ClientLogs.Get("some-other-fingerprint"));
    }

    [Fact]
    public async Task A_tampered_body_invalidates_the_signature_that_covered_the_original()
    {
        using var harness = new Harness();
        if (harness.Port == null)
            return;
        await harness.ApprovePeerAsync();

        var original = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new LogReportDto(harness.PeerFingerprint, "Peer", DateTimeOffset.UtcNow, []), JsonOptions));
        var (signature, timestamp, nonce) = harness.PeerKey.Sign("POST", "/api/flower/v1/log/report", [], original);

        using var request = new HttpRequestMessage(HttpMethod.Post, harness.Url("/api/flower/v1/log/report"))
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("""{"deviceFingerprint":"x","alias":"x","capturedAt":"2026-01-01T00:00:00Z","entries":[]}""")),
        };
        request.Headers.Add("X-Flower-Fingerprint", harness.PeerFingerprint);
        request.Headers.Add("X-Flower-Signature", signature);
        request.Headers.Add("X-Flower-Timestamp", timestamp);
        request.Headers.Add("X-Flower-Nonce", nonce);
        request.Headers.ConnectionClose = true;

        var response = await harness.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_pushed_playlist_manifest_replaces_this_devices_playlists()
    {
        var directory = Path.Combine(_tempHome, "music-playlists");
        Directory.CreateDirectory(directory);
        var track = TrackWithFile(directory, "Song One");
        using var harness = new Harness([track]);
        if (harness.Port == null)
            return;
        await harness.ApprovePeerAsync();
        harness.Library.AddPlaylist(new Playlist("Stale", [track]));

        var manifest = PlaylistSyncMapper.ToManifest(
            harness.PeerFingerprint, [new Playlist("Pushed", [track])]);
        var body = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(manifest, JsonOptions));

        using var request = harness.Signed(HttpMethod.Post, "/api/flower/v1/playlists/apply", body: body);
        var response = await harness.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var playlist = Assert.Single(harness.Library.Playlists);
        Assert.Equal("Pushed", playlist.Name);
        Assert.Same(track, Assert.Single(playlist.Tracks));
    }

    [Fact]
    public async Task A_server_refuses_bulk_sync_from_a_caller_that_also_claims_to_be_a_server()
    {
        using var harness = new Harness(isServer: true);
        if (harness.Port == null)
            return;
        await harness.ApprovePeerAsync();

        using var bulk = harness.Signed(HttpMethod.Get, "/api/flower/v1/library", role: "server");
        var bulkResponse = await harness.Http.SendAsync(bulk);
        Assert.Equal(HttpStatusCode.Forbidden, bulkResponse.StatusCode);

        // Browsing is deliberately unaffected by role (see SyncRolePolicy) -
        // only the bulk-sync endpoints carry this check.
        using var browse = harness.Signed(HttpMethod.Get, "/rest/getAlbumList2", role: "server");
        var browseResponse = await harness.Http.SendAsync(browse);
        Assert.Equal(HttpStatusCode.OK, browseResponse.StatusCode);
    }

    // ── The OpenSubsonic surface ─────────────────────────────────────────────

    [Fact]
    public async Task Stream_serves_the_real_file_bytes_for_a_track_this_device_actually_has()
    {
        var directory = Path.Combine(_tempHome, "music-stream");
        Directory.CreateDirectory(directory);
        var track = TrackWithFile(directory, "Song One");
        using var harness = new Harness([track]);
        if (harness.Port == null)
            return;
        await harness.ApprovePeerAsync();

        using var request = harness.Signed(HttpMethod.Get, "/rest/stream", [("id", track.Id.ToString("N"))]);
        var response = await harness.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(File.ReadAllBytes(track.Path!), await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Stream_serves_a_requested_byte_range_as_206_with_a_Content_Range()
    {
        var directory = Path.Combine(_tempHome, "music-range");
        Directory.CreateDirectory(directory);
        var track = TrackWithFile(directory, "Song One");
        var all = File.ReadAllBytes(track.Path!);
        using var harness = new Harness([track]);
        if (harness.Port == null)
            return;
        await harness.ApprovePeerAsync();

        using var request = harness.Signed(HttpMethod.Get, "/rest/stream", [("id", track.Id.ToString("N"))]);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(5, 9);
        var response = await harness.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal($"bytes 5-9/{all.Length}", response.Content.Headers.ContentRange!.ToString());
        Assert.Equal(all[5..10], await response.Content.ReadAsByteArrayAsync());
        // Note this cannot distinguish a server that stops at the end of the
        // range from one that copies to EOF: the declared Content-Length is 5
        // either way, so the client stops reading regardless. CopyRangeAsync
        // bounds the write anyway - HttpListener treats overshooting a
        // declared Content-Length as a protocol violation.
    }

    [Fact]
    public async Task Stream_serves_an_open_ended_range_as_the_rest_of_the_file()
    {
        var directory = Path.Combine(_tempHome, "music-range-open");
        Directory.CreateDirectory(directory);
        var track = TrackWithFile(directory, "Song One");
        var all = File.ReadAllBytes(track.Path!);
        using var harness = new Harness([track]);
        if (harness.Port == null)
            return;
        await harness.ApprovePeerAsync();

        // The shape a resuming download sends: "I have the first N bytes."
        using var request = harness.Signed(HttpMethod.Get, "/rest/stream", [("id", track.Id.ToString("N"))]);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(4, null);
        var response = await harness.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(all[4..], await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Stream_refuses_a_range_that_starts_past_the_end_with_416_and_the_real_length()
    {
        var directory = Path.Combine(_tempHome, "music-range-bad");
        Directory.CreateDirectory(directory);
        var track = TrackWithFile(directory, "Song One");
        var length = new FileInfo(track.Path!).Length;
        using var harness = new Harness([track]);
        if (harness.Port == null)
            return;
        await harness.ApprovePeerAsync();

        using var request = harness.Signed(HttpMethod.Get, "/rest/stream", [("id", track.Id.ToString("N"))]);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(length + 100, null);
        var response = await harness.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
        // The length is what lets a client with an over-long partial recover
        // rather than retry the same doomed request forever.
        Assert.Equal($"bytes */{length}", response.Content.Headers.ContentRange!.ToString());
    }

    [Fact]
    public async Task Stream_advertises_range_support_on_the_plain_full_body_response()
    {
        var directory = Path.Combine(_tempHome, "music-range-advert");
        Directory.CreateDirectory(directory);
        var track = TrackWithFile(directory, "Song One");
        using var harness = new Harness([track]);
        if (harness.Port == null)
            return;
        await harness.ApprovePeerAsync();

        // A client only learns it may resume from the *first*, unranged
        // response - so this header has to be there when no range was asked for.
        using var request = harness.Signed(HttpMethod.Get, "/rest/stream", [("id", track.Id.ToString("N"))]);
        var response = await harness.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("bytes", response.Headers.AcceptRanges.Single());
    }

    [Fact]
    public async Task Stream_is_404_for_an_id_this_device_has_no_file_for()
    {
        using var harness = new Harness();
        if (harness.Port == null)
            return;
        await harness.ApprovePeerAsync();

        using var request = harness.Signed(HttpMethod.Get, "/rest/stream", [("id", "no-such-song")]);
        var response = await harness.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetCoverArt_serves_the_arts_own_content_type_not_a_guess()
    {
        var directory = Path.Combine(_tempHome, "music-cover");
        Directory.CreateDirectory(directory);
        var track = TrackWithFile(directory, "Song One");
        File.WriteAllBytes(Path.Combine(directory, "cover.webp"), [1, 2, 3, 4]);
        using var harness = new Harness([track]);
        if (harness.Port == null)
            return;
        await harness.ApprovePeerAsync();

        var albumId = LibraryOpenSubsonicMapper.AlbumIdFor(track);
        using var request = harness.Signed(HttpMethod.Get, "/rest/getCoverArt", [("id", albumId)]);
        var response = await harness.Http.SendAsync(request);

        // Two things this device used to get wrong for the same album: a
        // cover.webp was served (the client set accepted it) but labelled
        // image/jpeg by byte-sniffing, while a self-hosted Flower.Server
        // serving the same library refused it outright.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/webp", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal<byte[]>([1, 2, 3, 4], await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task GetAlbumList2_lists_this_devices_own_albums()
    {
        var directory = Path.Combine(_tempHome, "music-albums");
        Directory.CreateDirectory(directory);
        using var harness = new Harness([
            TrackWithFile(directory, "Song One", album: "First"),
            TrackWithFile(directory, "Song Two", album: "Second"),
        ]);
        if (harness.Port == null)
            return;
        await harness.ApprovePeerAsync();

        using var request = harness.Signed(HttpMethod.Get, "/rest/getAlbumList2");
        var response = await harness.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var envelope = JsonSerializer.Deserialize<SubsonicEnvelope>(
            await response.Content.ReadAsStringAsync(), JsonOptions)!;
        var albums = envelope.Response!.AlbumList2!.Album;
        Assert.Equal(2, albums.Count);
        Assert.Contains(albums, a => a.Name == "First");
        Assert.Contains(albums, a => a.Name == "Second");
    }

    // ── Conditional manifest pull (Tier 1.4) ─────────────────────────────────

    [Fact]
    public async Task The_library_manifest_is_served_with_the_libraries_change_token_as_its_ETag()
    {
        var directory = Path.Combine(_tempHome, "music-etag");
        Directory.CreateDirectory(directory);
        using var harness = new Harness([TrackWithFile(directory, "Song One")]);
        if (harness.Port == null)
            return;
        await harness.ApprovePeerAsync();

        using var request = harness.Signed(HttpMethod.Get, "/api/flower/v1/library");
        var response = await harness.Http.SendAsync(request);

        Assert.Equal(harness.Library.ChangeToken, response.Headers.GetValues("ETag").Single());
    }

    [Fact]
    public async Task An_unchanged_catalog_answers_304_with_no_body_at_all()
    {
        var directory = Path.Combine(_tempHome, "music-304");
        Directory.CreateDirectory(directory);
        using var harness = new Harness([TrackWithFile(directory, "Song One")]);
        if (harness.Port == null)
            return;
        await harness.ApprovePeerAsync();

        using var first = harness.Signed(HttpMethod.Get, "/api/flower/v1/library");
        using var firstResponse = await harness.Http.SendAsync(first);
        var token = firstResponse.Headers.GetValues("ETag").Single();

        using var second = harness.Signed(HttpMethod.Get, "/api/flower/v1/library");
        second.Headers.TryAddWithoutValidation("If-None-Match", token);
        var secondResponse = await harness.Http.SendAsync(second);

        Assert.Equal(HttpStatusCode.NotModified, secondResponse.StatusCode);
        Assert.Empty(await secondResponse.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_changed_catalog_invalidates_the_token_and_serves_the_new_manifest()
    {
        var directory = Path.Combine(_tempHome, "music-changed");
        Directory.CreateDirectory(directory);
        var first = TrackWithFile(directory, "Song One");
        using var harness = new Harness([first]);
        if (harness.Port == null)
            return;
        await harness.ApprovePeerAsync();

        using var before = harness.Signed(HttpMethod.Get, "/api/flower/v1/library");
        using var beforeResponse = await harness.Http.SendAsync(before);
        var staleToken = beforeResponse.Headers.GetValues("ETag").Single();

        harness.Library.UpdateTracks([first, TrackWithFile(directory, "Song Two")]);

        // The client presents the token it holds; the server has moved on, so
        // this must be a full 200 rather than a 304 against a stale token.
        using var after = harness.Signed(HttpMethod.Get, "/api/flower/v1/library");
        after.Headers.TryAddWithoutValidation("If-None-Match", staleToken);
        var afterResponse = await harness.Http.SendAsync(after);

        Assert.Equal(HttpStatusCode.OK, afterResponse.StatusCode);
        Assert.NotEqual(staleToken, afterResponse.Headers.GetValues("ETag").Single());
        var manifest = JsonSerializer.Deserialize<LibrarySyncManifestDto>(
            await afterResponse.Content.ReadAsStringAsync(), JsonOptions)!;
        Assert.Equal(2, manifest.Songs.Count);
    }

    [Fact]
    public async Task Info_advertises_the_same_library_token_the_manifest_serves_as_its_ETag()
    {
        var directory = Path.Combine(_tempHome, "music-info-token");
        Directory.CreateDirectory(directory);
        var track = TrackWithFile(directory, "Song One");
        using var harness = new Harness([track]);
        if (harness.Port == null)
            return;
        await harness.ApprovePeerAsync();

        async Task<string> InfoTokenAsync()
        {
            using var response = await harness.Http.GetAsync(harness.Url("/api/localsend/v2/info"));
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("libraryToken").GetString()!;
        }

        using var request = harness.Signed(HttpMethod.Get, "/api/flower/v1/library");
        using var manifestResponse = await harness.Http.SendAsync(request);
        Assert.Equal(manifestResponse.Headers.GetValues("ETag").Single(), await InfoTokenAsync());

        // This is the whole point: a change made here, with no request from
        // the peer, is visible on the poll every client already runs.
        var before = await InfoTokenAsync();
        harness.Library.UpdateTracks([track, TrackWithFile(directory, "Song Two")]);
        Assert.NotEqual(before, await InfoTokenAsync());
    }
}
