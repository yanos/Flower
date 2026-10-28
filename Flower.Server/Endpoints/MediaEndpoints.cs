using Flower.Models;
using Flower.Persistence;
using Flower.Server.Services;
using Flower.Services;

namespace Flower.Server.Endpoints;

// Serving the bytes of a track, once something has decided the caller may have
// them: GET /api/flower/v1/stream, /download, and cover art.
//
// Its own file because the gate in front is not its business - a caller arrives
// holding a device signature or a stream ticket, and whichever admitted them,
// what reads a file off disk is the same code.
//
// This used to be shared with an OpenSubsonic adapter at /rest/*, which admitted
// a password as a third currency. The adapter is gone (see docs/SYNC-PLAN.md);
// that it could be deleted without taking a byte of media serving with it is
// what this separation was for.
//
// Deliberately GET-mapped so a HEAD reaches no endpoint and every
// client finds a track's length through the ranged-GET probe instead. That is
// not an oversight: it is the path Flower.DeviceChecks exercises on all five
// platforms, and answering HEAD here would take it out of every check at once.
//
// What a HEAD actually gets is a 404, not the 405 routing would give on its own
// - WebUiHosting's single-page fallback matches every method, and answers /api
// with a 404 rather than serving HTML to something expecting audio.
// Callers only ever ask whether the response succeeded (SeekableHttpStream's
// ProbeServerAsync), so the two are the same answer; the distinction is written
// down because three comments used to name the wrong one.
public static class MediaEndpoints
{
    // The route that carries the music, and for a long time the one route
    // that left no trace of having been asked.
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
    internal static IResult Stream(string? id, Library library, HttpContext context, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(StreamLogCategory);
        var track = FindPlayable(id, library);
        if (track is null)
        {
            logger.LogWarning(
                "Refusing to stream {Id} to {Peer}: no playable track with that id (unknown, or its file is gone)",
                id, StreamPeer(context));
            return Problems.NotFound();
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

        return Results.File(track.Path!, LibraryDtoMapper.ContentTypeOf(track), enableRangeProcessing: true);
    }

    // Named rather than typed: ILogger<T> needs a T, and this class is static.
    private const string StreamLogCategory = "Flower.Server.Media.Stream";

    // Who asked, as the gate in front found it rather than as the request
    // claims: the fingerprint whose signature verified, or the device a
    // stream ticket was minted for. Never the X-Flower-Fingerprint header on
    // a ticketed request, which nothing checks; and no longer a "u" query
    // parameter, which was the OpenSubsonic adapter's username and outlived
    // it here, read but never sent. See docs/SECURITY.md step 5.
    private static string StreamPeer(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress?.ToString() ?? "an unknown address";

        if (context.Items[SyncEndpoints.AuthenticatedFingerprintKey] is string { Length: > 0 } fingerprint)
            return $"{fingerprint} at {address}";

        if (context.Items[SyncEndpoints.TicketMinterKey] is string { Length: > 0 } minter)
            return $"{minter} (by ticket) at {address}";

        return address;
    }

    internal static IResult Download(string? id, Library library)
    {
        var track = FindPlayable(id, library);
        return track is null
            ? Problems.NotFound()
            : Results.File(track.Path!, LibraryDtoMapper.ContentTypeOf(track),
                fileDownloadName: Path.GetFileName(track.Path!), enableRangeProcessing: true);
    }

    private static Track? FindPlayable(string? id, Library library)
    {
        if (string.IsNullOrEmpty(id))
            return null;

        var track = library.Find(id);
        return track?.Path is not null && File.Exists(track.Path) ? track : null;
    }

    // Album art, at GET /api/flower/v1/cover-art. Out here with the routes
    // above rather than inline, for the same reason they are.
    // Found through CoverArtCache, which remembers where each id's art is.
    internal static IResult GetCoverArt(string? id, CoverArtCache covers)
    {
        if (string.IsNullOrEmpty(id))
            return Problems.NotFound();

        return covers.Find(id) is { } art
            ? Results.Bytes(art.Bytes, art.MimeType)
            : Problems.NotFound();
    }

    // A picture of an artist, at GET /api/flower/v1/artist-image?artist=<name>,
    // looked up online by this server for a paired device that is showing the
    // artist (OnlineMetadataEngine). The name as the device's Artists list
    // has it, which is the catalog's Track.Artists.
    //
    // Only for an artist this library has songs by, and checked against
    // their albums here: the device says who, and the server decides which
    // of Deezer's artists of that name it is - and a paired device cannot use
    // the server to look up whatever name it likes.
    //
    // 404 when there is no picture to be had, which a device remembers for a
    // while; 503 when there may be one but it could not be found out just now
    // (no way out, Deezer refusing), which it asks again about.
    internal static async Task<IResult> GetArtistImage(
        string? artist, Library library, OnlineMetadataEngine online, HttpContext context)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return Problems.NotFound();

        var tracks = library.Snapshot.Tracks.Where(t => ArtistNames.Same(t.Artists, artist)).ToList();
        if (tracks.Count == 0)
            return Problems.NotFound();

        var albums = tracks
            .Select(t => t.Album)
            .OfType<string>()
            .Where(album => album.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var picture = await online.ArtistPictureAsync(artist, albums, context.RequestAborted);
        if (picture == null)
            return Problems.Of(StatusCodes.Status503ServiceUnavailable, ProblemCodes.Unavailable);

        return picture.Length > 0 && LocalAlbumArtReader.MimeTypeForBytes(picture) is { } mimeType
            ? Results.Bytes(picture, mimeType)
            : Problems.NotFound();
    }

    // Which files an art request for this id is about: every track on an album
    // for an album id, or the one track for a song id. Shared with the batch
    // route (SyncEndpoints) and the admin cover-art route (AdminEndpoints),
    // which writes into exactly the files this reads from - so "the art you can
    // see at this id" and "the art you can replace at this id" cannot come
    // apart.
    internal static IReadOnlyList<Track> CoverArtCandidates(string id, Library library)
    {
        if (id.StartsWith("al-", StringComparison.Ordinal))
            return library.Snapshot.AlbumTracks(id);

        return library.Find(id) is { } track ? [track] : [];
    }
}
