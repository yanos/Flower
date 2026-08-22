using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Importer;
using Flower.Models;
using Flower.Services;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests;

// The browser head's playlists, which are the origin server's and nothing else.
//
// The interesting part is not the fetch but the resolution: a playlist on the
// wire names its tracks by title/artist/album/duration rather than by any id or
// path, because a path means nothing on another device (see Track.SyncKey). So
// what arrives is only as good as the library it is matched against, and a
// track the origin has that this head does not has to drop out rather than
// become a row that cannot play.
public class OriginPlaylistImporterTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly HttpClient Http = new();

    private static OriginPlaylistImporter Importer(FakePeerHttpServer server, string? credentialHeader = null) =>
        new(Http, $"http://127.0.0.1:{server.Port}",
            credentialHeader == null ? new NoCredentials() : new HeaderCredentials(credentialHeader),
            NullLogger<OriginPlaylistImporter>.Instance);

    private static Track LocalTrack(string title) => new()
    {
        Path = $"/music/{title}.mp3",
        Title = title,
        Artists = "Remote Artist",
        Album = "Remote Album",
        Duration = TimeSpan.FromSeconds(180),
    };

    private static PlaylistSyncTrackDto Wire(string title) =>
        new(title, "Remote Artist", "Remote Album", 180);

    private static FakePeerHttpServer Server(
        List<PlaylistSyncPlaylistDto> playlists,
        List<string>? requestedPaths = null,
        List<string?>? seenCredentials = null) =>
        new(async context =>
        {
            requestedPaths?.Add(context.Request.Url!.AbsolutePath);
            seenCredentials?.Add(context.Request.Headers["X-Test-Credential"]);

            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                new PlaylistSyncManifestDto("server-fingerprint", playlists), JsonOptions));
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        });

    [Fact]
    public async Task It_reads_the_origins_playlists_and_resolves_them_against_this_library()
    {
        var paths = new List<string>();
        using var server = Server(
            [new PlaylistSyncPlaylistDto(Guid.NewGuid(), "Evening", DateTimeOffset.UtcNow,
                [Wire("One"), Wire("Two")])],
            requestedPaths: paths);

        var playlists = await Importer(server).ImportAsync([LocalTrack("One"), LocalTrack("Two")]);

        Assert.Equal(OriginPlaylistImporter.PlaylistsPath, Assert.Single(paths));
        var playlist = Assert.Single(playlists);
        Assert.Equal("Evening", playlist.Name);
        Assert.Equal(["One", "Two"], playlist.Tracks.Select(t => t.Title));
    }

    [Fact]
    public async Task A_track_this_head_does_not_have_drops_out_instead_of_becoming_an_unplayable_row()
    {
        // The same rule a peer sync follows (PlaylistSyncMapper.ResolveTracks).
        // Worth pinning here because the browser reaches it by a different road:
        // its library is the origin's own catalog, so the two normally agree
        // exactly, and a disagreement means a rescan caught the server mid-edit.
        using var server = Server(
            [new PlaylistSyncPlaylistDto(Guid.NewGuid(), "Evening", DateTimeOffset.UtcNow,
                [Wire("One"), Wire("Missing"), Wire("Two")])]);

        var playlists = await Importer(server).ImportAsync([LocalTrack("One"), LocalTrack("Two")]);

        Assert.Equal(["One", "Two"], Assert.Single(playlists).Tracks.Select(t => t.Title));
    }

    [Fact]
    public async Task It_identifies_itself_the_way_every_other_call_from_this_head_does()
    {
        // A tab's credential is its session token, and GET /playlists sits
        // behind the same gate as GET /library - so sending nothing here is a
        // 403 and an empty playlist sidebar.
        var credentials = new List<string?>();
        using var server = Server([], seenCredentials: credentials);

        await Importer(server, credentialHeader: "session-token").ImportAsync([]);

        Assert.Equal("session-token", Assert.Single(credentials));
    }

    [Fact]
    public async Task A_server_with_no_playlists_yields_none_rather_than_failing()
    {
        using var server = Server([]);

        Assert.Empty(await Importer(server).ImportAsync([LocalTrack("One")]));
    }

    private sealed class NoCredentials : IPeerCredentials
    {
        public IEnumerable<(string Key, string Value)> Authorize(
            string method, string absolutePath, IEnumerable<(string Key, string Value)> query, byte[] body) => [];
    }

    private sealed class HeaderCredentials(string value) : IPeerCredentials
    {
        public IEnumerable<(string Key, string Value)> Authorize(
            string method, string absolutePath, IEnumerable<(string Key, string Value)> query, byte[] body) =>
            [("X-Test-Credential", value)];
    }
}
