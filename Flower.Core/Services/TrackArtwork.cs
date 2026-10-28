using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.Extensions.Logging;

using Flower.Models;

namespace Flower.Services;

// PUT and DELETE /api/admin/library/artwork - the answer. NotWritten are the
// songs whose file the server could not write the picture into; those did not
// happen there and are worth sending again. Every other song is settled,
// applied or not (one the server already has newer artwork for is settled
// too). LibraryToken is the catalog token the change produced - see
// LibraryUploadStatusDto.LibraryToken.
public sealed record LibraryArtEditResponseDto(int Applied, List<string> NotWritten, string? LibraryToken = null);

// A server applying artwork an owner's device changed - the artwork twin of
// TrackTags.ApplyEdits, and in Flower.Core for the same reason: the real
// server and SimulatedFlowerServer answer through this.
//
// The picture goes into the files of the songs named, one by one, and those
// songs are dated (Track.ArtEditedAt) so that every other device goes and
// fetches it. Addressed per song rather than per album, unlike the older
// PUT /api/admin/cover-art beside it: this route mirrors what a device did to
// its own files, and a device changes the files it was told to.
public static class TrackArtwork
{
    // art null removes the picture. placement is where the device that made
    // the change keeps it, which the server keeps it the same way.
    public static LibraryArtEditResponseDto ApplyEdit(
        Library library, IReadOnlyList<string> trackIds, DateTimeOffset editedAt, LocalAlbumArt? art, ILogger logger,
        ArtworkPlacement placement = ArtworkPlacement.Files)
    {
        // The newest change wins, as it does for tags.
        var named = trackIds
            .Select(id => (Id: id, Track: library.Find(id)))
            .Where(x => x.Track is { Path: not null } && !(x.Track.ArtEditedAt is { } known && known >= editedAt))
            .ToList();

        var results = AlbumArtWriter.Apply(library, [.. named.Select(x => x.Track!)], art, placement, logger);
        var notWritten = results.Where(r => !r.Written).Select(r => r.Track).ToHashSet();

        library.ApplySyncedArt([.. results
            .Where(r => r.Written)
            .Select(r => (r.Track, editedAt, (string?)null, ArtworkDimensions.OfFile(r.Track.Path!)))]);
        return new LibraryArtEditResponseDto(
            results.Count(r => r.Written),
            [.. named.Where(x => notWritten.Contains(x.Track!)).Select(x => x.Id)],
            library.ChangeToken);
    }

    // Whether two copies of a song carry the same picture: both none, or the
    // same bytes.
    public static bool Same(LocalAlbumArt? a, LocalAlbumArt? b) =>
        a == null || b == null ? a == null && b == null : a.Bytes.AsSpan().SequenceEqual(b.Bytes);
}
