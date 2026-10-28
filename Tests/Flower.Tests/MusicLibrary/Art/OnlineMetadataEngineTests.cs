using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Flower.Services;
using Flower.Tests.TestSupport;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Flower.Tests.MusicLibrary.Art;

// The lookup engine against a Deezer faked at the HttpMessageHandler: what it
// remembers, and what it does not. A picture found is kept, an artist with
// none is not asked about again for a month, and a lookup that failed is
// asked again next time - being refused is not the artist being unknown.
public sealed class OnlineMetadataEngineTests : IDisposable
{
    private readonly string _cache = Directory.CreateTempSubdirectory("flower-online-metadata-tests").FullName;
    private static readonly byte[] Picture = SyntheticPng.Build(8, 8);

    public OnlineMetadataEngineTests()
    {
        OnlineMetadataEngine.DeezerSpacing = TimeSpan.Zero;
    }

    public void Dispose() => TempDirectory.DeleteWhenReleased(_cache);

    private sealed class FakeDeezer : HttpMessageHandler
    {
        public string Search = """
            {"data":[{"id":399,"name":"Radiohead","picture_xl":"https://cdn.test/radiohead.jpg"}]}
            """;
        public string Albums = """{"data":[{"title":"OK Computer"},{"title":"Kid A"}]}""";
        public bool Refuse;
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            if (Refuse)
                return Task.FromResult(Json("""{"error":{"message":"Quota limit exceeded","code":4}}"""));

            var path = request.RequestUri!.AbsolutePath;
            if (path == "/search/artist")
                return Task.FromResult(Json(Search));
            if (path.EndsWith("/albums", StringComparison.Ordinal))
                return Task.FromResult(Json(Albums));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Picture) });
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private OnlineMetadataEngine Engine(FakeDeezer deezer, Func<bool>? enabled = null, TimeProvider? clock = null) =>
        new(new HttpClient(deezer), _cache, enabled ?? (() => true), NullLogger<OnlineMetadataEngine>.Instance, clock);

    private static readonly IReadOnlyCollection<string> TheirAlbums = ["OK Computer"];

    [Fact]
    public async Task A_picture_found_is_kept_and_not_looked_up_again()
    {
        var deezer = new FakeDeezer();

        Assert.Equal(Picture, await Engine(deezer).ArtistPictureAsync("Radiohead", TheirAlbums));
        var asked = deezer.Requests;

        // A new engine, as after a restart: the picture is on disk.
        Assert.Equal(Picture, await Engine(deezer).ArtistPictureAsync("radiohead", TheirAlbums));
        Assert.Equal(asked, deezer.Requests);
    }

    [Fact]
    public async Task An_artist_with_no_picture_is_not_asked_about_again_for_a_month()
    {
        var deezer = new FakeDeezer { Search = """{"data":[],"total":0}""" };
        var clock = new Clock();
        var engine = Engine(deezer, clock: clock);

        Assert.Empty((await engine.ArtistPictureAsync("Nobody Knows Them", []))!);
        Assert.Empty((await engine.ArtistPictureAsync("Nobody Knows Them", []))!);
        Assert.Equal(1, deezer.Requests);

        clock.Now += OnlineMetadataEngine.MissLifetime + TimeSpan.FromDays(1);
        await engine.ArtistPictureAsync("Nobody Knows Them", []);
        Assert.Equal(2, deezer.Requests);
    }

    [Fact]
    public async Task A_namesake_whose_albums_the_library_lacks_is_remembered_as_no_picture()
    {
        var deezer = new FakeDeezer { Albums = """{"data":[{"title":"Somebody Else's Record"}]}""" };
        var engine = Engine(deezer);

        Assert.Empty((await engine.ArtistPictureAsync("Radiohead", TheirAlbums))!);
        var asked = deezer.Requests;
        Assert.Empty((await engine.ArtistPictureAsync("Radiohead", TheirAlbums))!);
        Assert.Equal(asked, deezer.Requests);
    }

    [Fact]
    public async Task Being_refused_is_asked_again_rather_than_remembered()
    {
        var deezer = new FakeDeezer { Refuse = true };
        var engine = Engine(deezer);

        Assert.Null(await engine.ArtistPictureAsync("Radiohead", TheirAlbums));

        deezer.Refuse = false;
        Assert.Equal(Picture, await engine.ArtistPictureAsync("Radiohead", TheirAlbums));
    }

    [Fact]
    public async Task Switched_off_it_asks_nobody_but_still_has_what_it_found()
    {
        var deezer = new FakeDeezer();
        var on = true;
        var engine = Engine(deezer, () => on);
        await engine.ArtistPictureAsync("Radiohead", TheirAlbums);

        on = false;
        var asked = deezer.Requests;
        Assert.Equal(Picture, await engine.ArtistPictureAsync("Radiohead", TheirAlbums));
        Assert.Empty((await engine.ArtistPictureAsync("Somebody New", []))!);
        Assert.Equal(asked, deezer.Requests);
    }

    [Fact]
    public async Task Asked_twice_at_once_it_looks_up_once()
    {
        var deezer = new FakeDeezer();
        var engine = Engine(deezer);

        var both = await Task.WhenAll(
            engine.ArtistPictureAsync("Radiohead", TheirAlbums),
            engine.ArtistPictureAsync("Radiohead", TheirAlbums));

        Assert.All(both, picture => Assert.Equal(Picture, picture));
        // One search, one artist's albums, one picture.
        Assert.Equal(3, deezer.Requests);
    }
}
