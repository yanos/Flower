using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Flower.Models;
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

    // An empty directory, deliberately: Importer.ImportAsync falls back to the
    // user's real ~/Music when handed no paths at all (see CLAUDE.md), so
    // leaving LibraryPaths unset would make startup scan - and these tests
    // depend on - whatever music happens to be on the machine.
    private readonly string _emptyLibrary =
        Path.Combine(Path.GetTempPath(), "flower-server-tests-lib-" + Guid.NewGuid());

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_dataDirectory);
        Directory.CreateDirectory(_emptyLibrary);

        builder.UseSetting("Flower:DataDirectory", _dataDirectory);
        builder.UseSetting("Flower:LibraryPaths:0", _emptyLibrary);
        builder.UseSetting("Flower:AdminUsername", "admin");
        builder.UseSetting("Flower:AdminPassword", "hunter2");
    }

    public async ValueTask InitializeAsync()
    {
        // Forces the host to build (and so the schema to be created and the
        // startup rescan to run) before any test seeds rows.
        await SendAsync("/rest/ping" + Auth());

        await SeedAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try { Directory.Delete(_dataDirectory, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_emptyLibrary, recursive: true); } catch { /* best effort */ }
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
        ];

        new TrackRepository(Services.GetRequiredService<FlowerDb>()).ReplaceAll(Seeded);
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

    // Classic Subsonic token auth - the only scheme the server accepts.
    public static string Auth(string password = "hunter2")
    {
        const string salt = "testsalt";
        var token = OpenSubsonicClient.ComputeToken(password, salt);
        return $"?u=admin&t={token}&s={salt}&f=json&v=1.16.1&c=tests";
    }

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
    private static string Auth() => SubsonicServerFixture.Auth();

    [Fact]
    public async Task Ping_succeeds_with_valid_credentials()
    {
        var response = await server.GetAsync("/rest/ping" + Auth());

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
        var response = await server.GetAsync("/rest/getAlbumList2" + SubsonicServerFixture.Auth("wrong"));

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
        var response = await server.GetAsync($"/rest/getAlbumList2{Auth()}&type={type}&size=10");

        Assert.Equal("ok", response.GetProperty("status").GetString());
        var albums = response.GetProperty("albumList2").GetProperty("album");
        Assert.Equal(3, albums.GetArrayLength());
    }

    [Fact]
    public async Task getAlbumList2_aggregates_song_count_and_duration_per_album()
    {
        var response = await server.GetAsync($"/rest/getAlbumList2{Auth()}&type=alphabeticalByName&size=10");

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
        var response = await server.GetAsync($"/rest/getAlbumList2{Auth()}&type=newest&size=10");

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
        var first = await server.GetAsync($"/rest/getAlbumList2{Auth()}&type=alphabeticalByName&size=2&offset=0");
        var second = await server.GetAsync($"/rest/getAlbumList2{Auth()}&type=alphabeticalByName&size=2&offset=2");

        Assert.Equal(2, first.GetProperty("albumList2").GetProperty("album").GetArrayLength());
        // Skip/Take now happen in SQL rather than over a fully materialized list.
        Assert.Equal(1, second.GetProperty("albumList2").GetProperty("album").GetArrayLength());
    }

    [Fact]
    public async Task getArtists_counts_distinct_albums_per_artist()
    {
        var response = await server.GetAsync("/rest/getArtists" + Auth());

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
        var response = await server.GetAsync($"/rest/search3{Auth()}&query=Love");

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
        var lower = await server.GetAsync($"/rest/search3{Auth()}&query=love");
        var upper = await server.GetAsync($"/rest/search3{Auth()}&query=LOVE");

        Assert.Equal(
            lower.GetProperty("searchResult3").GetProperty("song").GetArrayLength(),
            upper.GetProperty("searchResult3").GetProperty("song").GetArrayLength());
    }

    [Fact]
    public async Task search3_honours_its_result_limits()
    {
        var response = await server.GetAsync($"/rest/search3{Auth()}&query=Song&songCount=2");

        // The Take now happens in SQL, so this pins that it still applies at
        // all rather than returning everything that matched.
        Assert.Equal(2, response.GetProperty("searchResult3").GetProperty("song").GetArrayLength());
    }

    [Fact]
    public async Task getAlbum_returns_its_songs_in_disc_and_track_order()
    {
        var albumId = SubsonicIdentity.AlbumId("Aurora", "Alpha Album");

        var response = await server.GetAsync($"/rest/getAlbum{Auth()}&id={Uri.EscapeDataString(albumId)}");

        Assert.Equal(2, response.GetProperty("album").GetProperty("song").GetArrayLength());
    }

    [Fact]
    public async Task An_unknown_album_reports_not_found_rather_than_throwing()
    {
        var response = await server.GetAsync($"/rest/getAlbum{Auth()}&id=al-nope");

        Assert.Equal(70, response.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task A_missing_required_id_is_a_parameter_error()
    {
        var response = await server.GetAsync("/rest/getAlbum" + Auth());

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
        var (status, _) = await server.SendAsync("/rest/ping" + SubsonicServerFixture.Auth(), ip);

        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("203.0.113.9")]
    [InlineData("172.32.0.1")]  // just outside the 172.16/12 block
    public async Task A_public_client_is_refused_before_reaching_any_endpoint(string ip)
    {
        // 403 from the middleware, not a Subsonic error body - the request
        // never reaches the route table or the auth filter.
        var (status, body) = await server.SendAsync("/rest/ping" + SubsonicServerFixture.Auth(), ip);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Empty(body);
    }

    [Fact]
    public async Task The_guard_applies_to_unauthenticated_requests_too()
    {
        // Order matters: a public client must be cut off before it can even
        // probe which credentials the server accepts.
        var (status, _) = await server.SendAsync("/rest/getAlbumList2?f=json", "8.8.8.8");

        Assert.Equal(HttpStatusCode.Forbidden, status);
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
        var wrong = "/rest/ping" + SubsonicServerFixture.Auth("wrong");

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
        var wrong = "/rest/ping" + SubsonicServerFixture.Auth("wrong");

        for (var i = 0; i < 11; i++)
        {
            await server.SendAsync(wrong, ip);
        }

        // The lockout is on the source, not on the guess - otherwise finally
        // landing the right password would clear the penalty.
        var (status, _) = await server.SendAsync("/rest/ping" + SubsonicServerFixture.Auth(), ip);
        Assert.Equal(HttpStatusCode.TooManyRequests, status);
    }

    [Fact]
    public async Task One_source_being_locked_out_does_not_affect_another()
    {
        const string attacker = "10.20.30.42";
        const string bystander = "10.20.30.43";
        var wrong = "/rest/ping" + SubsonicServerFixture.Auth("wrong");

        for (var i = 0; i < 11; i++)
        {
            await server.SendAsync(wrong, attacker);
        }

        var (status, body) = await server.SendAsync("/rest/ping" + SubsonicServerFixture.Auth(), bystander);
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
            var (status, _) = await server.SendAsync("/rest/ping" + SubsonicServerFixture.Auth(), ip);
            Assert.Equal(HttpStatusCode.OK, status);
        }
    }
}

// AdminPassword guards /api/admin *and*, through SubsonicAuth, every /rest
// route, so booting on the shipped placeholder meant one well-known
// credential in front of the whole library (ARCHITECTURE-REVIEW Tier 3.1).
public class AdminPasswordStartupTests
{
    private sealed class Factory(string? password) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            var dataDirectory = Path.Combine(Path.GetTempPath(), "flower-server-pw-" + Guid.NewGuid());
            var emptyLibrary = Path.Combine(Path.GetTempPath(), "flower-server-pw-lib-" + Guid.NewGuid());
            Directory.CreateDirectory(dataDirectory);
            Directory.CreateDirectory(emptyLibrary);

            builder.UseSetting("Flower:DataDirectory", dataDirectory);
            builder.UseSetting("Flower:LibraryPaths:0", emptyLibrary);
            builder.UseSetting("Flower:AdminPassword", password ?? "");
        }
    }

    [Theory]
    [InlineData("changeme")]
    [InlineData("")]
    [InlineData("   ")]
    public void The_server_refuses_to_start_without_a_real_admin_password(string password)
    {
        using var factory = new Factory(password);

        var ex = Assert.Throws<InvalidOperationException>(() => factory.Services);
        Assert.Contains("AdminPassword", ex.Message);
    }

    [Fact]
    public void A_real_password_starts_normally()
    {
        using var factory = new Factory("a-real-password");

        Assert.NotNull(factory.Services);
    }
}

// The surface that moved from EF Core's change tracker to hand-written SQL and
// had no coverage at all before: playlist CRUD, star/unstar, scrobble and the
// stream/download lookup. Each of these used to be "load the entity, mutate a
// property, SaveChanges", where the mapping was EF's problem; they are now
// statements this project has to get right itself.
public class SubsonicWriteEndpointTests(SubsonicServerFixture server) : IClassFixture<SubsonicServerFixture>
{
    private static string Auth() => SubsonicServerFixture.Auth();

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
            $"/rest/createPlaylist{Auth()}&name=Road+Trip&songId={songs[0]}&songId={songs[1]}");

        var playlist = created.GetProperty("playlist");
        Assert.Equal("Road Trip", playlist.GetProperty("name").GetString());
        Assert.Equal(2, playlist.GetProperty("songCount").GetInt32());
        // Two 100s tracks - summed from the resolved tracks, not stored.
        Assert.Equal(200, playlist.GetProperty("duration").GetInt64());

        // Order is membership order, not the order rows happen to come back in.
        Assert.Equal(
            ["Alpha Song", "Beta Song"],
            playlist.GetProperty("entry").EnumerateArray().Select(e => e.GetProperty("title").GetString()));

        await server.GetAsync($"/rest/deletePlaylist{Auth()}&id={playlist.GetProperty("id").GetString()}");
    }

    [Fact]
    public async Task Updating_a_playlist_removes_by_index_and_appends_by_id()
    {
        var created = await server.GetAsync(
            $"/rest/createPlaylist{Auth()}&name=Edited&songId={SongId("Alpha Song")}&songId={SongId("Beta Song")}");
        var id = created.GetProperty("playlist").GetProperty("id").GetString();

        // Drop position 0 and append another - the two operations Subsonic
        // sends together, and the reason membership is rewritten wholesale
        // rather than diffed (removing an entry shifts every position after it).
        var updated = await server.GetAsync(
            $"/rest/updatePlaylist{Auth()}&playlistId={id}&songIndexToRemove=0&songIdToAdd={SongId("Love Song")}");
        Assert.Equal("ok", updated.GetProperty("status").GetString());

        var reread = await server.GetAsync($"/rest/getPlaylist{Auth()}&id={id}");
        Assert.Equal(
            ["Beta Song", "Love Song"],
            reread.GetProperty("playlist").GetProperty("entry")
                .EnumerateArray().Select(e => e.GetProperty("title").GetString()));

        await server.GetAsync($"/rest/deletePlaylist{Auth()}&id={id}");
    }

    [Fact]
    public async Task A_playlist_name_survives_an_update_that_does_not_mention_it()
    {
        var created = await server.GetAsync($"/rest/createPlaylist{Auth()}&name=Keep+My+Name");
        var id = created.GetProperty("playlist").GetProperty("id").GetString();

        // updatePlaylist sends only what changed, so an absent attribute must
        // leave the stored value alone rather than null it - which is what a
        // naive "UPDATE ... SET name = $name" would have done.
        await server.GetAsync($"/rest/updatePlaylist{Auth()}&playlistId={id}&comment=later");

        var reread = await server.GetAsync($"/rest/getPlaylist{Auth()}&id={id}");
        Assert.Equal("Keep My Name", reread.GetProperty("playlist").GetProperty("name").GetString());
        Assert.Equal("later", reread.GetProperty("playlist").GetProperty("comment").GetString());

        await server.GetAsync($"/rest/deletePlaylist{Auth()}&id={id}");
    }

    [Fact]
    public async Task A_deleted_playlist_is_gone_and_deleting_it_again_reports_not_found()
    {
        var created = await server.GetAsync($"/rest/createPlaylist{Auth()}&name=Temporary");
        var id = created.GetProperty("playlist").GetProperty("id").GetString();

        Assert.Equal("ok", (await server.GetAsync($"/rest/deletePlaylist{Auth()}&id={id}"))
            .GetProperty("status").GetString());

        var gone = await server.GetAsync($"/rest/getPlaylist{Auth()}&id={id}");
        Assert.Equal(70, gone.GetProperty("error").GetProperty("code").GetInt32());

        var again = await server.GetAsync($"/rest/deletePlaylist{Auth()}&id={id}");
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
            $"/rest/createPlaylist{Auth()}&name=Half+Missing"
            + $"&songId={SongId("Alpha Song")}&songId={Guid.NewGuid():N}");

        var playlist = created.GetProperty("playlist");
        Assert.Equal(1, playlist.GetProperty("songCount").GetInt32());

        await server.GetAsync($"/rest/deletePlaylist{Auth()}&id={playlist.GetProperty("id").GetString()}");
    }

    [Fact]
    public async Task Starring_an_album_stars_every_track_on_it_and_unstarring_clears_them()
    {
        var albumId = SubsonicIdentity.AlbumId("Aurora", "Alpha Album");

        await server.GetAsync($"/rest/star{Auth()}&albumId={Uri.EscapeDataString(albumId)}");
        var starred = await server.GetAsync($"/rest/getAlbum{Auth()}&id={Uri.EscapeDataString(albumId)}");
        Assert.All(
            starred.GetProperty("album").GetProperty("song").EnumerateArray(),
            song => Assert.True(song.GetProperty("starred").GetBoolean()));

        await server.GetAsync($"/rest/unstar{Auth()}&albumId={Uri.EscapeDataString(albumId)}");
        var cleared = await server.GetAsync($"/rest/getAlbum{Auth()}&id={Uri.EscapeDataString(albumId)}");
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

        await server.GetAsync($"/rest/star{Auth()}&artistId={Uri.EscapeDataString(artistId)}");

        var beta = await server.GetAsync(
            $"/rest/getAlbum{Auth()}&id={Uri.EscapeDataString(SubsonicIdentity.AlbumId("Aurora", "Beta Album"))}");
        Assert.True(beta.GetProperty("album").GetProperty("song")
            .EnumerateArray().Single().GetProperty("starred").GetBoolean());

        await server.GetAsync($"/rest/unstar{Auth()}&artistId={Uri.EscapeDataString(artistId)}");
    }

    [Fact]
    public async Task Starring_with_no_target_is_a_parameter_error()
    {
        var response = await server.GetAsync("/rest/star" + Auth());

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

        await server.GetAsync($"/rest/scrobble{Auth()}&id={id}");
        await server.GetAsync($"/rest/scrobble{Auth()}&id={id}");

        using (var connection = server.Db.Open())
        {
            // Incremented in SQL rather than read-modify-write, so two
            // scrobbles are two increments and neither reads a stale value.
            Assert.Equal(2, PlayCount(connection, id));
        }
    }

    [Fact]
    public async Task A_scrobble_with_submission_false_is_accepted_but_records_nothing()
    {
        var id = SongId("Beta Song");

        await server.GetAsync($"/rest/scrobble{Auth()}&id={id}&submission=false");

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
        var response = await server.GetAsync($"/rest/search3{Auth()}&query={Uri.EscapeDataString(query)}");

        Assert.Equal(0, response.GetProperty("searchResult3").GetProperty("song").GetArrayLength());
        Assert.Equal(0, response.GetProperty("searchResult3").GetProperty("album").GetArrayLength());
    }

    [Fact]
    public async Task Streaming_a_track_whose_file_is_missing_is_a_404_not_a_500()
    {
        // The seeded rows point at paths that do not exist, which is exactly
        // the state a library left behind by deleted files is in.
        var (status, _) = await server.SendAsync($"/rest/stream{Auth()}&id={SongId("Alpha Song")}");

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
        var response = await server.GetAsync($"/rest/getSong{Auth()}&id={id}");

        Assert.Contains(
            response.GetProperty("error").GetProperty("code").GetInt32(),
            (int[])[10, 70]);
    }
}
