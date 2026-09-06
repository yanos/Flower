using Flower.Models;
using Flower.Persistence;
using Flower.Server.Services;
using Flower.Services;

namespace Flower.Server.Endpoints;

// Serving the bytes of a track, once something has decided the caller may have
// them. Two routes reach this and they are the same code:
//
//   GET /api/flower/v1/stream    - Flower's own, what every Flower client uses
//   GET /rest/stream             - the OpenSubsonic adapter, for third-party
//                                  clients that speak nothing else
//
// and the same pair for /download. Only the gate in front differs, which is the
// whole reason this is its own file: the native route admits a device signature
// or a stream ticket, the adapter route additionally admits a Subsonic password,
// and neither gate belongs to the thing that reads a file off disk.
//
// Deliberately GET-mapped, on both, so a HEAD reaches no endpoint and every
// client finds a track's length through the ranged-GET probe instead. That is
// not an oversight: it is the path Flower.DeviceChecks exercises on all five
// platforms, and answering HEAD here would take it out of every check at once.
//
// What a HEAD actually gets is a 404, not the 405 routing would give on its own
// - WebUiHosting's single-page fallback matches every method, and answers /api
// and /rest with a 404 rather than serving HTML to something expecting audio.
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

        return Results.File(track.Path!, LibraryDtoMapper.ContentTypeOf(track), enableRangeProcessing: true);
    }

    // Named rather than typed: ILogger<T> needs a T, and this class is static.
    private const string StreamLogCategory = "Flower.Server.Media.Stream";

    // Who asked, in whichever currency the gate that admitted them accepts: a
    // paired device's fingerprint, a Subsonic username on /rest, or - for a
    // stream ticket, which names nobody - the address alone.
    private static string StreamPeer(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress?.ToString() ?? "an unknown address";

        if (DeviceSignatureAuth.GetIdentityValue(context.Request, "X-Flower-Fingerprint") is { Length: > 0 } fingerprint)
            return $"{fingerprint} at {address}";

        if (context.Request.Query["u"].ToString() is { Length: > 0 } username)
            return $"{username} at {address}";

        return address;
    }

    internal static IResult Download(string? id, Library library)
    {
        var track = FindPlayable(id, library);
        return track is null
            ? Results.NotFound()
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
}
