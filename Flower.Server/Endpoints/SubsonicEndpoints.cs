using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Flower.Server.Configuration;
using Flower.Server.Data;
using Flower.Server.Services;
using Flower.Services;

namespace Flower.Server.Endpoints;

// The OpenSubsonic REST surface Flower.Core's OpenSubsonicClient actually
// calls (see SYNC-PLAN.md's client doc comment on OpenSubsonicClient for the
// exact list) - browsing, stream/download, playlist CRUD, star, scrobble,
// cover art. Only f=json is supported (Flower's own client, and every
// third-party client worth testing against, defaults to it); a real
// multi-client Subsonic server would also need XML - deliberately deferred,
// same "known v1 simplification" spirit as GET-only (no POST) routes below.
public static class SubsonicEndpoints
{
    public static void MapSubsonicEndpoints(this WebApplication app)
    {
        var rest = app.MapGroup("/rest").AddEndpointFilter(async (context, next) =>
        {
            var options = context.HttpContext.RequestServices.GetRequiredService<IOptions<FlowerServerOptions>>().Value;
            if (!SubsonicAuth.Validate(context.HttpContext.Request.Query, options))
                return SubsonicResults.Failed(40, "Wrong username or password.");
            return await next(context);
        });

        rest.MapGet("/ping", () => SubsonicResults.Ok());
        rest.MapGet("/ping.view", () => SubsonicResults.Ok());

        rest.MapGet("/getArtists", GetArtists);
        rest.MapGet("/getArtists.view", GetArtists);

        rest.MapGet("/getArtist", GetArtist);
        rest.MapGet("/getArtist.view", GetArtist);

        rest.MapGet("/getAlbum", GetAlbum);
        rest.MapGet("/getAlbum.view", GetAlbum);

        rest.MapGet("/getAlbumList2", GetAlbumList2);
        rest.MapGet("/getAlbumList2.view", GetAlbumList2);

        rest.MapGet("/getSong", GetSong);
        rest.MapGet("/getSong.view", GetSong);

        rest.MapGet("/search3", Search3);
        rest.MapGet("/search3.view", Search3);

        rest.MapGet("/getPlaylists", GetPlaylists);
        rest.MapGet("/getPlaylists.view", GetPlaylists);

        rest.MapGet("/getPlaylist", GetPlaylist);
        rest.MapGet("/getPlaylist.view", GetPlaylist);

        rest.MapGet("/createPlaylist", CreatePlaylist);
        rest.MapGet("/createPlaylist.view", CreatePlaylist);

        rest.MapGet("/updatePlaylist", UpdatePlaylist);
        rest.MapGet("/updatePlaylist.view", UpdatePlaylist);

        rest.MapGet("/deletePlaylist", DeletePlaylist);
        rest.MapGet("/deletePlaylist.view", DeletePlaylist);

        rest.MapGet("/star", (HttpRequest r, IDbContextFactory<FlowerDbContext> f) => SetStarred(true, r, f));
        rest.MapGet("/star.view", (HttpRequest r, IDbContextFactory<FlowerDbContext> f) => SetStarred(true, r, f));

        rest.MapGet("/unstar", (HttpRequest r, IDbContextFactory<FlowerDbContext> f) => SetStarred(false, r, f));
        rest.MapGet("/unstar.view", (HttpRequest r, IDbContextFactory<FlowerDbContext> f) => SetStarred(false, r, f));

        rest.MapGet("/scrobble", Scrobble);
        rest.MapGet("/scrobble.view", Scrobble);

        rest.MapGet("/stream", Stream);
        rest.MapGet("/stream.view", Stream);

        rest.MapGet("/download", Download);
        rest.MapGet("/download.view", Download);

        rest.MapGet("/getCoverArt", GetCoverArt);
        rest.MapGet("/getCoverArt.view", GetCoverArt);
    }

    private static async Task<IResult> GetArtists(IDbContextFactory<FlowerDbContext> dbFactory)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await db.Tracks.Select(t => new { t.ArtistId, t.AlbumArtist, t.AlbumId }).ToListAsync();

        var artists = rows
            .GroupBy(r => r.ArtistId)
            .Select(g => (Id: g.Key, Name: g.First().AlbumArtist ?? "Unknown Artist", AlbumCount: g.Select(x => x.AlbumId).Distinct().Count()))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var indices = artists
            .GroupBy(a => IndexLetter(a.Name))
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new IndexID3(g.Key, g.Select(a => SubsonicMapper.ToArtistId3(a.Id, a.Name, a.AlbumCount)).ToList()))
            .ToList();

        return SubsonicResults.Ok(artists: new ArtistsID3(indices));
    }

    private static string IndexLetter(string name)
    {
        var trimmed = name.TrimStart();
        var c = trimmed.Length > 0 ? char.ToUpperInvariant(trimmed[0]) : '#';
        return char.IsLetter(c) ? c.ToString() : "#";
    }

    private static async Task<IResult> GetArtist(string? id, IDbContextFactory<FlowerDbContext> dbFactory)
    {
        if (string.IsNullOrEmpty(id))
            return SubsonicResults.Failed(10, "Required parameter 'id' missing.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var tracks = await db.Tracks.Where(t => t.ArtistId == id).ToListAsync();
        if (tracks.Count == 0)
            return SubsonicResults.Failed(70, "Artist not found.");

        var albums = tracks.GroupBy(t => t.AlbumId)
            .Select(SubsonicMapper.ToAlbumId3)
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var artist = new ArtistWithAlbumsID3(id, tracks[0].AlbumArtist ?? "Unknown Artist", null, albums.Count, albums);
        return SubsonicResults.Ok(artist: artist);
    }

    private static async Task<IResult> GetAlbum(string? id, IDbContextFactory<FlowerDbContext> dbFactory)
    {
        if (string.IsNullOrEmpty(id))
            return SubsonicResults.Failed(10, "Required parameter 'id' missing.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var tracks = await db.Tracks.Where(t => t.AlbumId == id)
            .OrderBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber)
            .ToListAsync();
        if (tracks.Count == 0)
            return SubsonicResults.Failed(70, "Album not found.");

        var first = tracks[0];
        var album = new AlbumWithSongsID3(
            // id is already an "al-..." SubsonicIdentity.AlbumId value (see
            // GetCoverArt's own "al-" prefix check below) - not re-prefixed here.
            id, first.Album ?? "Unknown Album", first.AlbumArtist, first.ArtistId,
            id, tracks.Count, (long)tracks.Sum(t => t.DurationSeconds), first.Year, first.Genre,
            tracks.Select(SubsonicMapper.ToChild).ToList());

        return SubsonicResults.Ok(album: album);
    }

    private static async Task<IResult> GetAlbumList2(
        IDbContextFactory<FlowerDbContext> dbFactory, string type = "alphabeticalByName", int size = 500, int offset = 0)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var groups = (await db.Tracks.ToListAsync()).GroupBy(t => t.AlbumId);

        IEnumerable<IGrouping<string, TrackEntity>> ordered = type switch
        {
            "newest" => groups.OrderByDescending(g => g.Max(t => t.DateAdded)),
            "alphabeticalByArtist" => groups.OrderBy(g => g.First().AlbumArtist, StringComparer.OrdinalIgnoreCase),
            "random" => groups.OrderBy(_ => Random.Shared.Next()),
            _ => groups.OrderBy(g => g.First().Album, StringComparer.OrdinalIgnoreCase),
        };

        var page = ordered.Skip(offset).Take(size <= 0 ? 500 : size)
            .Select(SubsonicMapper.ToAlbumId3)
            .ToList();

        return SubsonicResults.Ok(albumList2: new AlbumList2(page));
    }

    private static async Task<IResult> GetSong(string? id, IDbContextFactory<FlowerDbContext> dbFactory)
    {
        if (string.IsNullOrEmpty(id))
            return SubsonicResults.Failed(10, "Required parameter 'id' missing.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var track = await db.Tracks.FindAsync(id);
        if (track is null)
            return SubsonicResults.Failed(70, "Song not found.");

        return SubsonicResults.Ok(song: SubsonicMapper.ToChild(track));
    }

    private static async Task<IResult> Search3(
        IDbContextFactory<FlowerDbContext> dbFactory,
        string query = "", int artistCount = 20, int albumCount = 20, int songCount = 20)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var matches = await db.Tracks
            .Where(t => (t.Title != null && t.Title.Contains(query))
                     || (t.Album != null && t.Album.Contains(query))
                     || (t.Artist != null && t.Artist.Contains(query)))
            .ToListAsync();

        var songs = matches
            .Where(t => t.Title != null && t.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(songCount)
            .Select(SubsonicMapper.ToChild)
            .ToList();

        var albums = matches
            .Where(t => t.Album != null && t.Album.Contains(query, StringComparison.OrdinalIgnoreCase))
            .GroupBy(t => t.AlbumId)
            .Take(albumCount)
            .Select(SubsonicMapper.ToAlbumId3)
            .ToList();

        var artists = matches
            .Where(t => t.AlbumArtist != null && t.AlbumArtist.Contains(query, StringComparison.OrdinalIgnoreCase))
            .GroupBy(t => t.ArtistId)
            .Take(artistCount)
            .Select(g => SubsonicMapper.ToArtistId3(g.Key, g.First().AlbumArtist ?? "Unknown Artist", g.Select(x => x.AlbumId).Distinct().Count()))
            .ToList();

        return SubsonicResults.Ok(searchResult3: new SearchResult3(artists, albums, songs));
    }

    private static async Task<IResult> GetPlaylists(IDbContextFactory<FlowerDbContext> dbFactory)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var playlists = await db.Playlists.Include(p => p.Tracks).ToListAsync();
        var trackIds = playlists.SelectMany(p => p.Tracks.Select(t => t.TrackId)).Distinct().ToList();
        var durations = await db.Tracks.Where(t => trackIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.DurationSeconds);

        var dtos = playlists.Select(p => new PlaylistDto(
            p.Id, p.Name, p.Comment, p.Tracks.Count,
            (long)p.Tracks.Sum(t => durations.GetValueOrDefault(t.TrackId, 0)),
            null, p.Public)).ToList();

        return SubsonicResults.Ok(playlists: new Flower.Services.Playlists(dtos));
    }

    private static async Task<IResult> GetPlaylist(string? id, IDbContextFactory<FlowerDbContext> dbFactory)
    {
        if (string.IsNullOrEmpty(id))
            return SubsonicResults.Failed(10, "Required parameter 'id' missing.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var playlist = await db.Playlists.Include(p => p.Tracks).FirstOrDefaultAsync(p => p.Id == id);
        if (playlist is null)
            return SubsonicResults.Failed(70, "Playlist not found.");

        var orderedIds = playlist.Tracks.OrderBy(t => t.Position).Select(t => t.TrackId).ToList();
        var trackById = await db.Tracks.Where(t => orderedIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id);
        var entries = orderedIds.Where(trackById.ContainsKey).Select(trackId => SubsonicMapper.ToChild(trackById[trackId])).ToList();

        var dto = new PlaylistWithSongsDto(
            playlist.Id, playlist.Name, playlist.Comment, entries.Count,
            (long)entries.Sum(e => e.Duration ?? 0), null, playlist.Public, entries);

        return SubsonicResults.Ok(playlist: dto);
    }

    private static async Task<IResult> CreatePlaylist(HttpRequest request, IDbContextFactory<FlowerDbContext> dbFactory)
    {
        var name = request.Query["name"].ToString();
        if (string.IsNullOrEmpty(name))
            return SubsonicResults.Failed(10, "Required parameter 'name' missing.");

        var songIds = request.Query["songId"].Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList();

        await using var db = await dbFactory.CreateDbContextAsync();
        var playlist = new PlaylistEntity { Id = Guid.NewGuid().ToString("N"), Name = name, CreatedAt = DateTimeOffset.UtcNow };
        for (var i = 0; i < songIds.Count; i++)
            playlist.Tracks.Add(new PlaylistTrackEntity { PlaylistId = playlist.Id, TrackId = songIds[i], Position = i });

        db.Playlists.Add(playlist);
        await db.SaveChangesAsync();

        return await GetPlaylist(playlist.Id, dbFactory);
    }

    private static async Task<IResult> UpdatePlaylist(HttpRequest request, IDbContextFactory<FlowerDbContext> dbFactory)
    {
        var playlistId = request.Query["playlistId"].ToString();
        if (string.IsNullOrEmpty(playlistId))
            return SubsonicResults.Failed(10, "Required parameter 'playlistId' missing.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var playlist = await db.Playlists.Include(p => p.Tracks).FirstOrDefaultAsync(p => p.Id == playlistId);
        if (playlist is null)
            return SubsonicResults.Failed(70, "Playlist not found.");

        if (request.Query.TryGetValue("name", out var name) && !string.IsNullOrEmpty(name))
            playlist.Name = name!;
        if (request.Query.TryGetValue("comment", out var comment))
            playlist.Comment = comment;
        if (request.Query.TryGetValue("public", out var isPublic))
            playlist.Public = string.Equals(isPublic, "true", StringComparison.OrdinalIgnoreCase);

        var ordered = playlist.Tracks.OrderBy(t => t.Position).Select(t => t.TrackId).ToList();

        var removeIndexes = request.Query["songIndexToRemove"]
            .Where(s => !string.IsNullOrEmpty(s)).Select(s => int.Parse(s!)).ToHashSet();
        if (removeIndexes.Count > 0)
            ordered = ordered.Where((_, i) => !removeIndexes.Contains(i)).ToList();

        var toAdd = request.Query["songIdToAdd"].Where(s => !string.IsNullOrEmpty(s)).Select(s => s!);
        ordered.AddRange(toAdd);

        db.PlaylistTracks.RemoveRange(playlist.Tracks);
        for (var i = 0; i < ordered.Count; i++)
            db.PlaylistTracks.Add(new PlaylistTrackEntity { PlaylistId = playlist.Id, TrackId = ordered[i], Position = i });

        await db.SaveChangesAsync();
        return SubsonicResults.Ok();
    }

    private static async Task<IResult> DeletePlaylist(string? id, IDbContextFactory<FlowerDbContext> dbFactory)
    {
        if (string.IsNullOrEmpty(id))
            return SubsonicResults.Failed(10, "Required parameter 'id' missing.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var playlist = await db.Playlists.FindAsync(id);
        if (playlist is null)
            return SubsonicResults.Failed(70, "Playlist not found.");

        db.Playlists.Remove(playlist);
        await db.SaveChangesAsync();
        return SubsonicResults.Ok();
    }

    private static async Task<IResult> SetStarred(bool starred, HttpRequest request, IDbContextFactory<FlowerDbContext> dbFactory)
    {
        var id = request.Query["id"].ToString();
        var albumId = request.Query["albumId"].ToString();
        var artistId = request.Query["artistId"].ToString();

        await using var db = await dbFactory.CreateDbContextAsync();
        IQueryable<TrackEntity> matches;
        if (!string.IsNullOrEmpty(id))
            matches = db.Tracks.Where(t => t.Id == id);
        else if (!string.IsNullOrEmpty(albumId))
            matches = db.Tracks.Where(t => t.AlbumId == albumId);
        else if (!string.IsNullOrEmpty(artistId))
            matches = db.Tracks.Where(t => t.ArtistId == artistId);
        else
            return SubsonicResults.Failed(10, "One of id/albumId/artistId is required.");

        var tracks = await matches.ToListAsync();
        foreach (var t in tracks)
        {
            t.Starred = starred;
            t.StarredAt = starred ? DateTimeOffset.UtcNow : null;
        }

        await db.SaveChangesAsync();
        return SubsonicResults.Ok();
    }

    private static async Task<IResult> Scrobble(string? id, IDbContextFactory<FlowerDbContext> dbFactory, bool submission = true)
    {
        if (string.IsNullOrEmpty(id))
            return SubsonicResults.Failed(10, "Required parameter 'id' missing.");

        if (submission)
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var track = await db.Tracks.FindAsync(id);
            if (track is not null)
            {
                track.PlayCount++;
                await db.SaveChangesAsync();
            }
        }

        return SubsonicResults.Ok();
    }

    private static async Task<IResult> Stream(string? id, IDbContextFactory<FlowerDbContext> dbFactory)
    {
        if (string.IsNullOrEmpty(id))
            return Results.NotFound();

        await using var db = await dbFactory.CreateDbContextAsync();
        var track = await db.Tracks.FindAsync(id);
        if (track is null || !File.Exists(track.Path))
            return Results.NotFound();

        return Results.File(track.Path, track.ContentType ?? "application/octet-stream", enableRangeProcessing: true);
    }

    private static async Task<IResult> Download(string? id, IDbContextFactory<FlowerDbContext> dbFactory)
    {
        if (string.IsNullOrEmpty(id))
            return Results.NotFound();

        await using var db = await dbFactory.CreateDbContextAsync();
        var track = await db.Tracks.FindAsync(id);
        if (track is null || !File.Exists(track.Path))
            return Results.NotFound();

        return Results.File(track.Path, track.ContentType ?? "application/octet-stream",
            fileDownloadName: Path.GetFileName(track.Path), enableRangeProcessing: true);
    }

    private static async Task<IResult> GetCoverArt(string? id, IDbContextFactory<FlowerDbContext> dbFactory)
    {
        if (string.IsNullOrEmpty(id))
            return Results.NotFound();

        await using var db = await dbFactory.CreateDbContextAsync();

        List<TrackEntity> candidates;
        if (id.StartsWith("al-", StringComparison.Ordinal))
        {
            var albumId = id["al-".Length..];
            candidates = await db.Tracks.Where(t => t.AlbumId == albumId)
                .OrderBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber)
                .ToListAsync();
        }
        else
        {
            var track = await db.Tracks.FindAsync(id);
            candidates = track is null ? [] : [track];
        }

        foreach (var candidate in candidates)
        {
            var art = TryGetLocalArtBytes(candidate.Path, out var mimeType);
            if (art is not null)
                return Results.Bytes(art, mimeType);
        }

        return Results.NotFound();
    }

    // Deliberately duplicated rather than shared: Flower's own equivalent
    // (AlbumArtLoader.TryGetLocalArtBytes) lives in the Flower project, which
    // is Avalonia-Bitmap-coupled and out of reach here (see SYNC-PLAN.md's
    // "Reuse boundary" note) - same embedded-tag-picture-then-cover/folder-file
    // fallback, just returning raw bytes+mime instead of decoding a Bitmap.
    private static byte[]? TryGetLocalArtBytes(string path, out string mimeType)
    {
        mimeType = "image/jpeg";

        try
        {
            using var tagFile = TagLib.File.Create(path);
            var pic = tagFile.Tag.Pictures.FirstOrDefault();
            if (pic?.Data?.Data is { Length: > 0 } data)
            {
                if (!string.IsNullOrEmpty(pic.MimeType))
                    mimeType = pic.MimeType;
                return data;
            }
        }
        catch
        {
            // Best effort - an unreadable/corrupt file just falls through to
            // the cover/folder-file check below.
        }

        try
        {
            var dir = Path.GetDirectoryName(path);
            if (dir != null)
            {
                var file = Directory.EnumerateFiles(dir).FirstOrDefault(f =>
                {
                    var stem = Path.GetFileNameWithoutExtension(f);
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    return (stem.Equals("cover", StringComparison.OrdinalIgnoreCase) || stem.Equals("folder", StringComparison.OrdinalIgnoreCase))
                        && (ext is ".jpg" or ".jpeg" or ".png");
                });
                if (file != null)
                {
                    mimeType = Path.GetExtension(file).ToLowerInvariant() == ".png" ? "image/png" : "image/jpeg";
                    return File.ReadAllBytes(file);
                }
            }
        }
        catch
        {
            // Best effort, same reasoning as above.
        }

        return null;
    }
}
