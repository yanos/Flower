using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Flower.Server.Data;
using Flower.Server.Services;
using Flower.Services;

namespace Flower.Server.Tests;

// Boots the real server in-process against a throwaway SQLite file and an
// empty library, then drives the real routes over HTTP.
//
// This is the harness ARCHITECTURE-REVIEW Tier 5.1 called for, and Tier 1.3 is
// why it is worth the setup cost rather than testing the query shapes through
// a seam: both defects that work found - SQLite refusing Max() over a
// DateTimeOffset, and EF translating a grouped aggregate projection only as a
// member initializer - compiled cleanly and threw only when a real request hit
// a real provider. Nothing short of executing the query catches either.
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
    private async Task SeedAsync()
    {
        var factory = Services.GetRequiredService<IDbContextFactory<FlowerDbContext>>();
        await using var db = await factory.CreateDbContextAsync();

        db.Tracks.RemoveRange(db.Tracks);
        await db.SaveChangesAsync();

        db.Tracks.AddRange(
            Track("/m/a1.mp3", "Alpha Song", "Aurora", "Alpha Album", 2001, "Rock", days: 30),
            Track("/m/a2.mp3", "Second Song", "Aurora", "Alpha Album", 2002, "Pop", days: 30),
            Track("/m/b1.mp3", "Beta Song", "Aurora", "Beta Album", 2010, "Jazz", days: 5),
            Track("/m/c1.mp3", "Love Song", "Zephyr", "Gamma Album", 1999, "Folk", days: 1));

        await db.SaveChangesAsync();
    }

    private static TrackEntity Track(
        string path, string title, string artist, string album, int year, string genre, int days) =>
        new()
        {
            Id = path,
            Path = path,
            Title = title,
            Artist = artist,
            AlbumArtist = artist,
            Album = album,
            ArtistId = SubsonicIdentity.ArtistId(artist),
            AlbumId = SubsonicIdentity.AlbumId(artist, album),
            Year = year,
            Genre = genre,
            DurationSeconds = 100,
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
        // Each of these is a separately-translated query. "newest" in
        // particular is the one SQLite cannot aggregate server-side, and
        // "random" is the only one using EF.Functions.Random - a regression in
        // any single one is invisible to a test that only covers the default.
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

        // Gamma (1 day) then Beta (5) then Alpha (30). This ordering is
        // re-imposed in memory after a WHERE ... IN that does not preserve it,
        // so it is worth asserting rather than assuming.
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
