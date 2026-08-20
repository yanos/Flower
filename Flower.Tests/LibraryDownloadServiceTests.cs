using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Models;
using Flower.Persistence.Sql;
using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;

namespace Flower.Tests;

// LibraryDownloadService is the "mobile download button" (SYNC-PLAN.md Phase
// 3): pulls a placeholder track's real audio from whichever peer currently
// holds it. The two branches that need no I/O at all (AlreadyDownloaded,
// PeerUnavailable) are covered as pure logic; everything past that - a real
// download succeeding, a peer that's flat-out unreachable, and a peer that
// drops the connection mid-transfer (a network outage during download) -
// runs against a real local HTTP server (FakePeerHttpServer) rather than a
// mocked HttpClient, since PeerOpenSubsonicClientFactory.Create always builds
// its own real HttpClient pointed at the peer's endpoint with no seam to
// substitute a fake handler.
//
// Isolated from the real developer's library.json (PlatformDataDirectory,
// same pattern as StoreRoundTripTests) and from the real ~/Music folder
// (LibraryDownloadService.DownloadFolderOverride, since SpecialFolder.MyMusic
// doesn't respect a HOME override the way AppDataDirectory does).
[Collection("PlatformDataDirectory")]
public class LibraryDownloadServiceTests : IDisposable
{
    private readonly string _tempHome;
    private readonly string _downloadFolder;

    public LibraryDownloadServiceTests()
    {
        _tempHome = Path.Combine(Path.GetTempPath(), "flower-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempHome);
        PlatformDataDirectory.Current = _tempHome;

        _downloadFolder = Path.Combine(_tempHome, "Downloads");
        LibraryDownloadService.DownloadFolderOverride = _downloadFolder;
    }

    public void Dispose()
    {
        PlatformDataDirectory.Current = null;
        LibraryDownloadService.DownloadFolderOverride = null;
        try { Directory.Delete(_tempHome, recursive: true); } catch { /* best effort */ }
    }

    private static Track Placeholder(string? originDeviceFingerprint = "peer-fp", string? extension = "mp3", string? path = null, string? originTrackId = "peer-track-id") => new()
    {
        Title = "Come Together",
        Artists = "The Beatles",
        Album = "Abbey Road",
        Duration = TimeSpan.FromSeconds(259),
        Path = path,
        OriginDeviceFingerprint = originDeviceFingerprint,
        OriginTrackId = originTrackId,
        OriginFileExtension = extension,
    };

    private static DiscoveredDevice PeerAt(int port) => new()
    {
        InstanceName = "peer.local",
        EndPoint = new IPEndPoint(IPAddress.Loopback, port),
        Alias = "Peer",
    };

    private static DeviceSigningKey MakeSigningKey()
    {
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var q = ecdsa.ExportParameters(false).Q;
        var raw = new byte[65];
        raw[0] = 0x04;
        Buffer.BlockCopy(q.X!, 0, raw, 1, 32);
        Buffer.BlockCopy(q.Y!, 0, raw, 33, 32);
        return new DeviceSigningKey(ecdsa, raw);
    }

    private static LibraryDownloadService MakeService(Track track, out Library library)
    {
        // A real TrackRepository, because persistence is Library's own now
        // (see its ITrackStore) - the service no longer saves anything itself.
        library = new Library([track], NullLogger<Library>.Instance, new TrackRepository(FlowerDb.OpenDefault()));
        var identity = new DeviceIdentity { Fingerprint = "my-fp", Alias = "Me" };
        var signingKey = MakeSigningKey();
        var appSettings = new AppSettings();
        return new LibraryDownloadService(library, identity, signingKey, appSettings, NullLogger<LibraryDownloadService>.Instance);
    }

    [Fact]
    public async Task DownloadAsync_returns_AlreadyDownloaded_for_a_track_that_already_has_a_local_path()
    {
        var track = Placeholder(path: "/music/already-here.mp3");
        var service = MakeService(track, out _);

        var result = await service.DownloadAsync(track, PeerAt(1));

        Assert.Equal(TrackDownloadResult.AlreadyDownloaded, result);
    }

    [Fact]
    public async Task DownloadAsync_returns_PeerUnavailable_when_no_peer_is_given()
    {
        var track = Placeholder();
        var service = MakeService(track, out _);

        var result = await service.DownloadAsync(track, peer: null);

        Assert.Equal(TrackDownloadResult.PeerUnavailable, result);
    }

    [Fact]
    public async Task DownloadAsync_succeeds_against_a_real_peer_and_persists_the_local_path()
    {
        var audioBytes = Encoding.ASCII.GetBytes("RIFF-fake-wav-bytes-standing-in-for-real-audio");
        using var server = new FakePeerHttpServer(async ctx =>
        {
            ctx.Response.ContentType = "audio/x-wav";
            ctx.Response.ContentLength64 = audioBytes.Length;
            await ctx.Response.OutputStream.WriteAsync(audioBytes);
            ctx.Response.OutputStream.Close();
        });
        var track = Placeholder(extension: "wav");
        var service = MakeService(track, out var library);

        var result = await service.DownloadAsync(track, PeerAt(server.Port));

        Assert.Equal(TrackDownloadResult.Downloaded, result);
        Assert.NotNull(track.Path);
        Assert.StartsWith(_downloadFolder, track.Path);
        Assert.EndsWith(".wav", track.Path);
        Assert.Equal(audioBytes, await File.ReadAllBytesAsync(track.Path!, TestContext.Current.CancellationToken));

        var reloaded = await new LibraryStore(NullLogger<LibraryStore>.Instance).LoadAsync();
        Assert.Contains(reloaded, t => t.Path == track.Path);
        Assert.Same(track, Assert.Single(library.Tracks));
    }

    [Fact]
    public async Task DownloadAsync_returns_Failed_when_the_peer_is_unreachable()
    {
        var unboundPort = FakePeerHttpServer.GetUnboundPort();
        var track = Placeholder();
        var service = MakeService(track, out _);

        var result = await service.DownloadAsync(track, PeerAt(unboundPort));

        Assert.Equal(TrackDownloadResult.Failed, result);
        Assert.Null(track.Path);
    }

    // Simulates a network outage partway through the download: the peer
    // accepts the connection and starts responding, but the connection drops
    // before the declared Content-Length is fully sent - Response.Abort()
    // resets the connection outright rather than closing it cleanly, which
    // is what a real dropped Wi-Fi connection or a killed peer process looks
    // like at the socket level.
    [Fact]
    public async Task DownloadAsync_returns_Failed_when_the_connection_drops_mid_transfer()
    {
        var fullPayload = new byte[64 * 1024];
        Random.Shared.NextBytes(fullPayload);
        using var server = new FakePeerHttpServer(async ctx =>
        {
            ctx.Response.ContentLength64 = fullPayload.Length;
            await ctx.Response.OutputStream.WriteAsync(fullPayload.AsMemory(0, fullPayload.Length / 4));
            await ctx.Response.OutputStream.FlushAsync();
            ctx.Response.Abort();
        });
        var track = Placeholder();
        var service = MakeService(track, out _);

        var result = await service.DownloadAsync(track, PeerAt(server.Port));

        Assert.Equal(TrackDownloadResult.Failed, result);
        Assert.Null(track.Path);
    }

    [Fact]
    public async Task DeleteDownloadedFileAsync_reverts_a_downloaded_track_to_a_placeholder_and_removes_the_file()
    {
        var filePath = Path.Combine(_downloadFolder, "local.mp3");
        Directory.CreateDirectory(_downloadFolder);
        await File.WriteAllTextAsync(filePath, "audio", TestContext.Current.CancellationToken);
        var track = Placeholder(path: filePath);
        var service = MakeService(track, out _);

        await service.DeleteDownloadedFileAsync(track);

        Assert.Null(track.Path);
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public async Task DeleteDownloadedFileAsync_still_reverts_to_a_placeholder_if_the_file_is_already_gone()
    {
        var missingPath = Path.Combine(_downloadFolder, "already-gone.mp3");
        var track = Placeholder(path: missingPath);
        var service = MakeService(track, out _);

        await service.DeleteDownloadedFileAsync(track);

        Assert.Null(track.Path);
    }
}
