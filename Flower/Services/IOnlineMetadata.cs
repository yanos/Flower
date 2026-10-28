using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Flower.Models;

namespace Flower.Services;

// What the internet knows about the library that the library does not carry -
// for now, a picture of each artist - asked of whichever host owns the songs
// (docs/ONLINE-METADATA-PLAN.md, "The rule that shapes it"). Views and view
// models see this and never which of the two implementations below answered.
//
// The answers are AlbumArtLoader's three: an image's bytes, empty for none to
// be had, and null for "could not find out just now".
public interface IOnlineMetadata
{
    // tracks are the artist's songs here, which say whose they are - and so
    // who to ask - and which albums tell the right artist from a namesake.
    Task<byte[]?> ArtistPictureAsync(string artist, IReadOnlyList<Track> tracks, CancellationToken ct = default);
}

// The engine in-process: a device on its own, and a paired device's own songs
// that its server does not have (a guest's own files).
public sealed class LocalOnlineMetadata(OnlineMetadataEngine engine) : IOnlineMetadata
{
    public Task<byte[]?> ArtistPictureAsync(string artist, IReadOnlyList<Track> tracks, CancellationToken ct = default) =>
        engine.ArtistPictureAsync(artist, AlbumsOf(tracks), ct);

    public static IReadOnlyList<string> AlbumsOf(IReadOnlyList<Track> tracks) =>
        tracks
            .Select(t => t.Album)
            .OfType<string>()
            .Where(album => album.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}

// The paired server, which runs the same engine for every device paired to it
// and answers at GET /api/flower/v1/artist-image - one lookup per library
// rather than one per device, and a listener's phone never tells Deezer what
// it is playing. On the art budget, like a cover, and cached on this device
// like one: a picture for good, a "none" for a day, so a phone scrolling its
// Artists tab does not ask the server about every row every time.
//
// No falling back to the internet when the server cannot be reached: one path
// per song keeps the owner's switch and the listener's privacy honest, and
// what was already seen is in the cache (the plan's first open question).
public sealed class ServerOnlineMetadata(
    HttpClient http, IPeerCredentials credentials, Func<Uri?> server, string cacheDirectory, bool closesConnection,
    ILogger<ServerOnlineMetadata> logger) : IOnlineMetadata
{
    public static readonly TimeSpan MissLifetime = TimeSpan.FromDays(1);

    private readonly ArtistPictureCache _cache = new(cacheDirectory, MissLifetime);

    public async Task<byte[]?> ArtistPictureAsync(string artist, IReadOnlyList<Track> tracks, CancellationToken ct = default)
    {
        var key = MetadataNames.Normalize(artist);
        if (key.Length == 0)
            return [];

        var (state, picture) = _cache.Read(key);
        if (state == ArtistPictureCache.State.Found)
            return picture;
        if (state == ArtistPictureCache.State.None)
            return [];

        if (server() is not { } origin)
            return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                new Uri(origin, $"/api/flower/v1/artist-image?artist={Uri.EscapeDataString(artist)}"));
            await request.AddPeerCredentialsAsync(credentials);
            if (closesConnection)
                request.Headers.ConnectionClose = true;

            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _cache.WriteNone(key);
                return [];
            }
            if (!response.IsSuccessStatusCode)
                return null;

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (LocalAlbumArtReader.MimeTypeForBytes(bytes) == null)
                return null;

            _cache.WriteFound(key, bytes);
            return bytes;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Debug, as for a cover: the server being out of reach is routine.
            logger.LogDebug(ex, "Could not ask the server for a picture of {Artist}", artist);
            return null;
        }
    }
}

// The choice between the two, made per artist from their songs: the server's
// when any of them is the server's, this device's own otherwise.
public sealed class RoutedOnlineMetadata(
    IOnlineMetadata local, IOnlineMetadata server, Func<string?> pairedServerFingerprint) : IOnlineMetadata
{
    public Task<byte[]?> ArtistPictureAsync(string artist, IReadOnlyList<Track> tracks, CancellationToken ct = default) =>
        (UsesServer(tracks, pairedServerFingerprint()) ? server : local).ArtistPictureAsync(artist, tracks, ct);

    // An artist the server has a song by is the server's to look up, even
    // when this device has files of theirs too: it is the server that holds
    // their albums, and its answer is the one every device paired to it gets.
    public static bool UsesServer(IReadOnlyList<Track> tracks, string? pairedServerFingerprint) =>
        !string.IsNullOrEmpty(pairedServerFingerprint)
        && tracks.Any(t => SyncRolePolicy.IsServersSong(t, pairedServerFingerprint));
}
