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
    // Classic Subsonic auth is t=md5(password+salt) with no expiry and no
    // nonce, so a captured u/t/s query string replays forever and a wrong one
    // costs an attacker nothing to retry - this surface had no rate limiting
    // at all, unlike AdminEndpoints and PairingEndpoints. Two budgets, both
    // keyed by source IP (there is no pre-auth identity worth keying by):
    //
    // - FailedAuthLimiter is charged only when auth actually fails, and is
    //   peeked before anything else, so a source that burns it is locked out
    //   of /rest entirely until it drains. That is what bounds password
    //   guessing against the shipped-default-credentials case.
    // - RequestLimiter is charged on every request and is sized for real
    //   client behaviour instead: an album grid pulls one getCoverArt per
    //   tile, which is bursty enough that SyncHttpServer's 120/60s browse
    //   ceiling would be too tight here.
    private static readonly RateLimiter FailedAuthLimiter = new(max: 10, TimeSpan.FromSeconds(60));
    private static readonly RateLimiter RequestLimiter = new(max: 600, TimeSpan.FromSeconds(60));

    public static void MapSubsonicEndpoints(this WebApplication app)
    {
        var rest = app.MapGroup("/rest").AddEndpointFilter(async (context, next) =>
        {
            var options = context.HttpContext.RequestServices.GetRequiredService<IOptions<FlowerServerOptions>>().Value;
            var key = context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var now = DateTimeOffset.UtcNow;

            if (!FailedAuthLimiter.WouldAllow(key, now) || !RequestLimiter.TryAcquire(key, now))
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);

            if (!SubsonicAuth.Validate(context.HttpContext.Request.Query, options))
            {
                FailedAuthLimiter.TryAcquire(key, now);
                return SubsonicResults.Failed(40, "Wrong username or password.");
            }

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
        // One row per distinct (artist, album) pair, grouped SQL-side, instead
        // of a projection of every track in the library. The album count this
        // needs is a count of distinct albums per artist, so collapsing the
        // duplicates in SQL first means materializing roughly one row per album
        // (~1.4k at the target scale) rather than one per track (~16k), and the
        // AlbumId index actually gets used. Counting the pairs per artist is
        // then trivial in memory. See ARCHITECTURE-REVIEW Tier 1.3.
        var pairs = await db.Tracks
            .GroupBy(t => new { t.ArtistId, t.AlbumId })
            .Select(g => new { g.Key.ArtistId, g.Key.AlbumId, Name = g.Min(t => t.AlbumArtist) })
            .ToListAsync();

        var artists = pairs
            .GroupBy(r => r.ArtistId)
            .Select(g => (Id: g.Key, Name: g.Min(x => x.Name) ?? "Unknown Artist", AlbumCount: g.Count()))
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

        // Grouped, ordered and paginated in SQL. This used to be
        // db.Tracks.ToListAsync() - the whole table materialized into memory,
        // grouped, sorted and then paged, on every browse request, against the
        // 16k-track library SYNC-PLAN.md names as the target scale, with no
        // caching. Returning one page of albums now touches one page of rows.
        //
        // The per-album scalars come from Min() rather than "whichever row came
        // back first". For a well-formed album every track carries the same
        // Album/AlbumArtist/ArtistId anyway, so the value is identical; where
        // tracks genuinely disagree (a per-track Genre or Year on a
        // compilation), Min is at least deterministic, which First() over an
        // unordered SQL result never was. See ARCHITECTURE-REVIEW Tier 1.3.
        var albums = AlbumSummaries(db.Tracks);
        var take = size <= 0 ? 500 : size;

        // "newest" is the one sort that can't be done in SQL here: it orders by
        // each album's most recent DateAdded, and the SQLite provider refuses
        // Max() over a DateTimeOffset (it is stored as TEXT, with no value
        // converter on TrackEntity.DateAdded - adding one would be a schema
        // change, which Tier 4.1's SQLite work is the right place for). So this
        // path aggregates client-side, but over a two-column projection rather
        // than whole entities: still one row per track, but a tiny one, and no
        // entity materialization or change tracking. Verified against the real
        // 16k-track library.
        List<SubsonicMapper.AlbumSummary> page;
        if (type == "newest")
        {
            var newestIds = (await db.Tracks
                    .Select(t => new { t.AlbumId, t.DateAdded })
                    .ToListAsync())
                .GroupBy(t => t.AlbumId)
                .OrderByDescending(g => g.Max(t => t.DateAdded))
                .Skip(offset)
                .Take(take)
                .Select(g => g.Key)
                .ToList();

            var byId = (await albums.Where(a => newestIds.Contains(a.AlbumId)).ToListAsync())
                .ToDictionary(a => a.AlbumId);

            // Re-imposes the recency order the WHERE ... IN above doesn't preserve.
            page = newestIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        }
        else
        {
            var ordered = type switch
            {
                "alphabeticalByArtist" => albums.OrderBy(a => a.AlbumArtist),
                // ORDER BY RANDOM() server-side - Random.Shared.Next() as a sort
                // key can't translate, and shuffling in memory would mean pulling
                // every album back just to throw most of them away.
                "random" => albums.OrderBy(_ => EF.Functions.Random()),
                _ => albums.OrderBy(a => a.Album),
            };

            page = await ordered.Skip(offset).Take(take).ToListAsync();
        }

        return SubsonicResults.Ok(albumList2: new AlbumList2(page.Select(SubsonicMapper.ToAlbumId3).ToList()));
    }

    // The shared "one row per album, aggregated by SQL" projection behind both
    // GetAlbumList2 and Search3 - see GetAlbumList2's comment for why the
    // scalars use Min() rather than an arbitrary first row.
    private static IQueryable<SubsonicMapper.AlbumSummary> AlbumSummaries(IQueryable<TrackEntity> tracks) =>
        tracks
            .GroupBy(t => t.AlbumId)
            .Select(g => new SubsonicMapper.AlbumSummary
            {
                AlbumId = g.Key,
                Album = g.Min(t => t.Album),
                AlbumArtist = g.Min(t => t.AlbumArtist),
                ArtistId = g.Min(t => t.ArtistId),
                SongCount = g.Count(),
                TotalDurationSeconds = g.Sum(t => t.DurationSeconds),
                Year = g.Min(t => t.Year),
                Genre = g.Min(t => t.Genre),
            });

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

        // Three targeted, individually-limited queries rather than one
        // unbounded fetch of every SQL-side match followed by a second,
        // in-memory re-filter.
        //
        // The two passes did not agree: SQL's LIKE and .NET's
        // OrdinalIgnoreCase have different case semantics, so a match SQLite
        // accepted could be dropped again in memory (and the SQL pass was the
        // one deciding how much got materialized). There is now one filter, in
        // SQL, and the Take happens there too - so a one-character query stops
        // pulling most of the library back to discard it. See
        // ARCHITECTURE-REVIEW Tier 1.3.
        var songs = (await db.Tracks
            .Where(t => t.Title != null && EF.Functions.Like(t.Title, $"%{query}%"))
            .Take(songCount)
            .ToListAsync())
            .Select(SubsonicMapper.ToChild)
            .ToList();

        var albums = (await AlbumSummaries(
                db.Tracks.Where(t => t.Album != null && EF.Functions.Like(t.Album, $"%{query}%")))
            .Take(albumCount)
            .ToListAsync())
            .Select(SubsonicMapper.ToAlbumId3)
            .ToList();

        var artistPairs = await db.Tracks
            .Where(t => t.AlbumArtist != null && EF.Functions.Like(t.AlbumArtist, $"%{query}%"))
            .GroupBy(t => new { t.ArtistId, t.AlbumId })
            .Select(g => new { g.Key.ArtistId, Name = g.Min(t => t.AlbumArtist) })
            .ToListAsync();

        var artists = artistPairs
            .GroupBy(r => r.ArtistId)
            .Take(artistCount)
            .Select(g => SubsonicMapper.ToArtistId3(g.Key, g.Min(x => x.Name) ?? "Unknown Artist", g.Count()))
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
