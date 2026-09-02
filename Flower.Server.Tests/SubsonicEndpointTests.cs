using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Flower.Models;
using Flower.Persistence;
using Flower.Persistence.Sql;
using Flower.Server.Services;
using Flower.Services;

namespace Flower.Server.Tests;

// Boots the real server in-process against a throwaway SQLite file and an
// empty library, then drives the real routes over HTTP.
//
// This is the harness ARCHITECTURE-REVIEW Tier 5.1 called for, and it is worth
// the setup cost rather than testing the query shapes through a seam: hand-
// written SQL is a string until something runs it, so a wrong column name or a
// GROUP BY that does not match the SELECT compiles perfectly and fails only
// when a real request reaches a real database. (The EF version had the same
// property for a different reason - its two known defects, SQLite refusing
// Max() over a DateTimeOffset and a grouped aggregate translating only as a
// member initializer, also compiled cleanly and threw at request time.)
public sealed class SubsonicServerFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "flower-server-tests-" + Guid.NewGuid());

    // An empty directory, deliberately: leaving LibraryPaths unset would make
    // startup scan - and these tests depend on - whatever music happens to be
    // on the machine. Pinning it is only half of that; see the
    // IntegrateWithITunes setting below for the other half.
    private readonly string _emptyLibrary =
        Path.Combine(Path.GetTempPath(), "flower-server-tests-lib-" + Guid.NewGuid());

    private readonly string _noWebUi =
        Path.Combine(Path.GetTempPath(), "flower-server-tests-noweb-" + Guid.NewGuid());

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_dataDirectory);
        Directory.CreateDirectory(_emptyLibrary);
        Directory.CreateDirectory(_noWebUi);

        builder.UseSetting("Flower:DataDirectory", _dataDirectory);
        builder.UseSetting("Flower:LibraryPaths:0", _emptyLibrary);

        // Off, because it defaults to on and a server that finds a Music.app
        // media folder adopts it as a library path on its first scan (see
        // LibraryImportService.AdoptAppleMusicFolderAsync). On a developer's
        // Mac that is their real ~/Music, so pinning LibraryPaths above is not
        // on its own enough to keep these tests off it - the server appends to
        // the pinned list and scans 16k real songs on every run.
        builder.UseSetting("Flower:IntegrateWithITunes", "false");

        // Pinned at an empty directory so these tests see the same server
        // whether or not a developer has dropped a real Flower.Web bundle into
        // Flower.Server/wwwroot - which is a normal thing to do locally (it is
        // how the "Server Settings..." button is exercised by hand) and which
        // the test host's content root would otherwise pick up. A configured
        // path is authoritative, so this is genuinely "no web UI deployed".
        builder.UseSetting("Flower:WebUiPath", _noWebUi);

        // The admin settings route asks PublicAddressProbe what the internet
        // sees this server as, which is the one outbound call this server makes
        // to anybody else. Pinned at a handler that refuses, for the same reason
        // the library path above is pinned at an empty folder: a test whose
        // result depends on the developer's internet link is not a test. The
        // settings DTO then reports no public address, which is also what a
        // server with no route out reports.
        builder.ConfigureServices(services =>
            services.AddSingleton(sp => new PublicAddressProbe(
                sp.GetRequiredService<ILogger<PublicAddressProbe>>(), new OfflineHandler())));
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("Tests do not reach the internet.");
    }

    // The credential every /rest request in these tests authenticates with.
    // There is no configured admin password to use any more (SYNC-PLAN.md,
    // "Passwordless by design"): third-party Subsonic clients get per-client
    // credentials the server generates at runtime, so the fixture issues
    // itself one exactly the way the admin UI would.
    private SubsonicCredential? _credential;

    public async ValueTask InitializeAsync()
    {
        // Resolving from Services forces the host to build - and so the schema
        // to be created and the startup rescan to run - before any test seeds
        // rows. It also has to happen before the first authenticated request,
        // since that request is what the credential authenticates.
        _credential = await Services.GetRequiredService<SubsonicCredentialStore>().IssueAsync("tests");

        await SendAsync("/rest/ping" + AuthQuery);
        await SeedAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try { Directory.Delete(_dataDirectory, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_emptyLibrary, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_noWebUi, recursive: true); } catch { /* best effort */ }
    }

    // Two albums by one artist plus a third by another, with differing
    // DateAdded so "newest" has something real to order by, and one album
    // whose tracks disagree on Genre/Year so the Min()-based aggregation in
    // AlbumSummaries is actually exercised rather than trivially satisfied.
    // Seeded through the same TrackRepository.ReplaceAll the real rescan uses,
    // over the same FlowerDb the app resolved - so these tests exercise the
    // production write path, including the derived album_artist/artist_id/
    // album_id columns every browse query groups by. Writing rows by hand
    // would let a test pass against columns the app never actually fills.
    public Track[] Seeded { get; private set; } = [];

    private Task SeedAsync()
    {
        Seeded =
        [
            Song("/m/a1.mp3", "Alpha Song", "Aurora", "Alpha Album", "2001", "Rock", days: 30),
            Song("/m/a2.mp3", "Second Song", "Aurora", "Alpha Album", "2002", "Pop", days: 30),
            Song("/m/b1.mp3", "Beta Song", "Aurora", "Beta Album", "2010", "Jazz", days: 5),
            Song("/m/c1.mp3", "Love Song", "Zephyr", "Gamma Album", "1999", "Folk", days: 1),
            // Accented on purpose. Same artist and album as the row above so
            // that artist/album counts elsewhere in this class do not move.
            Song("/m/c2.mp3", "Café Crème", "Zephyr", "Gamma Album", "1999", "Folk", days: 1),
        ];

        new TrackRepository(Services.GetRequiredService<FlowerDb>()).ReplaceAll(Seeded);

        // Publishing through LibraryImportService.LoadStored, not by handing
        // the Library the array directly: since the server reads from its
        // resident snapshot rather than per-request SQL, seeding the database
        // behind its back proves nothing. This makes the fixture exercise the
        // same store-then-load path startup does, so a track that cannot
        // round-trip through the schema fails here rather than passing against
        // objects the database never saw.
        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<LibraryImportService>().LoadStored();
        return Task.CompletedTask;
    }

    public FlowerDb Db => Services.GetRequiredService<FlowerDb>();

    private static Track Song(
        string path, string title, string artist, string album, string year, string genre, int days) =>
        new()
        {
            Path = path,
            Title = title,
            Artists = artist,
            AlbumArtists = artist,
            Album = album,
            Year = year,
            Genre = genre,
            Duration = TimeSpan.FromSeconds(100),
            DateAdded = DateTimeOffset.UtcNow.AddDays(-days),
        };

    // Classic Subsonic token auth against this fixture's own issued
    // credential. Built at runtime rather than being a shared constant: the
    // username and password are generated when the fixture starts, so there is
    // no compile-time credential left to hard-code.
    public string AuthQuery => AuthAs(Credential.Username, Credential.Password);

    // For the negative cases - a valid-looking request carrying the wrong
    // secret, or a username that was never issued.
    public static string AuthAs(string username, string password)
    {
        const string salt = "testsalt";
        var token = OpenSubsonicClient.ComputeToken(password, salt);
        return $"?u={Uri.EscapeDataString(username)}&t={token}&s={salt}&f=json&v=1.16.1&c=tests";
    }

    public SubsonicCredential Credential =>
        _credential ?? throw new InvalidOperationException("InitializeAsync has not run yet.");

    // Requests go through TestServer.SendAsync rather than an HttpClient
    // specifically so the connection's remote address can be set.
    //
    // The app's first middleware is LanGuard, which rejects a null
    // RemoteIpAddress outright - and under the test transport that is exactly
    // what an HttpClient-issued request has, so every endpoint test would
    // otherwise just be asserting "403". That is real behaviour worth keeping
    // (see LanGuardTests below), not something to configure away, so the tests
    // present a loopback client instead of the middleware being bypassed.
    public async Task<(HttpStatusCode Status, string Body)> SendAsync(
        string pathAndQuery, string remoteIp = "127.0.0.1")
    {
        var split = pathAndQuery.IndexOf('?');
        var path = split < 0 ? pathAndQuery : pathAndQuery[..split];
        var query = split < 0 ? "" : pathAndQuery[split..];

        var context = await Server.SendAsync(c =>
        {
            c.Request.Method = HttpMethods.Get;
            c.Request.Path = path;
            c.Request.QueryString = new QueryString(query);
            c.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        });

        // Read straight through, no seeking: TestServer swaps in its own
        // non-seekable ResponseBodyReaderStream, which throws on set_Position.
        using var reader = new StreamReader(context.Response.Body);
        return ((HttpStatusCode)context.Response.StatusCode, await reader.ReadToEndAsync());
    }

    public async Task<JsonElement> GetAsync(string pathAndQuery)
    {
        var (status, body) = await SendAsync(pathAndQuery);
        Assert.Equal(HttpStatusCode.OK, status);

        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("subsonic-response").Clone();
    }
}

public class SubsonicEndpointTests(SubsonicServerFixture server) : IClassFixture<SubsonicServerFixture>
{
    [Fact]
    public async Task Ping_succeeds_with_valid_credentials()
    {
        var response = await server.GetAsync("/rest/ping" + server.AuthQuery);

        Assert.Equal("ok", response.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_request_with_no_credentials_is_refused()
    {
        var response = await server.GetAsync("/rest/getAlbumList2?f=json");

        Assert.Equal("failed", response.GetProperty("status").GetString());
        Assert.Equal(40, response.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task A_request_with_the_wrong_password_is_refused()
    {
        var response = await server.GetAsync("/rest/getAlbumList2" + SubsonicServerFixture.AuthAs(server.Credential.Username, "wrong"));

        Assert.Equal(40, response.GetProperty("error").GetProperty("code").GetInt32());
    }

    // ── Tier 1.3: the SQL-side grouping ──────────────────────────────────────

    [Theory]
    [InlineData("alphabeticalByName")]
    [InlineData("alphabeticalByArtist")]
    [InlineData("newest")]
    [InlineData("random")]
    public async Task Every_getAlbumList2_sort_type_executes(string type)
    {
        // Each type is a different ORDER BY spliced into the same aggregate,
        // and none of them is validated until SQLite parses it - a regression
        // in any single one is invisible to a test that only covers the
        // default. "newest" (MAX(date_added)) and "random" (RANDOM()) are the
        // two that are not a plain column sort.
        var response = await server.GetAsync($"/rest/getAlbumList2{server.AuthQuery}&type={type}&size=10");

        Assert.Equal("ok", response.GetProperty("status").GetString());
        var albums = response.GetProperty("albumList2").GetProperty("album");
        Assert.Equal(3, albums.GetArrayLength());
    }

    [Fact]
    public async Task getAlbumList2_aggregates_song_count_and_duration_per_album()
    {
        var response = await server.GetAsync($"/rest/getAlbumList2{server.AuthQuery}&type=alphabeticalByName&size=10");

        var alpha = response.GetProperty("albumList2").GetProperty("album")
            .EnumerateArray().Single(a => a.GetProperty("name").GetString() == "Alpha Album");

        // Two tracks, 100s each - the SQL Count()/Sum() the in-memory
        // GroupBy used to do.
        Assert.Equal(2, alpha.GetProperty("songCount").GetInt32());
        Assert.Equal(200, alpha.GetProperty("duration").GetInt64());
        Assert.Equal("Aurora", alpha.GetProperty("artist").GetString());
    }

    [Fact]
    public async Task getAlbumList2_orders_newest_by_most_recently_added_album()
    {
        var response = await server.GetAsync($"/rest/getAlbumList2{server.AuthQuery}&type=newest&size=10");

        var names = response.GetProperty("albumList2").GetProperty("album")
            .EnumerateArray().Select(a => a.GetProperty("name").GetString()).ToList();

        // Gamma (1 day) then Beta (5) then Alpha (30). Ordered by
        // MAX(date_added) in SQL - possible only because the shared schema
        // stores timestamps as INTEGER ticks; EF had to aggregate this one in
        // memory because the provider refuses MAX() over a DateTimeOffset.
        Assert.Equal(["Gamma Album", "Beta Album", "Alpha Album"], names);
    }

    [Fact]
    public async Task getAlbumList2_paginates()
    {
        var first = await server.GetAsync($"/rest/getAlbumList2{server.AuthQuery}&type=alphabeticalByName&size=2&offset=0");
        var second = await server.GetAsync($"/rest/getAlbumList2{server.AuthQuery}&type=alphabeticalByName&size=2&offset=2");

        Assert.Equal(2, first.GetProperty("albumList2").GetProperty("album").GetArrayLength());
        // Skip/Take now happen in SQL rather than over a fully materialized list.
        Assert.Equal(1, second.GetProperty("albumList2").GetProperty("album").GetArrayLength());
    }

    [Fact]
    public async Task getArtists_counts_distinct_albums_per_artist()
    {
        var response = await server.GetAsync("/rest/getArtists" + server.AuthQuery);

        var artists = response.GetProperty("artists").GetProperty("index")
            .EnumerateArray()
            .SelectMany(i => i.GetProperty("artist").EnumerateArray())
            .ToList();

        var aurora = artists.Single(a => a.GetProperty("name").GetString() == "Aurora");
        // Two albums across three tracks - the distinct count is the part the
        // (artist, album) pair grouping exists to get right.
        Assert.Equal(2, aurora.GetProperty("albumCount").GetInt32());
        Assert.Equal(1, artists.Single(a => a.GetProperty("name").GetString() == "Zephyr")
            .GetProperty("albumCount").GetInt32());
    }

    [Fact]
    public async Task search3_matches_songs_albums_and_artists()
    {
        var response = await server.GetAsync($"/rest/search3{server.AuthQuery}&query=Love");

        var result = response.GetProperty("searchResult3");
        Assert.Equal("Love Song", result.GetProperty("song")
            .EnumerateArray().Single().GetProperty("title").GetString());
    }

    [Fact]
    public async Task search3_matching_is_case_insensitive_in_one_pass()
    {
        // There used to be two filters - a SQL LIKE and an in-memory
        // OrdinalIgnoreCase re-filter - that genuinely disagreed, so a match
        // SQLite accepted could be dropped again afterwards. One filter now
        // decides, and it must be case-insensitive.
        var lower = await server.GetAsync($"/rest/search3{server.AuthQuery}&query=love");
        var upper = await server.GetAsync($"/rest/search3{server.AuthQuery}&query=LOVE");

        Assert.Equal(
            lower.GetProperty("searchResult3").GetProperty("song").GetArrayLength(),
            upper.GetProperty("searchResult3").GetProperty("song").GetArrayLength());
    }

    // Beside SearchText's own tests rather than instead of them: this one pins
    // that the server's search endpoint is actually wired to the shared fold, so
    // a phone searching its local library and the same phone searching the
    // server get the same answer for the same query.
    [Fact]
    public async Task search3_finds_an_accented_title_without_the_accent()
    {
        var response = await server.GetAsync($"/rest/search3{server.AuthQuery}&query=cafe");

        Assert.Equal("Café Crème", response.GetProperty("searchResult3").GetProperty("song")
            .EnumerateArray().Single().GetProperty("title").GetString());
    }

    [Fact]
    public async Task search3_honours_its_result_limits()
    {
        var response = await server.GetAsync($"/rest/search3{server.AuthQuery}&query=Song&songCount=2");

        // The Take now happens in SQL, so this pins that it still applies at
        // all rather than returning everything that matched.
        Assert.Equal(2, response.GetProperty("searchResult3").GetProperty("song").GetArrayLength());
    }

    [Fact]
    public async Task getAlbum_returns_its_songs_in_disc_and_track_order()
    {
        var albumId = SubsonicIdentity.AlbumId("Aurora", "Alpha Album");

        var response = await server.GetAsync($"/rest/getAlbum{server.AuthQuery}&id={Uri.EscapeDataString(albumId)}");

        Assert.Equal(2, response.GetProperty("album").GetProperty("song").GetArrayLength());
    }

    [Fact]
    public async Task An_unknown_album_reports_not_found_rather_than_throwing()
    {
        var response = await server.GetAsync($"/rest/getAlbum{server.AuthQuery}&id=al-nope");

        Assert.Equal(70, response.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task A_missing_required_id_is_a_parameter_error()
    {
        var response = await server.GetAsync("/rest/getAlbum" + server.AuthQuery);

        Assert.Equal(10, response.GetProperty("error").GetProperty("code").GetInt32());
    }
}


// The first middleware in the pipeline, and the only thing standing between
// the whole REST surface and the open internet if the server is ever exposed
// beyond a LAN. It had no coverage at all.
public class LanGuardTests(SubsonicServerFixture server) : IClassFixture<SubsonicServerFixture>
{
    [Theory]
    [InlineData("127.0.0.1")]      // loopback
    [InlineData("192.168.1.50")]   // RFC1918
    [InlineData("10.0.0.7")]       // RFC1918
    [InlineData("172.16.4.2")]     // RFC1918
    [InlineData("100.101.102.103")] // Tailscale CGNAT
    public async Task A_private_or_loopback_client_is_allowed_through(string ip)
    {
        var (status, _) = await server.SendAsync("/rest/ping" + server.AuthQuery, ip);

        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("203.0.113.9")]
    [InlineData("172.32.0.1")]  // just outside the 172.16/12 block
    public async Task A_public_client_is_refused_before_reaching_any_endpoint(string ip)
    {
        // Dropped by the middleware, not answered with a Subsonic error body -
        // the request never reaches the route table or the auth filter.
        await GuardedRequest.AssertDropped(async () =>
            (await server.SendAsync("/rest/ping" + server.AuthQuery, ip)).Status);
    }

    [Fact]
    public async Task The_guard_applies_to_unauthenticated_requests_too()
    {
        // Order matters: a public client must be cut off before it can even
        // probe which credentials the server accepts.
        await GuardedRequest.AssertDropped(async () =>
            (await server.SendAsync("/rest/getAlbumList2?f=json", "8.8.8.8")).Status);
    }
}

// The /rest surface had no rate limiting at all (ARCHITECTURE-REVIEW Tier
// 3.1), unlike AdminEndpoints and PairingEndpoints - and classic Subsonic
// auth gives a guesser unlimited free retries.
//
// Each test uses its own source IP: the limiters are static (one budget per
// process, keyed by source) and deliberately outlive any single request, so
// sharing an IP would make these tests order-dependent on each other and on
// the endpoint tests above.
public class SubsonicRateLimitTests(SubsonicServerFixture server) : IClassFixture<SubsonicServerFixture>
{
    private static int ErrorCode(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement
            .GetProperty("subsonic-response").GetProperty("error").GetProperty("code").GetInt32();
    }

    [Fact]
    public async Task Repeated_failed_logins_from_one_source_are_locked_out()
    {
        const string ip = "10.20.30.40";
        var wrong = "/rest/ping" + SubsonicServerFixture.AuthAs(server.Credential.Username, "wrong");

        for (var i = 0; i < 10; i++)
        {
            var (status, body) = await server.SendAsync(wrong, ip);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal(40, ErrorCode(body));
        }

        // Eleventh attempt: over the failed-auth budget, so the source stops
        // getting an answer at all rather than another free guess.
        var (limited, _) = await server.SendAsync(wrong, ip);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited);
    }

    [Fact]
    public async Task A_locked_out_source_is_refused_even_with_correct_credentials()
    {
        const string ip = "10.20.30.41";
        var wrong = "/rest/ping" + SubsonicServerFixture.AuthAs(server.Credential.Username, "wrong");

        for (var i = 0; i < 11; i++)
        {
            await server.SendAsync(wrong, ip);
        }

        // The lockout is on the attempt, not on the guess - otherwise finally
        // landing the right password would clear the penalty. Which is why the
        // budget is checked *before* the credential is compared: an over-budget
        // attempt is refused unevaluated, so no amount of burning it can turn
        // into a lucky guess being admitted.
        var (status, _) = await server.SendAsync("/rest/ping" + server.AuthQuery, ip);
        Assert.Equal(HttpStatusCode.TooManyRequests, status);
    }

    // docs/OPEN-INTERNET-REVIEW.md finding #2: the failed-auth budget used to be
    // peeked before authentication, which made it a lockout of the whole /rest
    // surface. Two listeners behind one house NAT already shared that key, and
    // behind a tunnel with TrustedProxies unset it is everybody - so ten bad
    // passwords from one caller took the library away from all of them.

    [Fact]
    public async Task A_paired_device_gets_in_while_a_password_guesser_shares_its_address()
    {
        const string ip = "10.20.30.45";
        var wrong = "/rest/ping" + SubsonicServerFixture.AuthAs(server.Credential.Username, "wrong");

        for (var i = 0; i < 11; i++)
        {
            await server.SendAsync(wrong, ip);
        }

        // A signature cannot be guessed, so nothing is bought by refusing one
        // because somebody sharing the address got a password wrong.
        var device = NewDevice();
        await server.Services.GetRequiredService<TrustedPeerStore>()
            .ApproveAsync(device.Fingerprint, "Kitchen iPad", device.PublicKeyBase64);
        try
        {
            var (status, body) = await SendSignedAsync(device, "/rest/ping", ip);

            Assert.Equal(HttpStatusCode.OK, status);
            using var document = JsonDocument.Parse(body);
            Assert.Equal("ok", document.RootElement.GetProperty("subsonic-response").GetProperty("status").GetString());
        }
        finally
        {
            await server.Services.GetRequiredService<TrustedPeerStore>().RevokeAsync(device.Fingerprint);
        }
    }

    [Fact]
    public async Task Hammering_one_account_does_not_lock_out_another_from_the_same_address()
    {
        const string ip = "10.20.30.46";
        var guessing = "/rest/ping" + SubsonicServerFixture.AuthAs("an-account-that-was-never-issued", "wrong");

        for (var i = 0; i < 11; i++)
        {
            await server.SendAsync(guessing, ip);
        }

        // The budget is keyed by source *and* username, so the client this
        // server actually issued a credential to keeps working.
        var (status, _) = await server.SendAsync("/rest/ping" + server.AuthQuery, ip);
        Assert.Equal(HttpStatusCode.OK, status);
    }

    private static DeviceSigningKey NewDevice()
    {
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeyRaw = ecdsa.ExportParameters(false) is { Q.X: { } x, Q.Y: { } y }
            ? (byte[])[0x04, .. x, .. y]
            : throw new InvalidOperationException("no public point");
        return new DeviceSigningKey(ecdsa, publicKeyRaw);
    }

    // The same shape PeerOpenSubsonicClientFactory sends: empty u/t/s, identity
    // and signature in headers.
    private async Task<(HttpStatusCode Status, string Body)> SendSignedAsync(
        DeviceSigningKey device, string path, string remoteIp)
    {
        var query = new List<(string Key, string Value)>
        {
            ("u", ""), ("t", ""), ("s", ""), ("v", "1.16.1"), ("c", "tests"), ("f", "json"),
        };
        var identity = new List<(string Key, string Value)>
        {
            ("X-Flower-Fingerprint", device.Fingerprint),
            ("X-Flower-PublicKey", device.PublicKeyBase64),
        };
        var (signature, timestamp, nonce) = device.Sign("GET", path, query.Concat(identity), body: []);

        var context = await server.Server.SendAsync(c =>
        {
            c.Request.Method = HttpMethods.Get;
            c.Request.Path = path;
            c.Request.QueryString = new QueryString("?" + string.Join("&", query.Select(p =>
                $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}")));
            c.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
            foreach (var (key, value) in identity)
                c.Request.Headers[key] = value;
            c.Request.Headers["X-Flower-Signature"] = signature;
            c.Request.Headers["X-Flower-Timestamp"] = timestamp;
            c.Request.Headers["X-Flower-Nonce"] = nonce;
        });

        using var reader = new StreamReader(context.Response.Body);
        return ((HttpStatusCode)context.Response.StatusCode, await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task One_source_being_locked_out_does_not_affect_another()
    {
        const string attacker = "10.20.30.42";
        const string bystander = "10.20.30.43";
        var wrong = "/rest/ping" + SubsonicServerFixture.AuthAs(server.Credential.Username, "wrong");

        for (var i = 0; i < 11; i++)
        {
            await server.SendAsync(wrong, attacker);
        }

        var (status, body) = await server.SendAsync("/rest/ping" + server.AuthQuery, bystander);
        Assert.Equal(HttpStatusCode.OK, status);
        using var document = JsonDocument.Parse(body);
        Assert.Equal("ok", document.RootElement.GetProperty("subsonic-response").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Normal_authenticated_traffic_is_not_rate_limited()
    {
        const string ip = "10.20.30.44";

        // Well under the 600/60s request ceiling, which is sized for an album
        // grid's burst of getCoverArt calls rather than for a single client
        // browsing slowly.
        for (var i = 0; i < 50; i++)
        {
            var (status, _) = await server.SendAsync("/rest/ping" + server.AuthQuery, ip);
            Assert.Equal(HttpStatusCode.OK, status);
        }
    }
}

// A server nobody has ever paired with cannot be administered: pairing codes
// are issued from /api/admin, and /api/admin only admits a device that already
// paired as an admin. Program.cs breaks that circularity by minting one
// admin-granting code itself at startup and printing it, which on a headless
// box means it lands in `docker logs`.
//
// This replaces the old "refuse to boot without a configured admin password"
// check - there is no admin password any more (SYNC-PLAN.md, "Passwordless by
// design"), so the failure mode it guarded against no longer exists.
public class BootstrapPairingCodeTests
{
    private sealed class Factory : WebApplicationFactory<Program>
    {
        public string DataDirectory { get; } =
            Path.Combine(Path.GetTempPath(), "flower-server-bootstrap-" + Guid.NewGuid());

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            var emptyLibrary = Path.Combine(DataDirectory, "lib");
            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(emptyLibrary);

            builder.UseSetting("Flower:DataDirectory", DataDirectory);
            builder.UseSetting("Flower:LibraryPaths:0", emptyLibrary);
            builder.UseSetting("Flower:IntegrateWithITunes", "false");
        }
    }

    [Fact]
    public void A_server_with_no_admin_prints_a_pairing_code_at_startup()
    {
        var previous = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            using var factory = new Factory();
            _ = factory.Services;
        }
        finally
        {
            Console.SetOut(previous);
        }

        var output = captured.ToString();
        Assert.Contains("No device can administer this server yet", output);
        // The invite, not just the bare code: it carries the server's own
        // fingerprint, which is what lets the pairing device pin the key
        // rather than trusting whatever answers at that address.
        Assert.Contains("flower://pair?", output);
        Assert.Contains("fp=", output);
    }

    [Fact]
    public void A_server_that_already_has_an_admin_does_not_print_one()
    {
        // Otherwise every restart would broadcast a live admin-granting
        // credential to the logs of an already-configured server.
        var factory = new Factory();
        var previousDataDirectory = PlatformDataDirectory.Current;
        try
        {
            PlatformDataDirectory.Current = factory.DataDirectory;
            Directory.CreateDirectory(factory.DataDirectory);
            new TrustedPeerStore(NullLogger<TrustedPeerStore>.Instance)
                .ApproveAsync("fingerprint", "Existing admin", "public-key", isAdmin: true)
                .GetAwaiter().GetResult();
        }
        finally
        {
            PlatformDataDirectory.Current = previousDataDirectory;
        }

        var previous = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            _ = factory.Services;
        }
        finally
        {
            Console.SetOut(previous);
            factory.Dispose();
        }

        Assert.DoesNotContain("No device can administer this server yet", captured.ToString());
    }
}

// The surface that moved from EF Core's change tracker to hand-written SQL and
// had no coverage at all before: playlist CRUD, star/unstar, scrobble and the
// stream/download lookup. Each of these used to be "load the entity, mutate a
// property, SaveChanges", where the mapping was EF's problem; they are now
// statements this project has to get right itself.
public class SubsonicWriteEndpointTests(SubsonicServerFixture server) : IClassFixture<SubsonicServerFixture>
{
    private string SongId(string title) =>
        server.Seeded.Single(t => t.Title == title).Id.ToString("N");

    [Fact]
    public void The_schema_is_at_the_latest_migration()
    {
        // The server no longer creates its own schema: it shares Flower.Core's
        // migration runner, which is the whole of ARCHITECTURE-REVIEW Tier
        // 2.5's server half. Before this it called EnsureCreatedAsync(), which
        // stamps no version at all - so a self-hoster's only upgrade path
        // after a schema change was deleting flower.db.
        using var connection = server.Db.Open();

        Assert.Equal(SqliteMigrations.LatestVersion, SqliteMigrations.ReadVersion(connection));
    }

    [Fact]
    public async Task A_playlist_round_trips_through_create_and_get()
    {
        var songs = new[] { SongId("Alpha Song"), SongId("Beta Song") };
        var created = await server.GetAsync(
            $"/rest/createPlaylist{server.AuthQuery}&name=Road+Trip&songId={songs[0]}&songId={songs[1]}");

        var playlist = created.GetProperty("playlist");
        Assert.Equal("Road Trip", playlist.GetProperty("name").GetString());
        Assert.Equal(2, playlist.GetProperty("songCount").GetInt32());
        // Two 100s tracks - summed from the resolved tracks, not stored.
        Assert.Equal(200, playlist.GetProperty("duration").GetInt64());

        // Order is membership order, not the order rows happen to come back in.
        Assert.Equal(
            ["Alpha Song", "Beta Song"],
            playlist.GetProperty("entry").EnumerateArray().Select(e => e.GetProperty("title").GetString()));

        await server.GetAsync($"/rest/deletePlaylist{server.AuthQuery}&id={playlist.GetProperty("id").GetString()}");
    }

    [Fact]
    public async Task Updating_a_playlist_removes_by_index_and_appends_by_id()
    {
        var created = await server.GetAsync(
            $"/rest/createPlaylist{server.AuthQuery}&name=Edited&songId={SongId("Alpha Song")}&songId={SongId("Beta Song")}");
        var id = created.GetProperty("playlist").GetProperty("id").GetString();

        // Drop position 0 and append another - the two operations Subsonic
        // sends together, and the reason membership is rewritten wholesale
        // rather than diffed (removing an entry shifts every position after it).
        var updated = await server.GetAsync(
            $"/rest/updatePlaylist{server.AuthQuery}&playlistId={id}&songIndexToRemove=0&songIdToAdd={SongId("Love Song")}");
        Assert.Equal("ok", updated.GetProperty("status").GetString());

        var reread = await server.GetAsync($"/rest/getPlaylist{server.AuthQuery}&id={id}");
        Assert.Equal(
            ["Beta Song", "Love Song"],
            reread.GetProperty("playlist").GetProperty("entry")
                .EnumerateArray().Select(e => e.GetProperty("title").GetString()));

        await server.GetAsync($"/rest/deletePlaylist{server.AuthQuery}&id={id}");
    }

    [Fact]
    public async Task A_playlist_name_survives_an_update_that_does_not_mention_it()
    {
        var created = await server.GetAsync($"/rest/createPlaylist{server.AuthQuery}&name=Keep+My+Name");
        var id = created.GetProperty("playlist").GetProperty("id").GetString();

        // updatePlaylist sends only what changed, so an absent attribute must
        // leave the stored value alone rather than null it - which is what a
        // naive "UPDATE ... SET name = $name" would have done.
        await server.GetAsync($"/rest/updatePlaylist{server.AuthQuery}&playlistId={id}&comment=later");

        var reread = await server.GetAsync($"/rest/getPlaylist{server.AuthQuery}&id={id}");
        Assert.Equal("Keep My Name", reread.GetProperty("playlist").GetProperty("name").GetString());
        Assert.Equal("later", reread.GetProperty("playlist").GetProperty("comment").GetString());

        await server.GetAsync($"/rest/deletePlaylist{server.AuthQuery}&id={id}");
    }

    // A smart playlist is the one thing on this surface a third-party client
    // can see but must not edit: getPlaylist reports it as an ordinary
    // playlist, because that is all OpenSubsonic can describe, and an accepted
    // edit would be silently erased by the next recomputation. See
    // docs/SMART-PLAYLIST-PLAN.md, "Server / Subsonic surface".
    [Fact]
    public async Task updatePlaylist_is_refused_on_a_smart_playlist()
    {
        var created = await server.GetAsync(
            $"/rest/createPlaylist{server.AuthQuery}&name=Made+Smart&songId={SongId("Alpha Song")}");
        var id = created.GetProperty("playlist").GetProperty("id").GetString();

        var library = server.Services.GetRequiredService<Library>();
        var playlist = library.FindPlaylist(id!)!;
        // Frozen, so the server's own recomputation pass leaves the seeded
        // track alone and the assertions below are about this endpoint.
        playlist.Rules = SmartPlaylistRules.MatchAll(
            new SmartCondition(SmartField.PlayCount, SmartOperator.GreaterThan, new SmartValue.Number(0)))
            with { LiveUpdating = false };

        var refused = await server.GetAsync(
            $"/rest/updatePlaylist{server.AuthQuery}&playlistId={id}&name=Renamed"
            + $"&songIndexToRemove=0&songIdToAdd={SongId("Beta Song")}");
        Assert.Equal(50, refused.GetProperty("error").GetProperty("code").GetInt32());

        // Nothing was partly applied - not the rename either.
        var reread = await server.GetAsync($"/rest/getPlaylist{server.AuthQuery}&id={id}");
        Assert.Equal("Made Smart", reread.GetProperty("playlist").GetProperty("name").GetString());
        Assert.Equal(
            ["Alpha Song"],
            reread.GetProperty("playlist").GetProperty("entry")
                .EnumerateArray().Select(e => e.GetProperty("title").GetString()));

        // Deleting one is still allowed - it is the contents that come from the
        // rules, not the playlist's right to exist.
        Assert.Equal("ok", (await server.GetAsync($"/rest/deletePlaylist{server.AuthQuery}&id={id}"))
            .GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_deleted_playlist_is_gone_and_deleting_it_again_reports_not_found()
    {
        var created = await server.GetAsync($"/rest/createPlaylist{server.AuthQuery}&name=Temporary");
        var id = created.GetProperty("playlist").GetProperty("id").GetString();

        Assert.Equal("ok", (await server.GetAsync($"/rest/deletePlaylist{server.AuthQuery}&id={id}"))
            .GetProperty("status").GetString());

        var gone = await server.GetAsync($"/rest/getPlaylist{server.AuthQuery}&id={id}");
        Assert.Equal(70, gone.GetProperty("error").GetProperty("code").GetInt32());

        var again = await server.GetAsync($"/rest/deletePlaylist{server.AuthQuery}&id={id}");
        Assert.Equal(70, again.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task A_playlist_entry_whose_track_is_gone_is_skipped_rather_than_failing()
    {
        // playlist_tracks.track_id is deliberately not a foreign key (see
        // Schema.V1), so a rescan dropping a deleted file does not have to
        // cascade through every playlist referencing it. The unresolvable
        // entry is dropped on read instead.
        var created = await server.GetAsync(
            $"/rest/createPlaylist{server.AuthQuery}&name=Half+Missing"
            + $"&songId={SongId("Alpha Song")}&songId={Guid.NewGuid():N}");

        var playlist = created.GetProperty("playlist");
        Assert.Equal(1, playlist.GetProperty("songCount").GetInt32());

        await server.GetAsync($"/rest/deletePlaylist{server.AuthQuery}&id={playlist.GetProperty("id").GetString()}");
    }

    [Fact]
    public async Task Starring_an_album_stars_every_track_on_it_and_unstarring_clears_them()
    {
        var albumId = SubsonicIdentity.AlbumId("Aurora", "Alpha Album");

        await server.GetAsync($"/rest/star{server.AuthQuery}&albumId={Uri.EscapeDataString(albumId)}");
        var starred = await server.GetAsync($"/rest/getAlbum{server.AuthQuery}&id={Uri.EscapeDataString(albumId)}");
        Assert.All(
            starred.GetProperty("album").GetProperty("song").EnumerateArray(),
            song => Assert.True(song.GetProperty("starred").GetBoolean()));

        await server.GetAsync($"/rest/unstar{server.AuthQuery}&albumId={Uri.EscapeDataString(albumId)}");
        var cleared = await server.GetAsync($"/rest/getAlbum{server.AuthQuery}&id={Uri.EscapeDataString(albumId)}");
        Assert.All(
            cleared.GetProperty("album").GetProperty("song").EnumerateArray(),
            song => Assert.False(song.GetProperty("starred").GetBoolean()));
    }

    [Fact]
    public async Task Starring_by_artist_reaches_every_album_that_artist_has()
    {
        // Only works because album_artist/artist_id are written by the same
        // production path that writes the tags (TrackRepository, via
        // Track.EffectiveAlbumArtist) - a row seeded past that would have an
        // empty artist_id and match nothing.
        var artistId = SubsonicIdentity.ArtistId("Aurora");

        await server.GetAsync($"/rest/star{server.AuthQuery}&artistId={Uri.EscapeDataString(artistId)}");

        var beta = await server.GetAsync(
            $"/rest/getAlbum{server.AuthQuery}&id={Uri.EscapeDataString(SubsonicIdentity.AlbumId("Aurora", "Beta Album"))}");
        Assert.True(beta.GetProperty("album").GetProperty("song")
            .EnumerateArray().Single().GetProperty("starred").GetBoolean());

        await server.GetAsync($"/rest/unstar{server.AuthQuery}&artistId={Uri.EscapeDataString(artistId)}");
    }

    // The write-through property itself: since reads come from the resident
    // snapshot rather than from SQL, a write that only reached memory would
    // still look correct through the API and silently vanish on restart. Both
    // halves are asserted, from opposite sides.
    [Fact]
    public async Task A_star_is_visible_to_reads_and_on_disk_in_the_same_call()
    {
        var id = SongId("Love Song");

        await server.GetAsync($"/rest/star{server.AuthQuery}&id={id}");

        var song = await server.GetAsync($"/rest/getSong{server.AuthQuery}&id={id}");
        Assert.True(song.GetProperty("song").GetProperty("starred").GetBoolean());

        using (var connection = server.Db.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT starred, starred_at FROM tracks WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(1, reader.GetInt64(0));
            // Stamped, not left null - the client's History and the sync
            // protocol both order on it.
            Assert.False(reader.IsDBNull(1));
        }

        await server.GetAsync($"/rest/unstar{server.AuthQuery}&id={id}");
    }

    // A song id is a Guid, and Guid.TryParse accepts the dashed spelling as
    // readily as the 32-char hex the API hands out - so a client that
    // round-trips an id through its own Guid type and sends it back dashed
    // used to resolve in memory and then update no row, because the id column
    // holds hex. The endpoint papered over that by canonicalising the id
    // before passing it in; Library now tells the store the id of the track it
    // actually matched, which is the only value guaranteed to agree with the
    // row it is meant to update.
    [Fact]
    public async Task A_star_reaches_the_database_even_when_the_id_arrives_in_dashed_form()
    {
        var id = SongId("Love Song");
        var dashed = Guid.Parse(id).ToString("D");
        Assert.NotEqual(id, dashed);

        await server.GetAsync($"/rest/star{server.AuthQuery}&id={dashed}");

        using (var connection = server.Db.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT starred FROM tracks WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            Assert.Equal(1L, Assert.IsType<long>(command.ExecuteScalar()));
        }

        await server.GetAsync($"/rest/unstar{server.AuthQuery}&id={dashed}");
    }

    [Fact]
    public async Task Reloading_rebuilds_a_playlist_created_through_the_API_from_the_database()
    {
        var created = await server.GetAsync(
            $"/rest/createPlaylist{server.AuthQuery}&name=Survivor&songId={SongId("Alpha Song")}&songId={SongId("Beta Song")}");
        var id = created.GetProperty("playlist").GetProperty("id").GetString();

        await server.GetAsync($"/rest/updatePlaylist{server.AuthQuery}&playlistId={id}&comment=kept&public=true");

        // Playlists are resident now, so a write that only reached memory would
        // still read back correctly - this is what tells the two apart. It also
        // covers membership order and the two Subsonic attributes that used to
        // have no field on Playlist to load into, which is exactly why they
        // could not be reloaded before.
        using (var scope = server.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<LibraryImportService>().LoadStored();
        }

        var reread = await server.GetAsync($"/rest/getPlaylist{server.AuthQuery}&id={id}");
        var playlist = reread.GetProperty("playlist");
        Assert.Equal("Survivor", playlist.GetProperty("name").GetString());
        Assert.Equal("kept", playlist.GetProperty("comment").GetString());
        Assert.True(playlist.GetProperty("public").GetBoolean());
        Assert.Equal(
            ["Alpha Song", "Beta Song"],
            playlist.GetProperty("entry").EnumerateArray().Select(e => e.GetProperty("title").GetString()));

        await server.GetAsync($"/rest/deletePlaylist{server.AuthQuery}&id={id}");
    }

    [Fact]
    public async Task Reloading_the_stored_library_keeps_a_star_that_was_set_through_the_API()
    {
        var id = SongId("Beta Song");
        await server.GetAsync($"/rest/star{server.AuthQuery}&id={id}");

        // Rebuilds the resident snapshot from the database alone, which is what
        // a restart does. A star held only in memory would not survive this.
        using (var scope = server.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<LibraryImportService>().LoadStored();
        }

        var song = await server.GetAsync($"/rest/getSong{server.AuthQuery}&id={id}");
        Assert.True(song.GetProperty("song").GetProperty("starred").GetBoolean());

        await server.GetAsync($"/rest/unstar{server.AuthQuery}&id={id}");
    }

    [Fact]
    public async Task An_empty_search_query_matches_nothing_rather_than_the_whole_library()
    {
        // Clients call search3 on every keystroke, so the empty string has to
        // mean "no results", not "everything".
        var response = await server.GetAsync($"/rest/search3{server.AuthQuery}&query=");
        var result = response.GetProperty("searchResult3");

        Assert.False(result.TryGetProperty("song", out var songs) && songs.GetArrayLength() > 0);
        Assert.False(result.TryGetProperty("album", out var albums) && albums.GetArrayLength() > 0);
        Assert.False(result.TryGetProperty("artist", out var artists) && artists.GetArrayLength() > 0);
    }

    [Fact]
    public async Task Starring_with_no_target_is_a_parameter_error()
    {
        var response = await server.GetAsync("/rest/star" + server.AuthQuery);

        Assert.Equal(10, response.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task A_scrobble_submission_increments_that_tracks_play_count()
    {
        var id = SongId("Love Song");

        using (var connection = server.Db.Open())
        {
            Assert.Equal(0, PlayCount(connection, id));
        }

        await server.GetAsync($"/rest/scrobble{server.AuthQuery}&id={id}");
        await server.GetAsync($"/rest/scrobble{server.AuthQuery}&id={id}");

        using (var connection = server.Db.Open())
        {
            // Incremented on the resident Track and written through in the
            // same call, so the row agrees with what reads are already
            // reporting - see Library.RecordPlay.
            Assert.Equal(2, PlayCount(connection, id));
        }
    }

    [Fact]
    public async Task A_scrobble_with_submission_false_is_accepted_but_records_nothing()
    {
        var id = SongId("Beta Song");

        await server.GetAsync($"/rest/scrobble{server.AuthQuery}&id={id}&submission=false");

        using var connection = server.Db.Open();
        Assert.Equal(0, PlayCount(connection, id));
    }

    private static int PlayCount(Microsoft.Data.Sqlite.SqliteConnection connection, string id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT play_count FROM tracks WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    [Theory]
    [InlineData("%")]
    [InlineData("_")]
    public async Task A_query_of_only_LIKE_wildcards_matches_nothing(string query)
    {
        // "%" as a search term used to match the entire library, because the
        // pattern was interpolated straight into LIKE - a user searching for
        // "50%" got every title starting "50". The wildcards are escaped now,
        // with an explicit ESCAPE clause (SQLite has no default one).
        var response = await server.GetAsync($"/rest/search3{server.AuthQuery}&query={Uri.EscapeDataString(query)}");

        Assert.Equal(0, response.GetProperty("searchResult3").GetProperty("song").GetArrayLength());
        Assert.Equal(0, response.GetProperty("searchResult3").GetProperty("album").GetArrayLength());
    }

    [Fact]
    public async Task Streaming_a_track_whose_file_is_missing_is_a_404_not_a_500()
    {
        // The seeded rows point at paths that do not exist, which is exactly
        // the state a library left behind by deleted files is in.
        var (status, _) = await server.SendAsync($"/rest/stream{server.AuthQuery}&id={SongId("Alpha Song")}");

        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("")]
    public async Task A_malformed_song_id_is_handled_rather_than_thrown_on(string id)
    {
        // Ids are 32-char hex in the shared schema, and a Subsonic client is
        // free to send anything at all - including an id minted by a different
        // server. Parsed before it reaches SQL so it stays a clean "not found".
        var response = await server.GetAsync($"/rest/getSong{server.AuthQuery}&id={id}");

        Assert.Contains(
            response.GetProperty("error").GetProperty("code").GetInt32(),
            (int[])[10, 70]);
    }
}
