using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Flower.Models;
using Flower.Persistence;
using Flower.Server.Configuration;
using Flower.Services;

namespace Flower.Server.Tests;

// POST and PUT /api/admin/library/uploads - an owner's device handing this
// server a file, through the real host: the signature gate, the admin gate,
// and the body the gate buffered reaching LibraryIngest intact. What the ingest
// then does with a file is LibraryIngestTests' business; what these hold is
// who may call it, and that a file sent in pieces through this door is the
// file that lands.
//
// In a class of its own for the reason LibraryRemovalEndpointTests is: it adds
// to the fixture's library, and a class fixture is shared by its whole class.
public class LibraryUploadEndpointTests(FlowerServerFixture server) : IClassFixture<FlowerServerFixture>
{
    private const string UploadsPath = "/api/admin/library/uploads";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task<DeviceSigningKey> DeviceAsync(bool isAdmin)
    {
        var device = Unpaired();
        await server.Services.GetRequiredService<TrustedPeerStore>()
            .ApproveAsync(device.Fingerprint, isAdmin ? "Owner's Desktop" : "Guest Phone", device.PublicKeyBase64, isAdmin);
        return device;
    }

    private static DeviceSigningKey Unpaired()
    {
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var q = ecdsa.ExportParameters(false).Q;
        return new DeviceSigningKey(ecdsa, [0x04, .. q.X!, .. q.Y!]);
    }

    private Library Library => server.Services.GetRequiredService<Library>();

    private string MusicFolder =>
        server.Services.GetRequiredService<IOptions<FlowerServerOptions>>().Value.LibraryPaths[0];

    // A second of silence: a valid WAV, which is all the importer asks of one.
    private static byte[] Wav(byte marker)
    {
        const int sampleRate = 8000;
        var data = new byte[sampleRate * 2];
        Array.Fill(data, marker);

        var wav = new byte[44 + data.Length];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), 36 + data.Length);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(wav, 8);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), sampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), data.Length);
        data.CopyTo(wav, 44);
        return wav;
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(
        DeviceSigningKey device, string method, string path, (string Key, string Value)[] query, byte[] body, string contentType)
    {
        var identity = new List<(string Key, string Value)>
        {
            ("X-Flower-Fingerprint", device.Fingerprint),
            ("X-Flower-PublicKey", device.PublicKeyBase64),
        };
        var (signature, timestamp, nonce) = device.Sign(method, path, [.. identity, .. query], body);

        var context = await server.Server.SendAsync(c =>
        {
            c.Request.Method = method;
            c.Request.Path = path;
            c.Request.QueryString = QueryString.Create(query.Select(q => new KeyValuePair<string, string?>(q.Key, q.Value)));
            c.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.50");
            foreach (var (key, value) in identity)
                c.Request.Headers[key] = value;
            c.Request.Headers["X-Flower-Signature"] = signature;
            c.Request.Headers["X-Flower-Timestamp"] = timestamp;
            c.Request.Headers["X-Flower-Nonce"] = nonce;
            c.Request.ContentType = contentType;
            c.Request.Body = new MemoryStream(body);
            c.Request.ContentLength = body.Length;
        });

        using var reader = new StreamReader(context.Response.Body);
        return ((HttpStatusCode)context.Response.StatusCode, await reader.ReadToEndAsync());
    }

    // The server takes no uploads while it is scanning, and the fixture's
    // startup scan is a background task that may still be finishing.
    private async Task ScanFinishedAsync()
    {
        var rescans = server.Services.GetRequiredService<Flower.Server.Services.LibraryRescanCoordinator>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (rescans.IsRunning && DateTime.UtcNow < deadline)
            await Task.Delay(20, TestContext.Current.CancellationToken);
    }

    private async Task<(HttpStatusCode Status, string Body)> BeginAsync(DeviceSigningKey device, string relativePath, byte[] file)
    {
        await ScanFinishedAsync();
        return await SendAsync(device, "POST", UploadsPath, [],
            JsonSerializer.SerializeToUtf8Bytes(
                new LibraryUploadRequestDto(relativePath, file.Length, Convert.ToHexStringLower(SHA256.HashData(file)),
                    new DateTimeOffset(2019, 1, 1, 0, 0, 0, TimeSpan.Zero)), Json),
            "application/json");
    }

    private Task<(HttpStatusCode Status, string Body)> PieceAsync(DeviceSigningKey device, string uploadId, long offset, byte[] piece) =>
        SendAsync(device, "PUT", $"{UploadsPath}/{uploadId}", [("offset", offset.ToString())], piece, "application/octet-stream");

    [Fact]
    public async Task An_admin_device_can_send_a_file_in_pieces_and_it_lands_in_the_library()
    {
        using var admin = await DeviceAsync(isAdmin: true);
        var file = Wav(1);
        var half = file.Length / 2;

        var (beginStatus, beginBody) = await BeginAsync(admin, "Uploaded/Album/01 Song.wav", file);
        Assert.Equal(HttpStatusCode.OK, beginStatus);
        var begun = JsonSerializer.Deserialize<LibraryUploadStatusDto>(beginBody, Json)!;
        Assert.NotNull(begun.UploadId);
        Assert.Equal(0, begun.Offset);

        var (firstStatus, firstBody) = await PieceAsync(admin, begun.UploadId!, 0, file[..half]);
        Assert.Equal(HttpStatusCode.OK, firstStatus);
        Assert.Equal(half, JsonSerializer.Deserialize<LibraryUploadStatusDto>(firstBody, Json)!.Offset);

        var (lastStatus, lastBody) = await PieceAsync(admin, begun.UploadId!, half, file[half..]);
        Assert.Equal(HttpStatusCode.OK, lastStatus);
        var done = JsonSerializer.Deserialize<LibraryUploadStatusDto>(lastBody, Json)!;

        Assert.Null(done.UploadId);
        var landed = Path.Combine(MusicFolder, "Uploaded", "Album", "01 Song.wav");
        Assert.Equal(file, await File.ReadAllBytesAsync(landed, TestContext.Current.CancellationToken));

        var track = Library.Find(done.TrackId);
        Assert.NotNull(track);
        Assert.Equal(landed, track.Path);
        Assert.Equal(new DateTimeOffset(2019, 1, 1, 0, 0, 0, TimeSpan.Zero), track.DateAdded);
        Assert.Equal(Library.ChangeToken, done.LibraryToken);
    }

    // Handing a server files is an owner's act, like deleting them from it.
    [Fact]
    public async Task A_listener_cannot_upload()
    {
        using var guest = await DeviceAsync(isAdmin: false);

        var (status, _) = await BeginAsync(guest, "guest.wav", Wav(2));

        Assert.NotEqual(HttpStatusCode.OK, status);
        Assert.False(File.Exists(Path.Combine(MusicFolder, "guest.wav")));
    }

    [Fact]
    public async Task A_device_that_never_paired_cannot_upload()
    {
        using var stranger = Unpaired();

        var (status, _) = await BeginAsync(stranger, "stranger.wav", Wav(3));

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.False(File.Exists(Path.Combine(MusicFolder, "stranger.wav")));
    }

    [Fact]
    public async Task A_path_that_climbs_out_of_the_music_folder_is_refused()
    {
        using var admin = await DeviceAsync(isAdmin: true);

        var (status, body) = await BeginAsync(admin, "../escaped.wav", Wav(4));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        var problem = FlowerProblem.TryRead(body);
        Assert.Equal(ProblemCodes.InvalidRequest, problem?.Code);
        Assert.False(string.IsNullOrEmpty(problem?.Detail));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(MusicFolder)!, "escaped.wav")));
    }

    private async Task<LibraryUploadStatusDto> UploadAsync(DeviceSigningKey admin, string relativePath, byte[] file)
    {
        var (beginStatus, beginBody) = await BeginAsync(admin, relativePath, file);
        Assert.Equal(HttpStatusCode.OK, beginStatus);
        var begun = JsonSerializer.Deserialize<LibraryUploadStatusDto>(beginBody, Json)!;
        var (status, body) = await PieceAsync(admin, begun.UploadId!, 0, file);
        Assert.Equal(HttpStatusCode.OK, status);
        return JsonSerializer.Deserialize<LibraryUploadStatusDto>(body, Json)!;
    }

    private Task<(HttpStatusCode Status, string Body)> PostJsonAsync<T>(DeviceSigningKey device, string path, T body) =>
        SendAsync(device, "POST", path, [], JsonSerializer.SerializeToUtf8Bytes(body, Json), "application/json");

    // An owner's device renamed a file; the server's copy follows, and the
    // catalog serves it from where it now is.
    [Fact]
    public async Task An_admin_device_can_move_a_song_and_a_listener_cannot()
    {
        using var admin = await DeviceAsync(isAdmin: true);
        using var guest = await DeviceAsync(isAdmin: false);
        var uploaded = await UploadAsync(admin, "Moves/Old/song.wav", Wav(6));

        var (refused, _) = await PostJsonAsync(guest, "/api/admin/library/move",
            new LibraryMoveRequestDto(uploaded.TrackId!, "Moves/New/song.wav"));
        Assert.Equal(HttpStatusCode.Forbidden, refused);
        Assert.True(File.Exists(Path.Combine(MusicFolder, "Moves", "Old", "song.wav")));

        var (status, body) = await PostJsonAsync(admin, "/api/admin/library/move",
            new LibraryMoveRequestDto(uploaded.TrackId!, "Moves/New/song.wav"));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Moves/New/song.wav", JsonSerializer.Deserialize<LibraryMoveResponseDto>(body, Json)!.RelativePath);
        var landed = Path.Combine(MusicFolder, "Moves", "New", "song.wav");
        Assert.True(File.Exists(landed));
        Assert.False(Directory.Exists(Path.Combine(MusicFolder, "Moves", "Old")));
        Assert.Equal(landed, Library.Find(uploaded.TrackId)!.Path);
    }

    // Tags edited on an owner's device: written into this server's file,
    // dated, and served with the catalog so every other device takes them.
    [Fact]
    public async Task An_admin_device_can_edit_a_songs_tags_and_the_catalog_carries_the_edit()
    {
        using var admin = await DeviceAsync(isAdmin: true);
        using var guest = await DeviceAsync(isAdmin: false);
        var uploaded = await UploadAsync(admin, "Tags/song.wav", Wav(10));
        var editedAt = DateTimeOffset.UtcNow;
        var request = new LibraryTagEditsRequestDto(
            [new TrackTagEditDto(uploaded.TrackId!, editedAt, new TrackTagsDto(Title: "Edited", Artists: "Somebody", Comment: "a note"))]);

        var (refused, _) = await PostJsonAsync(guest, "/api/admin/library/tags", request);
        Assert.Equal(HttpStatusCode.Forbidden, refused);

        var (status, body) = await PostJsonAsync(admin, "/api/admin/library/tags", request);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, JsonSerializer.Deserialize<LibraryTagEditsResponseDto>(body, Json)!.Applied);
        var track = Library.Find(uploaded.TrackId)!;
        Assert.Equal("Edited", track.Title);
        Assert.Equal(editedAt, track.TagsEditedAt);

        // Still there after a scan reads the file again.
        using (var file = TagLib.File.Create(Path.Combine(MusicFolder, "Tags", "song.wav")))
            Assert.Equal("Edited", file.Tag.Title);

        var (_, catalog) = await SendAsync(guest, "GET", "/api/flower/v1/library", [], [], "application/json");
        using var manifest = JsonDocument.Parse(catalog);
        var song = manifest.RootElement.GetProperty("Songs").EnumerateArray()
            .Single(s => s.GetProperty("Id").GetString() == uploaded.TrackId);
        Assert.Equal("a note", song.GetProperty("Tags").GetProperty("Comment").GetString());
        Assert.Equal(editedAt, song.GetProperty("TagsEditedAt").GetDateTimeOffset());
    }

    // Artwork changed on an owner's device, for the songs it changed it on:
    // written into this server's files and dated, so every other device with
    // a copy fetches it. The picture is the body; the songs and the moment
    // are in the query, which the signature covers.
    [Fact]
    public async Task An_admin_device_can_change_a_songs_artwork_and_the_catalog_dates_it()
    {
        using var admin = await DeviceAsync(isAdmin: true);
        using var guest = await DeviceAsync(isAdmin: false);
        var uploaded = await UploadAsync(admin, "Art/song.wav", Wav(11));
        var editedAt = DateTimeOffset.UtcNow;
        (string, string)[] query = [("ids", uploaded.TrackId!), ("editedAt", editedAt.ToString("O"))];
        // The smallest thing that is recognisably a PNG.
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

        var (refused, _) = await SendAsync(guest, "PUT", "/api/admin/library/artwork", query, png, "image/png");
        Assert.Equal(HttpStatusCode.Forbidden, refused);

        var (status, body) = await SendAsync(admin, "PUT", "/api/admin/library/artwork", query, png, "image/png");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, JsonSerializer.Deserialize<LibraryArtEditResponseDto>(body, Json)!.Applied);
        Assert.Equal(png, LocalAlbumArtReader.EmbeddedIn(Path.Combine(MusicFolder, "Art", "song.wav"))?.Bytes);
        Assert.Equal(editedAt, Library.Find(uploaded.TrackId)!.ArtEditedAt);

        // An older change does not undo a newer one.
        (string, string)[] older = [("ids", uploaded.TrackId!), ("editedAt", editedAt.AddMinutes(-5).ToString("O"))];
        var (_, stale) = await SendAsync(admin, "DELETE", "/api/admin/library/artwork", older, [], "application/octet-stream");
        Assert.Equal(0, JsonSerializer.Deserialize<LibraryArtEditResponseDto>(stale, Json)!.Applied);
        Assert.NotNull(LocalAlbumArtReader.EmbeddedIn(Path.Combine(MusicFolder, "Art", "song.wav")));
    }

    // The library's clean-up: a song removed with its file kept, and then the
    // file deleted for good - and nothing that was not removed first.
    [Fact]
    public async Task Removed_files_can_be_deleted_for_good_and_nothing_else_can()
    {
        using var admin = await DeviceAsync(isAdmin: true);
        var gone = await UploadAsync(admin, "Cleanup/gone.wav", Wav(7));
        var kept = await UploadAsync(admin, "Cleanup/kept.wav", Wav(8));
        var gonePath = Path.Combine(MusicFolder, "Cleanup", "gone.wav");
        var keptPath = Path.Combine(MusicFolder, "Cleanup", "kept.wav");

        var (removeStatus, _) = await PostJsonAsync(admin, "/api/admin/library/remove",
            new LibraryRemovalRequestDto([gone.TrackId!], DeleteFiles: false));
        Assert.Equal(HttpStatusCode.OK, removeStatus);
        Assert.True(File.Exists(gonePath));

        var (status, body) = await PostJsonAsync(admin, "/api/admin/library/removed/delete",
            new DeleteRemovedFilesRequestDto([gonePath, keptPath]));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, JsonSerializer.Deserialize<DeleteRemovedFilesResponseDto>(body, Json)!.Deleted);
        Assert.False(File.Exists(gonePath));
        Assert.True(File.Exists(keptPath));
        Assert.NotNull(Library.Find(kept.TrackId));
        Assert.DoesNotContain(Library.ExcludedPaths, e => e.Path == gonePath);
    }

    // What a device is told so that its own copy goes too.
    [Fact]
    public async Task The_catalog_names_the_songs_that_were_removed_on_purpose()
    {
        using var admin = await DeviceAsync(isAdmin: true);
        var uploaded = await UploadAsync(admin, "Named/removed.wav", Wav(9));
        await PostJsonAsync(admin, "/api/admin/library/remove", new LibraryRemovalRequestDto([uploaded.TrackId!], DeleteFiles: false));

        var (status, body) = await SendAsync(admin, "GET", "/api/flower/v1/library", [], [], "application/json");

        Assert.Equal(HttpStatusCode.OK, status);
        using var manifest = JsonDocument.Parse(body);
        var removed = manifest.RootElement.GetProperty("Removed").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains(uploaded.TrackId, removed);
    }

    // A server part-way through a scan lists almost none of what is on its
    // disk, and a device uploads what is not listed.
    [Fact]
    public async Task Nothing_is_taken_while_the_server_is_scanning()
    {
        using var admin = await DeviceAsync(isAdmin: true);
        await ScanFinishedAsync();
        var rescans = server.Services.GetRequiredService<Flower.Server.Services.LibraryRescanCoordinator>();
        var file = Wav(5);

        // Held open by a scan that is told to wait on this test.
        var release = new TaskCompletionSource();
        var held = new TaskCompletionSource();
        Assert.True(rescans.TryStart(() =>
        {
            held.SetResult();
            release.Task.Wait(TimeSpan.FromSeconds(30));
        }));
        await held.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        (HttpStatusCode Status, string Body) refused;
        try
        {
            refused = await SendAsync(admin, "POST", UploadsPath, [],
                JsonSerializer.SerializeToUtf8Bytes(
                    new LibraryUploadRequestDto("during-scan.wav", file.Length, Convert.ToHexStringLower(SHA256.HashData(file)), DateTimeOffset.UtcNow), Json),
                "application/json");
        }
        finally
        {
            release.SetResult();
        }

        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.Status);
        Assert.False(File.Exists(Path.Combine(MusicFolder, "during-scan.wav")));
    }

    [Fact]
    public async Task A_piece_for_an_upload_nobody_began_is_told_to_begin_again()
    {
        using var admin = await DeviceAsync(isAdmin: true);
        await ScanFinishedAsync();

        var (status, _) = await PieceAsync(admin, new string('b', 64) + "-10", 0, new byte[10]);

        Assert.Equal(HttpStatusCode.NotFound, status);
    }
}
