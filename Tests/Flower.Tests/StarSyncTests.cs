using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Importer;
using Flower.Models;
using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;

namespace Flower.Tests;

// A star round-tripping through a real pull and push, since the bug was in
// neither half alone: the pull ignored the server's star on a track this
// device already had, and the push then compared this device's stale value
// against what the server had just served and reported the difference as a
// change - so an admin phone unstarred, for everyone, every track starred
// anywhere else. See Library.MergeStar and ServerStarBaselineStore.
//
// Pinned to an isolated PlatformDataDirectory: the baseline is a real file.
[Collection("PlatformDataDirectory")]
public class StarSyncTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string? _originalHome;
    private readonly string _tempHome;

    public StarSyncTests()
    {
        _originalHome = Environment.GetEnvironmentVariable("HOME");
        _tempHome = Path.Combine(Path.GetTempPath(), "flower-starsync-" + Guid.NewGuid());
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

    private const string LibraryPath = "/api/flower/v1/library";
    private const string TrackStatePath = "/api/flower/v1/track-state";

    // A server with one song, whose star the test flips between pulls, and
    // which records every track-state report sent to it.
    private sealed class OneSongServer : IDisposable
    {
        private readonly FakePeerHttpServer _http;
        private int _version;

        public bool Starred { get; set; }
        public List<TrackStateDto> Reports { get; } = new();

        public OneSongServer()
        {
            _http = new FakePeerHttpServer(async context =>
            {
                var path = context.Request.Url?.AbsolutePath;
                if (path == TrackStatePath)
                {
                    var body = await JsonSerializer.DeserializeAsync<TrackStateReportDto>(
                        context.Request.InputStream, JsonOptions);
                    lock (Reports)
                        Reports.AddRange(body!.Tracks);
                    context.Response.Close();
                    return;
                }

                if (path != LibraryPath)
                {
                    context.Response.Close();
                    return;
                }

                // A fresh token every time, so no pull is answered 304.
                context.Response.Headers["ETag"] = "v" + ++_version;
                var song = new TrackDto(
                    Id: "t1", Title: "Pirsomnia", Album: "Luminescent Creatures", Artist: "Ichiko Aoba",
                    AlbumId: null, ArtistId: null, Track: null, Year: null, Genre: null,
                    Size: null, ContentType: null, Suffix: null, Duration: 200, BitRate: null, CoverArt: null,
                    Starred: Starred);
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                    new LibrarySyncManifestDto("server", [song]), JsonOptions));
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            });
        }

        public DiscoveredDevice Device => new()
        {
            InstanceName = "server",
            BaseUri = NetworkDiscoveryService.HttpOrigin(new IPEndPoint(IPAddress.Loopback, _http.Port)),
            Fingerprint = "server",
            WeAreAdmin = true,
        };

        public void Dispose() => _http.Dispose();
    }

    private static LibrarySyncService MakeService(Library library, DeviceSigningKey key) =>
        new(library,
            new DeviceIdentity { Fingerprint = key.Fingerprint, Alias = "Phone" },
            key,
            new AppSettings(),
            new ServerStarBaselineStore(NullLogger<ServerStarBaselineStore>.Instance),
            TestLogArchive.InTempDirectory(),
            NullLogger<LibrarySyncService>.Instance,
            NullLogger<RemoteLibraryImporter>.Instance);

    [Fact]
    public async Task A_star_set_elsewhere_is_taken_by_the_next_pull_and_never_reported_back_as_an_unstar()
    {
        using var server = new OneSongServer();
        using var key = TestSigningKey.Create();
        var library = new Library([]);
        var service = MakeService(library, key);

        await service.SyncWithAsync(server.Device);
        server.Starred = true; // starred from the desktop
        await service.SyncWithAsync(server.Device);

        Assert.True(library.Tracks.Single().Starred);
        Assert.DoesNotContain(server.Reports, r => !r.Starred);
    }

    // The case the baseline is on disk for: starred with no server in reach,
    // then the app is killed. The next launch's first pull has to recognise
    // that star as this device's own and report it, not hand it to the server's
    // older answer.
    [Fact]
    public async Task A_star_made_offline_survives_a_restart_and_reaches_the_server()
    {
        using var server = new OneSongServer();
        using var key = TestSigningKey.Create();
        var library = new Library([]);

        await MakeService(library, key).SyncWithAsync(server.Device);
        var track = library.Tracks.Single();
        library.SetStarred(StarTarget.Song, track.Id.ToString(), true);

        await MakeService(library, key).SyncWithAsync(server.Device); // a fresh process

        Assert.True(library.Tracks.Single().Starred);
        Assert.Contains(server.Reports, r => r.TrackId == "t1" && r.Starred);
    }

    // And an unstar made elsewhere still reaches a device that starred the
    // track itself and has since told the server so.
    [Fact]
    public async Task An_unstar_made_elsewhere_after_this_device_starred_the_track_is_taken()
    {
        using var server = new OneSongServer();
        using var key = TestSigningKey.Create();
        var library = new Library([]);
        var service = MakeService(library, key);

        await service.SyncWithAsync(server.Device);
        library.SetStarred(StarTarget.Song, library.Tracks.Single().Id.ToString(), true);
        service.MarkTrackStateChanged(library.Tracks); // PeerSyncCoordinator's job in the app
        await service.PushTrackStateAsync(server.Device);
        Assert.Contains(server.Reports, r => r.Starred);
        server.Starred = true;  // the push landed
        server.Starred = false; // and then the desktop unstarred it
        server.Reports.Clear();

        await service.SyncWithAsync(server.Device);

        Assert.False(library.Tracks.Single().Starred);
        Assert.DoesNotContain(server.Reports, r => r.Starred);
    }
}
