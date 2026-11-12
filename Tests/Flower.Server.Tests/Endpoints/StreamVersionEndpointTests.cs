using System.Net;
using System.Security.Cryptography;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using Flower.Models;
using Flower.Persistence;
using Flower.Services;

using Flower.Server.Tests.TestSupport;

namespace Flower.Server.Tests.Endpoints;

// The stream route's version of a file (MediaEndpoints.VersionOf), which a
// client picking a song up partway through sends back in If-Range. A tag edit
// replaces the file while it may be streaming (TagFileSave) and moves its
// audio, so a reopen must get either the rest of the file it was reading or
// the whole of the new one, never a range of the new one taken for the old.
// In a class of its own because it gives the fixture's library a song with a
// real file, and a class fixture is shared by every test in its class.
public sealed class StreamVersionEndpointTests(FlowerServerFixture server) : IClassFixture<FlowerServerFixture>, IDisposable
{
    private const string StreamPath = "/api/flower/v1/stream";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "flower-stream-version-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    private Library Library => server.Services.GetRequiredService<Library>();

    private Track Song(byte[] bytes)
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "01 1969.mp3");
        File.WriteAllBytes(path, bytes);

        var song = new Track { Path = path, Title = "1969", Album = "Geogaddi " + Guid.NewGuid().ToString("N"), Artists = "Boards of Canada" };
        Library.UpdateTracks([.. Library.Tracks.Where(t => t.Path != null), song]);
        return Library.Tracks.Single(t => t.Path == path);
    }

    private async Task<DeviceSigningKey> DeviceAsync()
    {
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var q = ecdsa.ExportParameters(false).Q;
        var device = new DeviceSigningKey(ecdsa, [0x04, .. q.X!, .. q.Y!]);
        await server.Services.GetRequiredService<TrustedPeerStore>()
            .ApproveAsync(device.Fingerprint, "Kitchen iPad", device.PublicKeyBase64, isAdmin: false);
        return device;
    }

    private async Task<HttpContext> GetAsync(DeviceSigningKey device, Track song, string range, string? ifRange = null)
    {
        (string Key, string Value)[] query = [("id", song.Id.ToKey())];
        var (signature, timestamp, nonce) = device.Sign("GET", StreamPath, query, []);

        return await server.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = StreamPath;
            c.Request.QueryString = QueryString.Create(query.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)));
            c.Connection.RemoteIpAddress = IPAddress.Parse("10.0.3.1");
            c.Request.Headers["X-Flower-Fingerprint"] = device.Fingerprint;
            c.Request.Headers["X-Flower-Alias"] = "Kitchen iPad";
            c.Request.Headers["X-Flower-Role"] = "client";
            c.Request.Headers["X-Flower-Signature"] = signature;
            c.Request.Headers["X-Flower-Timestamp"] = timestamp;
            c.Request.Headers["X-Flower-Nonce"] = nonce;
            c.Request.Headers.Range = range;
            if (ifRange != null)
                c.Request.Headers.IfRange = ifRange;
        });
    }

    [Fact]
    public async Task A_reopen_of_the_same_file_gets_the_range_it_asked_for()
    {
        var song = Song([.. Enumerable.Range(0, 1000).Select(i => (byte)i)]);
        using var device = await DeviceAsync();
        var version = (await GetAsync(device, song, "bytes=0-0")).Response.Headers.ETag.ToString();

        var reopened = await GetAsync(device, song, "bytes=500-", version);

        Assert.Equal(StatusCodes.Status206PartialContent, reopened.Response.StatusCode);
    }

    [Fact]
    public async Task A_reopen_of_a_file_replaced_since_gets_the_whole_of_the_new_one()
    {
        var song = Song([.. Enumerable.Range(0, 1000).Select(i => (byte)i)]);
        using var device = await DeviceAsync();
        var version = (await GetAsync(device, song, "bytes=0-0")).Response.Headers.ETag.ToString();
        File.WriteAllBytes(song.Path!, [.. Enumerable.Range(0, 1500).Select(i => (byte)(i * 7))]);

        var reopened = await GetAsync(device, song, "bytes=500-", version);

        Assert.Equal(StatusCodes.Status200OK, reopened.Response.StatusCode);
        Assert.Equal(1500, reopened.Response.ContentLength);
        Assert.NotEqual(version, reopened.Response.Headers.ETag.ToString());
    }

    // The same length, and within the same second: the date alone would not
    // tell these two apart.
    [Fact]
    public async Task A_file_rewritten_to_the_same_length_within_the_second_is_still_a_new_version()
    {
        var song = Song(new byte[1000]);
        using var device = await DeviceAsync();
        var written = File.GetLastWriteTimeUtc(song.Path!);
        var version = (await GetAsync(device, song, "bytes=0-0")).Response.Headers.ETag.ToString();
        File.WriteAllBytes(song.Path!, Enumerable.Repeat((byte)1, 1000).ToArray());
        File.SetLastWriteTimeUtc(song.Path!, written.AddMilliseconds(1));

        var reopened = await GetAsync(device, song, "bytes=500-", version);

        Assert.Equal(StatusCodes.Status200OK, reopened.Response.StatusCode);
    }
}
