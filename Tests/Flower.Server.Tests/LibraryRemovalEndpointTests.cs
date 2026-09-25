using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using Flower.Models;
using Flower.Persistence;
using Flower.Persistence.Sql;
using Flower.Services;

namespace Flower.Server.Tests;

// POST /api/admin/library/remove - an admin device's "Remove from Library"
// reaching the server. In a class of its own because it removes the fixture's
// seeded tracks, and a class fixture is shared by every test in its class.
public class LibraryRemovalEndpointTests(FlowerServerFixture server) : IClassFixture<FlowerServerFixture>
{
    private const string RemovePath = "/api/admin/library/remove";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task<DeviceSigningKey> DeviceAsync(bool isAdmin)
    {
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var q = ecdsa.ExportParameters(false).Q;
        var device = new DeviceSigningKey(ecdsa, [0x04, .. q.X!, .. q.Y!]);
        await server.Services.GetRequiredService<TrustedPeerStore>()
            .ApproveAsync(device.Fingerprint, isAdmin ? "Owner's Desktop" : "Guest Phone", device.PublicKeyBase64, isAdmin);
        return device;
    }

    private Task<(HttpStatusCode Status, string Body)> RemoveAsync(DeviceSigningKey device, IEnumerable<string> ids, bool deleteFiles) =>
        SendAsync(device, "POST", RemovePath, JsonSerializer.Serialize(new LibraryRemovalRequestDto(ids.ToList(), deleteFiles), Json));

    private Task<(HttpStatusCode Status, string Body)> SendAsync(DeviceSigningKey device, string method, string path, string? body) =>
        SendOnAsync(server, device, method, path, body);

    internal static async Task<(HttpStatusCode Status, string Body)> SendOnAsync(
        FlowerServerFixture server, DeviceSigningKey device, string method, string path, string? body)
    {
        var bodyBytes = body == null ? [] : Encoding.UTF8.GetBytes(body);
        var identity = new List<(string Key, string Value)>
        {
            ("X-Flower-Fingerprint", device.Fingerprint),
            ("X-Flower-PublicKey", device.PublicKeyBase64),
        };
        var (signature, timestamp, nonce) = device.Sign(method, path, identity, bodyBytes);

        var context = await server.Server.SendAsync(c =>
        {
            c.Request.Method = method;
            c.Request.Path = path;
            c.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.50");
            foreach (var (key, value) in identity)
                c.Request.Headers[key] = value;
            c.Request.Headers["X-Flower-Signature"] = signature;
            c.Request.Headers["X-Flower-Timestamp"] = timestamp;
            c.Request.Headers["X-Flower-Nonce"] = nonce;
            if (body != null)
            {
                c.Request.ContentType = "application/json";
                c.Request.Body = new MemoryStream(bodyBytes);
                c.Request.ContentLength = bodyBytes.Length;
            }
        });

        using var reader = new StreamReader(context.Response.Body);
        return ((HttpStatusCode)context.Response.StatusCode, await reader.ReadToEndAsync());
    }

    private Library Library => server.Services.GetRequiredService<Library>();

    private Track SeededTrack(string title) => Library.Tracks.Single(t => t.Title == title);

    [Fact]
    public async Task A_listener_cannot_remove_the_servers_songs()
    {
        using var guest = await DeviceAsync(isAdmin: false);
        var song = SeededTrack("Love Song");

        var (status, _) = await RemoveAsync(guest, [song.Id.ToKey()], deleteFiles: false);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Contains(Library.Tracks, t => t.Id == song.Id);
    }

    [Fact]
    public async Task An_admin_removes_a_song_from_the_catalog_and_its_playlists_and_a_rescan_leaves_it_out()
    {
        using var admin = await DeviceAsync(isAdmin: true);
        var song = SeededTrack("Beta Song");
        var other = SeededTrack("Alpha Song");
        Library.AddPlaylist(new Playlist("Mix", [other, song]));

        var (status, body) = await RemoveAsync(admin, [song.Id.ToKey()], deleteFiles: false);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, JsonSerializer.Deserialize<LibraryRemovalResponseDto>(body, Json)!.Removed);
        Assert.DoesNotContain(Library.Tracks, t => t.Id == song.Id);
        Assert.DoesNotContain(new TrackRepository(server.Db).LoadAll(), t => t.Id == song.Id);
        Assert.Equal(["Alpha Song"], Library.Playlists.Single(p => p.Name == "Mix").Tracks.Select(t => t.Title));

        // The file is still there as far as a scan is concerned.
        var rescanned = new Track { Path = song.Path, Title = song.Title, Artists = song.Artists, Album = song.Album, Duration = song.Duration };
        Library.UpdateTracks([.. Library.Tracks.Where(t => t.Path != null).Select(Copy), rescanned]);
        Assert.DoesNotContain(Library.Tracks, t => t.Path == song.Path);
        Assert.Contains(song.Path!, new TrackRepository(server.Db).LoadExcludedPaths().Select(e => e.Path));
    }

    [Fact]
    public async Task An_admin_asking_for_the_files_too_moves_them_to_the_servers_trash()
    {
        using var admin = await DeviceAsync(isAdmin: true);
        var song = SeededTrack("Second Song");
        var file = Path.Combine(Path.GetTempPath(), $"flower-remove-{Guid.NewGuid():N}.mp3");
        await File.WriteAllBytesAsync(file, [1, 2, 3]);
        song.Path = file;

        var (status, body) = await RemoveAsync(admin, [song.Id.ToKey()], deleteFiles: true);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, JsonSerializer.Deserialize<LibraryRemovalResponseDto>(body, Json)!.FilesTrashed);
        Assert.False(File.Exists(file));
        Assert.NotEmpty(Directory.EnumerateFiles(ServerTestTrash.Folder));
        Assert.DoesNotContain(new TrackRepository(server.Db).LoadExcludedPaths().Select(e => e.Path), p => p == file);
    }

    [Fact]
    public async Task An_id_the_server_does_not_have_is_not_an_error()
    {
        using var admin = await DeviceAsync(isAdmin: true);

        var (status, body) = await RemoveAsync(admin, [Guid.NewGuid().ToString("N")], deleteFiles: true);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, JsonSerializer.Deserialize<LibraryRemovalResponseDto>(body, Json)!.Removed);
    }

    [Fact]
    public async Task A_listener_cannot_see_or_restore_the_servers_removed_files()
    {
        using var guest = await DeviceAsync(isAdmin: false);

        var (listStatus, _) = await SendAsync(guest, "GET", "/api/admin/library/removed", null);
        var (restoreStatus, _) = await SendAsync(guest, "POST", "/api/admin/library/removed/restore",
            JsonSerializer.Serialize(new RestoreRemovedFilesRequestDto(["/m/a1.mp3"]), Json));

        Assert.Equal(HttpStatusCode.Forbidden, listStatus);
        Assert.Equal(HttpStatusCode.Forbidden, restoreStatus);
    }

    private static Track Copy(Track t) => new()
    {
        Path = t.Path, Title = t.Title, Artists = t.Artists, Album = t.Album, Duration = t.Duration,
    };
}

// Restore on the server's Removed Songs. A class of its own because Restore
// starts a real rescan, and the fixture's library folder is empty - so that
// rescan empties the library every other test in a shared fixture reads.
public class RemovedSongsEndpointTests(FlowerServerFixture server) : IClassFixture<FlowerServerFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task A_removed_file_is_listed_and_restoring_it_takes_it_off_the_list_and_rescans()
    {
        var library = server.Services.GetRequiredService<Library>();
        var rescans = server.Services.GetRequiredService<Flower.Server.Services.LibraryRescanCoordinator>();
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var q = ecdsa.ExportParameters(false).Q;
        using var admin = new DeviceSigningKey(ecdsa, [0x04, .. q.X!, .. q.Y!]);
        await server.Services.GetRequiredService<TrustedPeerStore>()
            .ApproveAsync(admin.Fingerprint, "Owner's Desktop", admin.PublicKeyBase64, isAdmin: true);
        var song = library.Tracks.Single(t => t.Title == "Love Song");
        LibraryRemoval.Remove(library, [song], deleteFiles: false, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var (listStatus, listBody) = await LibraryRemovalEndpointTests.SendOnAsync(server, admin, "GET", "/api/admin/library/removed", null);
        Assert.Equal(HttpStatusCode.OK, listStatus);
        var row = Assert.Single(JsonSerializer.Deserialize<List<RemovedFileDto>>(listBody, Json)!);
        Assert.Equal(song.Path, row.Path);
        Assert.False(row.StillOnDisk); // the fixture's paths are not real files

        var before = rescans.LastCompletedAt;
        var (restoreStatus, restoreBody) = await LibraryRemovalEndpointTests.SendOnAsync(server, admin, "POST",
            "/api/admin/library/removed/restore", JsonSerializer.Serialize(new RestoreRemovedFilesRequestDto([song.Path!]), Json));

        Assert.Equal(HttpStatusCode.OK, restoreStatus);
        Assert.Equal(1, JsonSerializer.Deserialize<RestoreRemovedFilesResponseDto>(restoreBody, Json)!.Restored);
        Assert.Empty(library.ExcludedPaths);
        Assert.Empty(new TrackRepository(server.Db).LoadExcludedPaths());

        // The rescan that brings a restored file back was started.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (rescans.LastCompletedAt == before && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.NotEqual(before, rescans.LastCompletedAt);
    }
}

