using System;
using System.Collections.Generic;
using System.Linq;

namespace Flower.Services;

// POST /api/flower/v1/album-progress - the Home screen's Continue Playing
// shelf, kept by the server so an album put down on the phone can be picked
// up on the desktop at the same place.
//
// One exchange rather than a push and a pull: the client sends everything it
// holds, the server merges it into its own copy and answers with the result,
// and the client merges that back. Both merges are the same rule
// (AlbumProgressMerge), so the exchange is idempotent - a retry after a lost
// response, or two devices exchanging at once, converges instead of
// double-applying anything.
//
// One shelf per listener rather than one per library. Flower has devices, not
// accounts, so "listener" is the same line the rest of the protocol draws: the
// owner's devices - the admins - share one shelf, and every other device keeps
// a shelf of its own on the server. A housemate's phone playing an album must
// not move the owner's cassette, and the owner's phone and desktop are the
// pair this exists to join up. See SyncEndpoints.ExchangeAlbumProgress.
public static class AlbumProgressProtocol
{
    public const string Path = "/api/flower/v1/album-progress";

    // More than any shelf holds (clients keep 30 live albums and a bounded
    // number of removals), so only a malformed or hostile body is refused.
    public const int MaxEntries = 500;
}

// One album on the shelf, or its removal. AlbumId is CatalogIdentity's
// (album artist + album), which every host computes identically from the same
// tags. TrackId is the server's id for the song the tape is at
// (Track.OriginTrackId on a client), and null for an album taken off the shelf
// - finished, or removed by hand - which is kept as a dated marker so the
// removal reaches devices that still have it rather than being put back by
// the next of them to exchange.
public sealed record AlbumProgressDto(
    string AlbumId,
    string? TrackId,
    double PositionSeconds,
    DateTimeOffset UpdatedAt)
{
    public bool IsRemoval => TrackId == null;
}

public sealed record AlbumProgressExchangeDto(List<AlbumProgressDto> Albums);

// Last write wins, per album: whichever side touched an album most recently -
// moved the tape, or took it off the shelf - says where it is. Ties keep the
// side merged into, so merging something with itself changes nothing.
public static class AlbumProgressMerge
{
    // How long a removal is remembered. Long enough for a device left in a
    // drawer to come back and hear about it; past that, an album it still
    // holds reappears, which costs one tile.
    public static readonly TimeSpan RemovalLifetime = TimeSpan.FromDays(90);

    public const int MaxLiveAlbums = 30;
    public const int MaxRemovals = 200;

    public static List<T> Newest<T>(
        IEnumerable<T> into, IEnumerable<T> from, Func<T, string> albumOf, Func<T, DateTimeOffset> at)
    {
        var merged = new Dictionary<string, T>();
        foreach (var item in into)
            merged[albumOf(item)] = item;

        foreach (var item in from)
        {
            var album = albumOf(item);
            if (!merged.TryGetValue(album, out var existing) || at(item) > at(existing))
                merged[album] = item;
        }

        return merged.Values.OrderByDescending(at).ToList();
    }

    // The server's copy, after a merge: the most recent albums and removals,
    // each bounded, and removals only for as long as they are worth carrying.
    public static List<AlbumProgressDto> Prune(IEnumerable<AlbumProgressDto> albums, DateTimeOffset now)
    {
        var ordered = albums.OrderByDescending(a => a.UpdatedAt).ToList();
        var live = ordered.Where(a => !a.IsRemoval).Take(MaxLiveAlbums);
        var removals = ordered
            .Where(a => a.IsRemoval && now - a.UpdatedAt < RemovalLifetime)
            .Take(MaxRemovals);

        return live.Concat(removals).OrderByDescending(a => a.UpdatedAt).ToList();
    }
}
