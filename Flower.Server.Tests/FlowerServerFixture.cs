using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Flower.Models;
using Flower.Persistence;
using Flower.Persistence.Sql;
using Flower.Server.Services;

namespace Flower.Server.Tests;

// Boots the real server in-process against a throwaway SQLite file and an
// empty library, then drives the real routes over HTTP.
//
// This is the harness ARCHITECTURE-REVIEW Tier 5.1 called for, and it is worth
// the setup cost rather than testing the query shapes through a seam: hand-
// written SQL is a string until something runs it, so a wrong column name or a
// GROUP BY that does not match the SELECT compiles perfectly and fails only
// when a real request reaches a real database.
//
// It was FlowerServerFixture, and lived in the OpenSubsonic adapter's own test
// file, because that surface was the first thing to need a real server. Nothing
// about it was ever specific to that adapter - which is why it outlived it (see
// docs/SYNC-PLAN.md on retiring /rest) and why most of this project's endpoint
// tests hang off it.
public sealed class FlowerServerFixture : WebApplicationFactory<Program>, IAsyncLifetime
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

    public async ValueTask InitializeAsync()
    {
        // Resolving from Services forces the host to build - and so the schema
        // to be created and the startup rescan to run - before any test seeds
        // rows.
        _ = Services;
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

    // The same request, reporting one response header instead of the body.
    // Exists for the rate-limit tests: what a 429 carries is the whole point
    // of the 429, and it has no body to read it out of.
    public async Task<(HttpStatusCode Status, string? Header)> SendWithHeaderAsync(
        string pathAndQuery, string remoteIp, string header)
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

        return ((HttpStatusCode)context.Response.StatusCode, context.Response.Headers[header].ToString());
    }
}
