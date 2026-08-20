using Microsoft.Extensions.Options;

using Flower.Models;
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
//
// Data access is LibraryQueries/PlaylistQueries over the schema shared with
// the client (Flower.Core/Persistence/Sql/), not EF Core - see LibraryQueries'
// own remarks and ARCHITECTURE-REVIEW Tier 4.1. Handlers are synchronous
// because SQLite is: Microsoft.Data.Sqlite's *Async methods block on the same
// native calls, so awaiting them bought nothing but a state machine.
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

        Map("/getArtists", GetArtists);
        Map("/getArtist", GetArtist);
        Map("/getAlbum", GetAlbum);
        Map("/getAlbumList2", GetAlbumList2);
        Map("/getSong", GetSong);
        Map("/search3", Search3);
        Map("/getPlaylists", GetPlaylists);
        Map("/getPlaylist", GetPlaylist);
        Map("/createPlaylist", CreatePlaylist);
        Map("/updatePlaylist", UpdatePlaylist);
        Map("/deletePlaylist", DeletePlaylist);
        Map("/scrobble", Scrobble);
        Map("/stream", Stream);
        Map("/download", Download);
        Map("/getCoverArt", GetCoverArt);

        Map("/star", (HttpRequest r, LibraryQueries q) => SetStarred(true, r, q));
        Map("/unstar", (HttpRequest r, LibraryQueries q) => SetStarred(false, r, q));

        // Every Subsonic route answers under both its bare name and the legacy
        // ".view" suffix real clients still send. Registering the pair in one
        // place beats 15 hand-written duplicate lines that could drift.
        void Map(string route, Delegate handler)
        {
            rest.MapGet(route, handler);
            rest.MapGet(route + ".view", handler);
        }
    }

    private static IResult GetArtists(LibraryQueries queries)
    {
        var artists = FoldArtists(queries.ArtistAlbumPairs())
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var indices = artists
            .GroupBy(a => IndexLetter(a.Name))
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new IndexID3(g.Key, g.Select(a => SubsonicMapper.ToArtistId3(a.Id, a.Name, a.AlbumCount)).ToList()))
            .ToList();

        return SubsonicResults.Ok(artists: new ArtistsID3(indices));
    }

    // (artist, album) pairs -> one entry per artist with its album count. The
    // pairs are already collapsed SQL-side, so this is a fold over ~one row per
    // album rather than one per track.
    private static IEnumerable<(string Id, string Name, int AlbumCount)> FoldArtists(IEnumerable<ArtistAlbumPair> pairs) =>
        pairs
            .GroupBy(p => p.ArtistId)
            .Select(g => (Id: g.Key, Name: g.Min(p => p.Name) ?? "Unknown Artist", AlbumCount: g.Count()));

    private static string IndexLetter(string name)
    {
        var trimmed = name.TrimStart();
        var c = trimmed.Length > 0 ? char.ToUpperInvariant(trimmed[0]) : '#';
        return char.IsLetter(c) ? c.ToString() : "#";
    }

    private static IResult GetArtist(string? id, LibraryQueries queries)
    {
        if (string.IsNullOrEmpty(id))
            return SubsonicResults.Failed(10, "Required parameter 'id' missing.");

        var tracks = queries.TracksByArtist(id);
        if (tracks.Count == 0)
            return SubsonicResults.Failed(70, "Artist not found.");

        var albums = tracks
            .GroupBy(t => SubsonicIdentity.AlbumId(t.EffectiveAlbumArtist, t.Album))
            .Select(SubsonicMapper.ToAlbumId3)
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var artist = new ArtistWithAlbumsID3(id, tracks[0].EffectiveAlbumArtist, null, albums.Count, albums);
        return SubsonicResults.Ok(artist: artist);
    }

    private static IResult GetAlbum(string? id, LibraryQueries queries)
    {
        if (string.IsNullOrEmpty(id))
            return SubsonicResults.Failed(10, "Required parameter 'id' missing.");

        var tracks = queries.TracksByAlbum(id);
        if (tracks.Count == 0)
            return SubsonicResults.Failed(70, "Album not found.");

        var first = tracks[0];
        var album = new AlbumWithSongsID3(
            // id is already an "al-..." SubsonicIdentity.AlbumId value (see
            // GetCoverArt's own "al-" prefix check below) - not re-prefixed here.
            id, first.Album ?? "Unknown Album", first.EffectiveAlbumArtist,
            SubsonicIdentity.ArtistId(first.EffectiveAlbumArtist),
            id, tracks.Count, (long)tracks.Sum(t => t.Duration.TotalSeconds),
            ParseYear(first.Year), first.Genre,
            tracks.Select(SubsonicMapper.ToChild).ToList());

        return SubsonicResults.Ok(album: album);
    }

    private static IResult GetAlbumList2(
        LibraryQueries queries, string type = "alphabeticalByName", int size = 500, int offset = 0)
    {
        // Grouped, ordered and paginated entirely in SQL, including "newest" -
        // which under EF Core had to fall back to aggregating in memory,
        // because that provider refuses MAX() over a DateTimeOffset. The shared
        // schema stores timestamps as INTEGER ticks, so it is now just an
        // integer sort. Returning one page of albums touches one page of rows.
        var page = queries.AlbumSummaries(type, take: size <= 0 ? 500 : size, offset: offset);
        return SubsonicResults.Ok(albumList2: new AlbumList2(page.Select(SubsonicMapper.ToAlbumId3).ToList()));
    }

    private static IResult GetSong(string? id, LibraryQueries queries)
    {
        if (string.IsNullOrEmpty(id))
            return SubsonicResults.Failed(10, "Required parameter 'id' missing.");

        var track = queries.Find(id);
        if (track is null)
            return SubsonicResults.Failed(70, "Song not found.");

        return SubsonicResults.Ok(song: SubsonicMapper.ToChild(track));
    }

    private static IResult Search3(
        LibraryQueries queries,
        string query = "", int artistCount = 20, int albumCount = 20, int songCount = 20)
    {
        // Three targeted, individually-limited queries rather than one
        // unbounded fetch of every SQL-side match followed by a second,
        // in-memory re-filter.
        //
        // The two passes did not agree: SQL's LIKE and .NET's
        // OrdinalIgnoreCase have different case semantics, so a match SQLite
        // accepted could be dropped again in memory (and the SQL pass was the
        // one deciding how much got materialized). There is now one filter, in
        // SQL, and the limit is applied there too - so a one-character query
        // stops pulling most of the library back to discard it. See
        // ARCHITECTURE-REVIEW Tier 1.3.
        var songs = queries.SearchSongs(query, songCount).Select(SubsonicMapper.ToChild).ToList();
        var albums = queries.SearchAlbums(query, albumCount).Select(SubsonicMapper.ToAlbumId3).ToList();
        var artists = FoldArtists(queries.ArtistAlbumPairs(matching: query))
            .Take(artistCount)
            .Select(a => SubsonicMapper.ToArtistId3(a.Id, a.Name, a.AlbumCount))
            .ToList();

        return SubsonicResults.Ok(searchResult3: new SearchResult3(artists, albums, songs));
    }

    private static IResult GetPlaylists(PlaylistQueries playlists, LibraryQueries queries)
    {
        var rows = playlists.All();
        var byId = queries.ByIds(rows.SelectMany(p => p.TrackIds).Distinct().ToList());

        var dtos = rows.Select(p => new PlaylistDto(
            p.Id, p.Name, p.Comment,
            p.TrackIds.Count,
            (long)p.TrackIds.Sum(id => byId.TryGetValue(id, out var t) ? t.Duration.TotalSeconds : 0),
            null, p.IsPublic)).ToList();

        return SubsonicResults.Ok(playlists: new Flower.Services.Playlists(dtos));
    }

    private static IResult GetPlaylist(string? id, PlaylistQueries playlists, LibraryQueries queries)
    {
        if (string.IsNullOrEmpty(id))
            return SubsonicResults.Failed(10, "Required parameter 'id' missing.");

        var playlist = playlists.Find(id);
        if (playlist is null)
            return SubsonicResults.Failed(70, "Playlist not found.");

        var byId = queries.ByIds(playlist.TrackIds.Distinct().ToList());

        // An entry whose id no longer resolves is skipped rather than blocked
        // by a foreign key - see PlaylistQueries' remarks and Schema.V1.
        var entries = playlist.TrackIds
            .Where(byId.ContainsKey)
            .Select(trackId => SubsonicMapper.ToChild(byId[trackId]))
            .ToList();

        var dto = new PlaylistWithSongsDto(
            playlist.Id, playlist.Name, playlist.Comment, entries.Count,
            (long)entries.Sum(e => e.Duration ?? 0), null, playlist.IsPublic, entries);

        return SubsonicResults.Ok(playlist: dto);
    }

    private static IResult CreatePlaylist(HttpRequest request, PlaylistQueries playlists, LibraryQueries queries)
    {
        var name = request.Query["name"].ToString();
        if (string.IsNullOrEmpty(name))
            return SubsonicResults.Failed(10, "Required parameter 'name' missing.");

        var id = playlists.Create(name, ParseIds(request.Query["songId"]));
        return GetPlaylist(id, playlists, queries);
    }

    private static IResult UpdatePlaylist(HttpRequest request, PlaylistQueries playlists)
    {
        var playlistId = request.Query["playlistId"].ToString();
        if (string.IsNullOrEmpty(playlistId))
            return SubsonicResults.Failed(10, "Required parameter 'playlistId' missing.");

        var ordered = playlists.Membership(playlistId);
        if (ordered is null)
            return SubsonicResults.Failed(70, "Playlist not found.");

        var removeIndexes = request.Query["songIndexToRemove"]
            .Where(s => int.TryParse(s, out _))
            .Select(s => int.Parse(s!))
            .ToHashSet();

        var updated = removeIndexes.Count == 0
            ? ordered.ToList()
            : ordered.Where((_, i) => !removeIndexes.Contains(i)).ToList();
        updated.AddRange(ParseIds(request.Query["songIdToAdd"]));

        request.Query.TryGetValue("name", out var name);
        request.Query.TryGetValue("comment", out var comment);
        var isPublic = request.Query.TryGetValue("public", out var publicValue)
            ? string.Equals(publicValue, "true", StringComparison.OrdinalIgnoreCase)
            : (bool?)null;

        if (!playlists.Update(
                playlistId,
                string.IsNullOrEmpty(name) ? null : name.ToString(),
                comment.Count == 0 ? null : comment.ToString(),
                isPublic,
                updated))
        {
            return SubsonicResults.Failed(70, "Playlist not found.");
        }

        return SubsonicResults.Ok();
    }

    private static IResult DeletePlaylist(string? id, PlaylistQueries playlists)
    {
        if (string.IsNullOrEmpty(id))
            return SubsonicResults.Failed(10, "Required parameter 'id' missing.");

        return playlists.Delete(id)
            ? SubsonicResults.Ok()
            : SubsonicResults.Failed(70, "Playlist not found.");
    }

    // A song id that isn't a Guid can never match a row, so it is dropped here
    // rather than stored as membership that would silently never resolve.
    private static List<Guid> ParseIds(IEnumerable<string?> values) =>
        values.Where(s => Guid.TryParse(s, out _)).Select(s => Guid.Parse(s!)).ToList();

    private static IResult SetStarred(bool starred, HttpRequest request, LibraryQueries queries)
    {
        var (column, value) = Target(request);
        if (column is null)
            return SubsonicResults.Failed(10, "One of id/albumId/artistId is required.");

        // Starring is one UPDATE over the matching rows - by row id, by
        // album_id or by artist_id, all three indexed - rather than loading
        // every matching track, mutating it and writing it back.
        queries.SetStarred(column, value!, starred);
        return SubsonicResults.Ok();

        static (string? Column, string? Value) Target(HttpRequest request)
        {
            var id = request.Query["id"].ToString();
            if (!string.IsNullOrEmpty(id))
                return (LibraryQueries.IdColumn, Guid.TryParse(id, out var parsed) ? parsed.ToString("N") : id);

            var albumId = request.Query["albumId"].ToString();
            if (!string.IsNullOrEmpty(albumId))
                return (LibraryQueries.AlbumIdColumn, albumId);

            var artistId = request.Query["artistId"].ToString();
            if (!string.IsNullOrEmpty(artistId))
                return (LibraryQueries.ArtistIdColumn, artistId);

            return (null, null);
        }
    }

    private static IResult Scrobble(string? id, LibraryQueries queries, bool submission = true)
    {
        if (string.IsNullOrEmpty(id))
            return SubsonicResults.Failed(10, "Required parameter 'id' missing.");

        if (submission)
            queries.IncrementPlayCount(id);

        return SubsonicResults.Ok();
    }

    private static IResult Stream(string? id, LibraryQueries queries)
    {
        var track = FindPlayable(id, queries);
        return track is null
            ? Results.NotFound()
            : Results.File(track.Path!, SubsonicMapper.ContentTypeOf(track), enableRangeProcessing: true);
    }

    private static IResult Download(string? id, LibraryQueries queries)
    {
        var track = FindPlayable(id, queries);
        return track is null
            ? Results.NotFound()
            : Results.File(track.Path!, SubsonicMapper.ContentTypeOf(track),
                fileDownloadName: Path.GetFileName(track.Path!), enableRangeProcessing: true);
    }

    private static Track? FindPlayable(string? id, LibraryQueries queries)
    {
        if (string.IsNullOrEmpty(id))
            return null;

        var track = queries.Find(id);
        return track?.Path is not null && File.Exists(track.Path) ? track : null;
    }

    private static IResult GetCoverArt(string? id, LibraryQueries queries)
    {
        if (string.IsNullOrEmpty(id))
            return Results.NotFound();

        List<Track> candidates;
        if (id.StartsWith("al-", StringComparison.Ordinal))
        {
            candidates = queries.TracksByAlbum(id);
        }
        else
        {
            var track = queries.Find(id);
            candidates = track is null ? [] : [track];
        }

        foreach (var candidate in candidates)
        {
            // Shared with the client and SyncHttpServer - see
            // LocalAlbumArtReader, which this used to be a private copy of.
            var art = LocalAlbumArtReader.ForFile(candidate.Path);
            if (art is not null)
                return Results.Bytes(art.Bytes, art.MimeType);
        }

        return Results.NotFound();
    }

    private static int? ParseYear(string? year) => int.TryParse(year, out var parsed) ? parsed : null;
}
