using System;
using System.Collections.Generic;

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
    // art null removes the picture.
    public static LibraryArtEditResponseDto ApplyEdit(
        Library library, IReadOnlyList<string> trackIds, DateTimeOffset editedAt, LocalAlbumArt? art, ILogger logger)
    {
        var applied = new List<(Track Track, DateTimeOffset EditedAt, string? FileStamp)>();
        var notWritten = new List<string>();
        foreach (var id in trackIds)
        {
            if (library.Find(id) is not { Path: { } path } track)
                continue;

            // The newest change wins, as it does for tags.
            if (track.ArtEditedAt is { } known && known >= editedAt)
                continue;

            var written = art != null
                ? AlbumArtWriter.TryWrite(path, art.Bytes, art.MimeType, logger)
                : AlbumArtWriter.TryRemove(path, logger);
            if (written)
                applied.Add((track, editedAt, null));
            else
                notWritten.Add(id);
        }

        library.ApplySyncedArt(applied);
        return new LibraryArtEditResponseDto(applied.Count, notWritten, library.ChangeToken);
    }

    // Whether two copies of a song carry the same picture: both none, or the
    // same bytes.
    public static bool Same(LocalAlbumArt? a, LocalAlbumArt? b) =>
        a == null || b == null ? a == null && b == null : a.Bytes.AsSpan().SequenceEqual(b.Bytes);
}
