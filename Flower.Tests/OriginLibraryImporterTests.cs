using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Importer;
using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests;

// SYNC-PLAN.md seam 5's prerequisite: the browser has to find out who its server
// is before a catalog pulled from it is worth anything.
//
// Every placeholder carries the fingerprint of the device that holds the file
// (Track.OriginDeviceFingerprint), and everything downstream - streaming,
// downloading, cover art - reads it to know who to ask. A desktop client gets
// that fingerprint free with mDNS discovery. A browser tab has no discovery at
// all, so it reads the ungated /info handshake, and a catalog imported without
// it would be a full library of rows that can never play.
public class OriginLibraryImporterTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly HttpClient Http = new();

    private const string ServerFingerprint = "server-fingerprint";

    private static OriginLibraryImporter Importer(FakePeerHttpServer server) =>
        new(Http, $"http://127.0.0.1:{server.Port}", new NoCredentials(),
            NullLogger<RemoteLibraryImporter>.Instance, NullLogger<OriginLibraryImporter>.Instance);

    private static Child Song(string title) => new(
        Id: "sync:" + title,
        Title: title,
        Album: "Remote Album",
        Artist: "Remote Artist",
        AlbumId: null, ArtistId: null, Track: null, Year: null, Genre: null,
        Size: null, ContentType: null, Suffix: "flac", Duration: 180, BitRate: null,
        CoverArt: null);

    // Both routes a browser's library needs, on one host: the identity handshake
    // and the bulk manifest.
    private static FakePeerHttpServer Server(
        List<Child> songs, string? fingerprint = ServerFingerprint, List<string>? requested = null) =>
        new(async context =>
        {
            var path = context.Request.Url!.AbsolutePath;
            requested?.Add(path);

            object payload = path == SyncProtocol.InfoPath
                ? new SyncInfoResponseDto(
                    "Study Server", "2.0", null, "server", fingerprint!, "public-key",
                    IsServer: true, Download: false, TrustsCaller: null, LibraryToken: "token-1")
                : new LibrarySyncManifestDto(ServerFingerprint, songs);

            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, payload.GetType(), JsonOptions));
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        });

    [Fact]
    public async Task It_learns_who_the_server_is_and_addresses_every_track_back_to_it()
    {
        using var server = Server([Song("One"), Song("Two")]);

        var tracks = await Importer(server).ImportAsync();

        Assert.Equal(2, tracks.Count);
        // The whole point: without this the rows are unplayable.
        Assert.All(tracks, t => Assert.Equal(ServerFingerprint, t.OriginDeviceFingerprint));
        Assert.All(tracks, t => Assert.Null(t.Path));
        Assert.Equal(["One", "Two"], tracks.Select(t => t.Title));
    }

    [Fact]
    public async Task It_asks_who_the_server_is_once_and_not_once_per_rescan()
    {
        // A server does not change its keypair while a tab is open, and the
        // rescan this sits behind runs again on every library refresh.
        var requested = new List<string>();
        using var server = Server([Song("One")], requested: requested);
        var importer = Importer(server);

        await importer.ImportAsync();
        await importer.ImportAsync();

        Assert.Equal(1, requested.Count(p => p == SyncProtocol.InfoPath));
        Assert.Equal(2, requested.Count(p => p == RemoteLibraryImporter.LibraryPath));
    }

    [Fact]
    public async Task A_server_that_will_not_say_who_it_is_yields_no_library_rather_than_a_dead_one()
    {
        // Importing the catalog anyway would fill the library with rows that
        // look playable and are not. The startup rescan logs the failure and
        // carries on with an empty library, which is the honest outcome.
        using var server = Server([Song("One")], fingerprint: "");

        await Assert.ThrowsAsync<HttpRequestException>(() => Importer(server).ImportAsync());
    }

    [Fact]
    public async Task It_is_not_mistaken_for_a_scan_of_this_machine()
    {
        // What the startup rescan reads to decide whether the iTunes play-count
        // and date-added syncs mean anything - they read this machine's own
        // Music.app database, which has nothing to say about a server's catalog.
        using var server = Server([Song("One")]);

        Assert.False(Importer(server).ScansLocalFiles);
        // Through the interface, because true is the default IMusicImporter
        // answer rather than something the filesystem scanner states itself.
        Assert.True(((IMusicImporter)new Flower.Importer.Importer(NullLogger<Flower.Importer.Importer>.Instance))
            .ScansLocalFiles);
    }

    // The browser presents a signature; this test's server does not check
    // one, so the simplest honest stand-in is a credential that says nothing.
    private sealed class NoCredentials : IPeerCredentials
    {
        public Task<IReadOnlyList<(string Key, string Value)>> AuthorizeAsync(
            string method, string absolutePath, IEnumerable<(string Key, string Value)> query, byte[] body) =>
            Task.FromResult<IReadOnlyList<(string Key, string Value)>>([]);
    }
}
