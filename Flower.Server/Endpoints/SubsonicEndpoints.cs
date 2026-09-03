using Microsoft.Extensions.Options;

using Flower.Models;
using Flower.Persistence;
using Flower.Persistence.Sql;
using Flower.Server.Configuration;
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
// Track reads come from the resident Flower.Core Library the client also runs
// on - the same LibrarySnapshot its own embedded sync server reads through -
// and writes go through the same Library, which mutates it and persists the
// change in the same call. Playlists are the same story one level down: these
// handlers edit the library's own resident Playlist objects - the very objects
// the client's sidebar edits - and Library turns that into the write. There is
// no server-side library or playlist type at all. See ARCHITECTURE-REVIEW
// Tier 4.1. Handlers are synchronous
// because SQLite is: Microsoft.Data.Sqlite's *Async methods block on the same
// native calls, so awaiting them bought nothing but a state machine.
public static class SubsonicEndpoints
{
    // Classic Subsonic auth is t=md5(password+salt) with no expiry and no
    // nonce, so a captured u/t/s query string replays forever and a wrong one
    // costs an attacker nothing to retry - this surface had no rate limiting
    // at all. Two budgets:
    //
    // - RequestLimiter is charged on every request, keyed by source via
    //   RateLimiter.KeyFor, and sized for real client behaviour: an album grid
    //   pulls one getCoverArt per tile, which is bursty enough that a
    //   120/60s browse ceiling would be too tight here.
    // - FailedAuthLimiter is charged only when a *password* attempt fails, and
    //   gates only the password path.
    //
    // That scoping is docs/OPEN-INTERNET-REVIEW.md finding #2. This used to be
    // peeked before anything else, which made it a lockout of the whole surface
    // rather than a throttle on guessing: ten bad passwords from any one source
    // took /rest away from every other caller sharing that key - already a real
    // outcome for two listeners behind one house NAT, and behind a tunnel with
    // TrustedProxies unset, everyone. A signature or a stream ticket cannot be
    // guessed, so nothing is bought by refusing one because somebody else got a
    // password wrong.
    //
    // The guessing bound itself is unchanged where it matters: an over-budget
    // password attempt is refused *without being evaluated*, so burning the
    // budget can never admit a lucky guess. Keyed by source and username
    // together, so hammering one account cannot lock out another client behind
    // the same address. An attacker rotating usernames does get a fresh budget
    // each, bounded only by RequestLimiter - which is 600/60s against a 32-char
    // CSPRNG secret (SubsonicCredentialStore.GenerateSecret, never a
    // human-chosen password), so guessing was never the threat this bounds.
    // What it bounds is a probe flood, and it still does.
    private static readonly RateLimiter FailedAuthLimiter = new(max: 10, TimeSpan.FromSeconds(60));
    private static readonly RateLimiter RequestLimiter = new(max: 600, TimeSpan.FromSeconds(60));

    public static void MapSubsonicEndpoints(this WebApplication app)
    {
        var rest = app.MapGroup("/rest").AddEndpointFilter(async (context, next) =>
        {
            var services = context.HttpContext.RequestServices;
            var query = context.HttpContext.Request.Query;
            var key = RateLimiter.KeyFor(context.HttpContext.Connection.RemoteIpAddress);
            var now = DateTimeOffset.UtcNow;

            if (!RequestLimiter.TryAcquire(key, now))
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);

            // Three ways in, deliberately unequal in power. A path-A device
            // signature authenticates the whole /rest surface for a paired
            // Flower device, which never holds a username/password at all. A
            // path-B credential (SubsonicCredentialStore) does the same for a
            // third-party Subsonic client, which cannot sign. A stream ticket
            // authenticates one track, because that is all an <audio>
            // element's unsignable request should ever be able to reach - see
            // StreamTicketService.
            //
            // The two unguessable ones are tried first, so a caller holding one
            // never touches the failed-auth budget in either direction.

            // Path A: a paired Flower device browsing/streaming this server
            // with its device signature rather than a username and password.
            // Without it, pairing
            // succeeded (TrustedPeerStore) but every /rest call the client made
            // afterwards came back "Wrong username or password", because
            // PeerOpenSubsonicClientFactory deliberately sends empty u/p and
            // signs instead. GETs only here, so the signed body is always
            // empty.
            var trustedPeers = services.GetRequiredService<TrustedPeerStore>();
            var replayGuard = services.GetRequiredService<NonceReplayGuard>();
            if (DeviceSignatureAuth.VerifyTrustedPeer(context.HttpContext.Request, [], trustedPeers, replayGuard) != null)
                return await next(context);

            var tickets = services.GetRequiredService<StreamTicketService>();
            if (tickets.TryRedeem(query["ticket"].ToString(), query["id"].ToString(), now))
                return await next(context);

            // Path B, and the only guessable credential on this surface - hence
            // the budget, and hence checking it before the comparison rather
            // than after. An over-budget attempt is refused unevaluated, so no
            // amount of burning it can turn into a lucky guess being admitted.
            var attempted = query["u"].ToString();
            var failedAuthKey = $"{key}|{attempted}";
            if (!FailedAuthLimiter.WouldAllow(failedAuthKey, now))
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);

            var credentials = services.GetRequiredService<SubsonicCredentialStore>();
            var username = SubsonicAuth.Validate(query, credentials);
            if (username != null)
            {
                // Fire-and-forget, and rate-limited inside the store itself:
                // last-seen is an admin convenience, not something a stream
                // request should wait on a file write for.
                _ = credentials.TouchAsync(username, now);
                return await next(context);
            }

            FailedAuthLimiter.TryAcquire(failedAuthKey, now);
            // A Flower device that signed but is not trusted is a pairing
            // problem, not a password problem, and saying "wrong username or
            // password" to a client that holds neither sends the user looking
            // for a credential that does not exist. Third-party clients still
            // get the protocol's own wording.
            return DeviceSignatureAuth.GetIdentityValue(context.HttpContext.Request, "X-Flower-Fingerprint") != null
                ? SubsonicResults.Failed(40, "This device is not paired with this server.")
                : SubsonicResults.Failed(40, "Wrong username or password.");
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

        Map("/star", (HttpRequest r, Library l) => SetStarred(true, r, l));
        Map("/unstar", (HttpRequest r, Library l) => SetStarred(false, r, l));

        // Every Subsonic route answers under both its bare name and the legacy
        // ".view" suffix real clients still send. Registering the pair in one
        // place beats 15 hand-written duplicate lines that could drift.
        void Map(string route, Delegate handler)
        {
            rest.MapGet(route, handler);
            rest.MapGet(route + ".view", handler);
        }
    }

    private static IResult GetArtists(Library library)
    {
        var artists = FoldArtists(library.Snapshot.Albums)
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var indices = artists
            .GroupBy(a => IndexLetter(a.Name))
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new IndexID3(g.Key, g.Select(a => SubsonicMapper.ToArtistId3(a.Id, a.Name, a.AlbumCount)).ToList()))
            .ToList();

        return SubsonicResults.Ok(artists: new ArtistsID3(indices));
    }

    // Albums -> one entry per artist with its album count. Folding the
    // pre-grouped albums means this walks ~one entry per album rather than one
    // per track, the same shape the SQL version's GROUP BY produced.
    private static IEnumerable<(string Id, string Name, int AlbumCount)> FoldArtists(IEnumerable<AlbumEntry> albums) =>
        albums
            .GroupBy(a => a.Summary.ArtistId!)
            .Select(g => (Id: g.Key, Name: g.Min(a => a.Summary.AlbumArtist) ?? "Unknown Artist", AlbumCount: g.Count()));

    private static string IndexLetter(string name)
    {
        var trimmed = name.TrimStart();
        var c = trimmed.Length > 0 ? char.ToUpperInvariant(trimmed[0]) : '#';
        return char.IsLetter(c) ? c.ToString() : "#";
    }

    private static IResult GetArtist(string? id, Library library)
    {
        if (string.IsNullOrEmpty(id))
            return SubsonicResults.Failed(10, "Required parameter 'id' missing.");

        // The artist's albums come straight off the snapshot's grouping rather
        // than being re-derived from its tracks - one rule, applied once per
        // rescan.
        var albums = library.Snapshot.Albums
            .Where(a => a.Summary.ArtistId == id)
            .OrderBy(a => a.Summary.Album, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (albums.Count == 0)
            return SubsonicResults.Failed(70, "Artist not found.");

        var artist = new ArtistWithAlbumsID3(
            id, albums[0].Summary.AlbumArtist ?? "Unknown Artist", null, albums.Count,
            albums.Select(a => SubsonicMapper.ToAlbumId3(a.Summary)).ToList());

        return SubsonicResults.Ok(artist: artist);
    }

    private static IResult GetAlbum(string? id, Library library, IOptionsMonitor<FlowerServerOptions> options)
    {
        if (string.IsNullOrEmpty(id))
            return SubsonicResults.Failed(10, "Required parameter 'id' missing.");

        if (library.Snapshot.Album(id) is not { } album)
            return SubsonicResults.Failed(70, "Album not found.");

        var summary = album.Summary;
        var dto = new AlbumWithSongsID3(
            // id is already an "al-..." SubsonicIdentity.AlbumId value (see
            // GetCoverArt's own "al-" prefix check below) - not re-prefixed here.
            id, summary.Album ?? "Unknown Album", summary.AlbumArtist,
            summary.ArtistId ?? "",
            id, summary.SongCount, (long)summary.TotalDuration.TotalSeconds,
            summary.Year, summary.Genre,
            album.Tracks.Select(t => SubsonicMapper.ToChild(t, libraryRoots: options.CurrentValue.LibraryPaths)).ToList());

        return SubsonicResults.Ok(album: dto);
    }

    private static IResult GetAlbumList2(
        Library library, string type = "alphabeticalByName", int size = 500, int offset = 0)
    {
        // Sorting ~1.4k pre-grouped albums, not grouping ~16k rows. The
        // grouping happened once, at the last rescan; this request only picks
        // an order and a page.
        var albums = library.Snapshot.Albums;
        IEnumerable<AlbumEntry> ordered = type switch
        {
            "newest" => albums.OrderByDescending(a => a.NewestDateAdded),
            "alphabeticalByArtist" => albums.OrderBy(a => a.Summary.AlbumArtist, StringComparer.OrdinalIgnoreCase),
            // Shuffled per request, as the protocol intends - a stable order
            // here would make repeated calls return the same "random" page.
            "random" => albums.OrderBy(_ => Random.Shared.Next()),
            _ => albums.OrderBy(a => a.Summary.Album, StringComparer.OrdinalIgnoreCase),
        };

        var page = ordered
            .Skip(Math.Max(offset, 0))
            .Take(size <= 0 ? 500 : size)
            .Select(a => SubsonicMapper.ToAlbumId3(a.Summary))
            .ToList();

        return SubsonicResults.Ok(albumList2: new AlbumList2(page));
    }

    private static IResult GetSong(string? id, Library library, IOptionsMonitor<FlowerServerOptions> options)
    {
        if (string.IsNullOrEmpty(id))
            return SubsonicResults.Failed(10, "Required parameter 'id' missing.");

        var track = library.Find(id);
        if (track is null)
            return SubsonicResults.Failed(70, "Song not found.");

        return SubsonicResults.Ok(song: SubsonicMapper.ToChild(track, libraryRoots: options.CurrentValue.LibraryPaths));
    }

    private static IResult Search3(
        Library library,
        IOptionsMonitor<FlowerServerOptions> options,
        string query = "", int artistCount = 20, int albumCount = 20, int songCount = 20)
    {
        // One filter, in one place. This was two disagreeing passes under EF
        // (a SQL LIKE plus an in-memory re-filter with different case
        // semantics - Tier 1.3), then one SQL LIKE with hand-escaped
        // wildcards. Matching in memory retires the escaping problem outright:
        // there is no LIKE, so a query containing % or _ is just a query
        // containing % or _. It also fixes what escaping could not - LIKE's
        // case-insensitivity is ASCII-only, so a lowercase accented letter
        // never matched its uppercase form.
        var snapshot = library.Snapshot;

        var songs = snapshot.Tracks
            .Where(t => Matches(t.Title, query))
            .Take(songCount)
            .Select(t => SubsonicMapper.ToChild(t, libraryRoots: options.CurrentValue.LibraryPaths))
            .ToList();

        var albums = snapshot.Albums
            .Where(a => Matches(a.Summary.Album, query))
            .OrderBy(a => a.Summary.Album, StringComparer.OrdinalIgnoreCase)
            .Take(albumCount)
            .Select(a => SubsonicMapper.ToAlbumId3(a.Summary))
            .ToList();

        var artists = FoldArtists(snapshot.Albums.Where(a => Matches(a.Summary.AlbumArtist, query)))
            .Take(artistCount)
            .Select(a => SubsonicMapper.ToArtistId3(a.Id, a.Name, a.AlbumCount))
            .ToList();

        return SubsonicResults.Ok(searchResult3: new SearchResult3(artists, albums, songs));
    }

    // An empty query matches nothing rather than everything: Subsonic clients
    // send search3 on every keystroke, and returning the whole library for the
    // empty string is not a search result.
    private static bool Matches(string? value, string query) =>
        !string.IsNullOrEmpty(query) && SearchText.Contains(value, query);

    private static IResult GetPlaylists(Library library)
    {
        var dtos = library.Playlists.Select(ToDto).ToList();
        return SubsonicResults.Ok(playlists: new Flower.Services.Playlists(dtos));
    }

    // Membership is already resolved to live Tracks by the time it is resident
    // - PlaylistRepository.Load drops an id the library no longer has, and
    // Library.ResolveTracks does the same on the way in - so there is no
    // second "skip what does not resolve" pass here any more.
    private static PlaylistDto ToDto(Playlist playlist) => new(
        playlist.Id.ToKey(),
        playlist.Name,
        playlist.Comment,
        playlist.Tracks.Count,
        (long)playlist.Tracks.Sum(t => t.Duration.TotalSeconds),
        // Owner. Flower has no user model - see the auth notes above - so
        // there is nobody to name. Playlist.CreatedAt is stored and loaded but
        // has no field on PlaylistDto to surface it through.
        null,
        playlist.IsPublic);

    private static IResult GetPlaylist(string? id, Library library, IOptionsMonitor<FlowerServerOptions> options)
    {
        if (string.IsNullOrEmpty(id))
            return SubsonicResults.Failed(10, "Required parameter 'id' missing.");

        if (library.FindPlaylist(id) is not { } playlist)
            return SubsonicResults.Failed(70, "Playlist not found.");

        var entries = playlist.Tracks.Select(t => SubsonicMapper.ToChild(t, libraryRoots: options.CurrentValue.LibraryPaths)).ToList();
        var summary = ToDto(playlist);

        var dto = new PlaylistWithSongsDto(
            summary.Id, summary.Name, summary.Comment, summary.SongCount,
            summary.Duration, summary.Owner, summary.Public, entries);

        return SubsonicResults.Ok(playlist: dto);
    }

    private static IResult CreatePlaylist(HttpRequest request, Library library, IOptionsMonitor<FlowerServerOptions> options)
    {
        var name = request.Query["name"].ToString();
        if (string.IsNullOrEmpty(name))
            return SubsonicResults.Failed(10, "Required parameter 'name' missing.");

        // AddPlaylist persists, exactly as it does for a playlist created from
        // the client's sidebar - see Library's IPlaylistStore.
        var created = new Playlist(name, library.ResolveTracks(request.Query["songId"]));
        library.AddPlaylist(created);
        return GetPlaylist(created.Id.ToKey(), library, options);
    }

    private static IResult UpdatePlaylist(HttpRequest request, Library library)
    {
        var playlistId = request.Query["playlistId"].ToString();
        if (string.IsNullOrEmpty(playlistId))
            return SubsonicResults.Failed(10, "Required parameter 'playlistId' missing.");

        if (library.FindPlaylist(playlistId) is not { } existing)
            return SubsonicResults.Failed(70, "Playlist not found.");

        // A smart playlist's contents are the output of its rules, so an edit
        // accepted here would be erased by the next recomputation - a silent
        // undo minutes later, which is worse for a third-party client than an
        // error it can show. Refused whole rather than partly applied: there is
        // no wire vocabulary for rules in OpenSubsonic (getPlaylist reports one
        // of these as an ordinary playlist, because that is the only thing the
        // protocol can describe), so a client cannot tell which parts of its
        // call would have survived. Editing the rules is Flower's own editor,
        // and deletePlaylist still works.
        if (existing.IsSmart)
            return SubsonicResults.Failed(50, "This is a smart playlist; its contents come from its rules and cannot be edited directly.");

        var removeIndexes = request.Query["songIndexToRemove"]
            .Where(s => int.TryParse(s, out _))
            .Select(s => int.Parse(s!))
            .ToHashSet();

        // Applied to the resident Tracks directly. This used to project them
        // out to ids and resolve them straight back - a round trip that existed
        // only because the server's old storage-shaped view of a playlist was a
        // list of ids.
        var updated = removeIndexes.Count == 0
            ? existing.Tracks.ToList()
            : existing.Tracks.Where((_, i) => !removeIndexes.Contains(i)).ToList();
        updated.AddRange(library.ResolveTracks(request.Query["songIdToAdd"]));

        request.Query.TryGetValue("name", out var name);
        request.Query.TryGetValue("comment", out var comment);
        var isPublic = request.Query.TryGetValue("public", out var publicValue)
            ? string.Equals(publicValue, "true", StringComparison.OrdinalIgnoreCase)
            : (bool?)null;

        // Set on the Playlist itself, which is the same object the client
        // edits from its sidebar: each setter bumps UpdatedAt and raises
        // Playlist.Changed, which Library turns into one write of the set (see
        // RaisePlaylistsChanged). Only the attributes Subsonic actually sent -
        // updatePlaylist omits the ones it is not changing, and an absent one
        // has to leave the stored value alone.
        if (!string.IsNullOrEmpty(name))
            existing.Name = name.ToString();

        if (comment.Count > 0)
            existing.Comment = comment.ToString();

        if (isPublic is not null)
            existing.IsPublic = isPublic.Value;

        existing.ReplaceAll(updated);
        return SubsonicResults.Ok();
    }

    private static IResult DeletePlaylist(string? id, Library library)
    {
        if (string.IsNullOrEmpty(id))
            return SubsonicResults.Failed(10, "Required parameter 'id' missing.");

        if (library.FindPlaylist(id) is not { } playlist)
            return SubsonicResults.Failed(70, "Playlist not found.");

        library.RemovePlaylist(playlist);
        return SubsonicResults.Ok();
    }

    // A song id that isn't a Guid can never match a row, so it is dropped here
    // rather than stored as membership that would silently never resolve.
    private static IResult SetStarred(bool starred, HttpRequest request, Library library)
    {
        var (target, value) = Target(request);
        if (value is null)
            return SubsonicResults.Failed(10, "One of id/albumId/artistId is required.");

        // Applied to the resident tracks and persisted in the same call - see
        // Library.SetStarred. Starring a whole album or artist is one
        // indexed UPDATE, not one write per track.
        library.SetStarred(target, value, starred);
        return SubsonicResults.Ok();

        static (StarTarget Target, string? Value) Target(HttpRequest request)
        {
            var id = request.Query["id"].ToString();
            if (!string.IsNullOrEmpty(id))
                // Passed through exactly as the client sent it. Canonicalising
                // it here was this file's own id conversion, and it existed for
                // the *write*: the id column stores 32-char hex, so a dashed
                // Guid would resolve in memory and then match no row. Library
                // now hands the store the id of the track it actually matched,
                // which is the only value guaranteed to agree with the row.
                return (StarTarget.Song, id);

            var albumId = request.Query["albumId"].ToString();
            if (!string.IsNullOrEmpty(albumId))
                return (StarTarget.Album, albumId);

            var artistId = request.Query["artistId"].ToString();
            if (!string.IsNullOrEmpty(artistId))
                return (StarTarget.Artist, artistId);

            return (StarTarget.Song, null);
        }
    }

    private static IResult Scrobble(string? id, Library library, bool submission = true)
    {
        if (string.IsNullOrEmpty(id))
            return SubsonicResults.Failed(10, "Required parameter 'id' missing.");

        if (submission)
            library.RecordPlay(id);

        return SubsonicResults.Ok();
    }

    // The one route on this surface that carries the music, and until now the
    // one route that left no trace of having been asked.
    //
    // That mattered the first time a client reported that streaming had
    // stopped working: ninety-two tracks skipped in one afternoon on a phone,
    // every one of them remote, every one of them decoding to zero bytes -
    // and nothing at all on the server to say whether the requests had even
    // arrived. The catalog routes were answering fine the whole time, so
    // "reachable" was never the question; "reachable for the bytes" was, and
    // it was unanswerable.
    //
    // Logged in two halves, because the interesting failures are not in the
    // first one. Starting a stream is a synchronous decision - the track is
    // known, or it is not - while everything that goes wrong afterwards
    // happens while ASP.NET Core is writing the file, long after this method
    // has returned its IResult. So the response's completion carries the other
    // half: how much actually went out, and whether the client was still there
    // at the end of it.
    private static IResult Stream(string? id, Library library, HttpContext context, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(StreamLogCategory);
        var track = FindPlayable(id, library);
        if (track is null)
        {
            logger.LogWarning(
                "Refusing to stream {Id} to {Peer}: no playable track with that id (unknown, or its file is gone)",
                id, StreamPeer(context));
            return Results.NotFound();
        }

        var range = context.Request.Headers.Range.ToString();
        logger.LogInformation(
            "Streaming \"{Title}\" ({Id}) to {Peer}{Range}",
            track.Title, id, StreamPeer(context), range.Length > 0 ? $" for range {range}" : "");

        var startedAt = DateTimeOffset.UtcNow;
        context.Response.OnCompleted(() =>
        {
            var elapsed = DateTimeOffset.UtcNow - startedAt;
            var sent = context.Response.ContentLength;

            if (context.RequestAborted.IsCancellationRequested)
            {
                // Not necessarily trouble - a skip, a seek and closing the app
                // all abort a stream mid-flight. It is trouble when it happens
                // to every track in a row, which is what this exists to show.
                logger.LogInformation(
                    "Stream of \"{Title}\" ({Id}) to {Peer} was cut off after {ElapsedMs:F0}ms",
                    track.Title, id, StreamPeer(context), elapsed.TotalMilliseconds);
                return Task.CompletedTask;
            }

            logger.LogInformation(
                "Finished streaming \"{Title}\" ({Id}) to {Peer}: {Status}, {Bytes} byte(s) in {ElapsedMs:F0}ms",
                track.Title, id, StreamPeer(context), context.Response.StatusCode, sent, elapsed.TotalMilliseconds);
            return Task.CompletedTask;
        });

        return Results.File(track.Path!, SubsonicMapper.ContentTypeOf(track), enableRangeProcessing: true);
    }

    // Named rather than typed: ILogger<T> needs a T, and this class is static.
    private const string StreamLogCategory = "Flower.Server.Subsonic.Stream";

    // Who asked, in whichever of the three currencies this surface accepts: a
    // paired device's fingerprint, a Subsonic username, or - for a stream
    // ticket, which names nobody - the address alone.
    private static string StreamPeer(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress?.ToString() ?? "an unknown address";

        if (DeviceSignatureAuth.GetIdentityValue(context.Request, "X-Flower-Fingerprint") is { Length: > 0 } fingerprint)
            return $"{fingerprint} at {address}";

        if (context.Request.Query["u"].ToString() is { Length: > 0 } username)
            return $"{username} at {address}";

        return address;
    }

    private static IResult Download(string? id, Library library)
    {
        var track = FindPlayable(id, library);
        return track is null
            ? Results.NotFound()
            : Results.File(track.Path!, SubsonicMapper.ContentTypeOf(track),
                fileDownloadName: Path.GetFileName(track.Path!), enableRangeProcessing: true);
    }

    private static Track? FindPlayable(string? id, Library library)
    {
        if (string.IsNullOrEmpty(id))
            return null;

        var track = library.Find(id);
        return track?.Path is not null && File.Exists(track.Path) ? track : null;
    }

    // Internal rather than private: SyncEndpoints serves the same bytes at
    // GET /api/flower/v1/cover-art for callers that authenticate with a session
    // token instead of a Subsonic credential or a signature - the browser head,
    // in practice. One handler, so the two doors cannot drift about what an
    // album's art is.
    internal static IResult GetCoverArt(string? id, Library library)
    {
        if (string.IsNullOrEmpty(id))
            return Results.NotFound();

        foreach (var candidate in CoverArtCandidates(id, library))
        {
            // Shared with the client - see LocalAlbumArtReader, which this used
            // to be a private copy of.
            var art = LocalAlbumArtReader.ForFile(candidate.Path);
            if (art is not null)
                return Results.Bytes(art.Bytes, art.MimeType);
        }

        return Results.NotFound();
    }

    // Which files an art request for this id is about: every track on an album
    // for an album id, or the one track for a song id. Shared with the admin
    // cover-art route (AdminEndpoints), which writes into exactly the files this
    // would have read from - so "the art you can see at this id" and "the art
    // you can replace at this id" cannot come apart.
    internal static IReadOnlyList<Track> CoverArtCandidates(string id, Library library)
    {
        if (id.StartsWith("al-", StringComparison.Ordinal))
            return library.Snapshot.AlbumTracks(id);

        return library.Find(id) is { } track ? [track] : [];
    }

}
