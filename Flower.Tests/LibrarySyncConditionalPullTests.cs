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
using Flower.Logging;
using Flower.Models;
using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;

namespace Flower.Tests;

// The client half of ARCHITECTURE-REVIEW Tier 1.4: LibrarySyncService must
// send back the ETag it last successfully merged, and treat a 304 as "already
// up to date" rather than as an empty catalog - merging an empty manifest
// would prune every placeholder the peer ever taught this device about, so
// getting this wrong is data loss, not a missed optimization.
//
// Pinned to an isolated PlatformDataDirectory (see StoreRoundTripTests'
// comment): LibraryStore.SaveAsync writes a real library.json.
[Collection("PlatformDataDirectory")]
public class LibrarySyncConditionalPullTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string? _originalHome;
    private readonly string _tempHome;

    public LibrarySyncConditionalPullTests()
    {
        _originalHome = Environment.GetEnvironmentVariable("HOME");
        _tempHome = Path.Combine(Path.GetTempPath(), "flower-condpull-" + Guid.NewGuid());
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

    private static LibrarySyncService MakeService(Library library, DeviceSigningKey key) =>
        new(library,
            new DeviceIdentity { Fingerprint = key.Fingerprint, Alias = "Client" },
            key,
            new AppSettings { IsServer = false },
            InMemoryLogStore.Instance,
            NullLogger<LibrarySyncService>.Instance,
            NullLogger<RemoteLibraryImporter>.Instance);

    private static Child RemoteSong(string title) => new(
        Id: "sync:" + title,
        Title: title,
        Album: "Remote Album",
        Artist: "Remote Artist",
        AlbumId: null, ArtistId: null, Track: null, Year: null, Genre: null,
        Size: null, ContentType: null, Suffix: null, Duration: 180, BitRate: null,
        CoverArt: null);

    [Fact]
    public async Task The_first_pull_sends_no_condition_and_the_second_sends_the_token_it_was_served()
    {
        var conditions = new List<string?>();
        const string token = "abc12345-7";
        using var peer = new FakePeerHttpServer(async context =>
        {
            conditions.Add(context.Request.Headers["If-None-Match"]);
            context.Response.Headers["ETag"] = token;
            var manifest = new LibrarySyncManifestDto("peer-fingerprint", [RemoteSong("Remote One")]);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, JsonOptions));
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        });

        using var key = TestSigningKey.Create();
        var library = new Library([]);
        var service = MakeService(library, key);
        var device = new DiscoveredDevice
        {
            InstanceName = "peer",
            EndPoint = new IPEndPoint(IPAddress.Loopback, peer.Port),
            Fingerprint = "peer-fingerprint",
        };

        var first = await service.SyncWithAsync(device);
        var second = await service.SyncWithAsync(device);

        Assert.True(first.Success);
        Assert.Equal(1, first.AddedCount);
        Assert.True(second.Success);
        Assert.Equal([null, token], conditions);
    }

    [Fact]
    public async Task A_304_is_a_successful_no_op_that_leaves_every_placeholder_alone()
    {
        const string token = "abc12345-7";
        var served = 0;
        using var peer = new FakePeerHttpServer(async context =>
        {
            // Serve the catalog once, then answer 304 to the conditional
            // request the service is expected to make next.
            if (context.Request.Headers["If-None-Match"] == token)
            {
                context.Response.StatusCode = 304;
                context.Response.Close();
                return;
            }

            served++;
            context.Response.Headers["ETag"] = token;
            var manifest = new LibrarySyncManifestDto("peer-fingerprint", [RemoteSong("Remote One"), RemoteSong("Remote Two")]);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, JsonOptions));
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        });

        using var key = TestSigningKey.Create();
        var library = new Library([]);
        var service = MakeService(library, key);
        var device = new DiscoveredDevice
        {
            InstanceName = "peer",
            EndPoint = new IPEndPoint(IPAddress.Loopback, peer.Port),
            Fingerprint = "peer-fingerprint",
        };

        await service.SyncWithAsync(device);
        Assert.Equal(2, library.Tracks.Count);

        var result = await service.SyncWithAsync(device);

        Assert.True(result.Success);
        Assert.True(result.Unchanged);
        Assert.Equal(0, result.FetchedCount);
        Assert.Equal(0, result.AddedCount);
        // The pruning path in MergeSyncedTracks must not have run - a 304 is
        // "identical to what you already have", not "I have nothing".
        Assert.Equal(2, library.Tracks.Count);
        Assert.Equal(1, served);
    }

    [Fact]
    public async Task A_failed_pull_does_not_poison_the_next_one_with_a_token_it_never_merged()
    {
        var conditions = new List<string?>();
        var requestCount = 0;
        using var peer = new FakePeerHttpServer(context =>
        {
            conditions.Add(context.Request.Headers["If-None-Match"]);
            // Serves an ETag alongside a response the service can't merge, so
            // remembering the token here would mean the next sync gets 304'd
            // for a catalog this device never actually stored.
            context.Response.Headers["ETag"] = "abc12345-9";
            context.Response.StatusCode = ++requestCount == 1 ? 500 : 200;
            context.Response.Close();
            return Task.CompletedTask;
        });

        using var key = TestSigningKey.Create();
        var service = MakeService(new Library([]), key);
        var device = new DiscoveredDevice
        {
            InstanceName = "peer",
            EndPoint = new IPEndPoint(IPAddress.Loopback, peer.Port),
            Fingerprint = "peer-fingerprint",
        };

        var failed = await service.SyncWithAsync(device);
        await service.SyncWithAsync(device);

        Assert.False(failed.Success);
        Assert.Equal([null, null], conditions);
    }
}
