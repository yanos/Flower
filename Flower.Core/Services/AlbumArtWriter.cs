using System;

using Microsoft.Extensions.Logging;

namespace Flower.Services;

// The write half of LocalAlbumArtReader: putting a picture into a track file's
// own tag, and taking it back out again.
//
// In Flower.Core, and shared, for the same reason the reader is: both the app
// (Track Info's Artwork tab, writing into a local file) and Flower.Server (the
// admin cover-art route, writing into a file the app can only reach over the
// network) do exactly this, and a second copy of "replace Tag.Pictures, then
// Save" would be a second place to get the picture type or the replace-versus-
// append decision wrong.
public static class AlbumArtWriter
{
    // Replaces whatever the tag already carries rather than appending: the read
    // side takes the *first* picture in the tag, so a file with a front cover
    // plus three stray pictures would otherwise keep showing the old one.
    //
    // Note what this does not touch. A cover.jpg sitting beside the track is a
    // separate thing on disk and is what LocalAlbumArtReader falls back to; it
    // now loses to the embedded picture written here, which is the intended
    // outcome, but it is still there. Removing the embedded picture therefore
    // reveals it again rather than leaving the album blank.
    public static bool TryWrite(string path, byte[] bytes, string mimeType, ILogger? logger = null)
    {
        try
        {
            using var tagFile = TagLib.File.Create(path);
            tagFile.Tag.Pictures =
            [
                new TagLib.Picture(new TagLib.ByteVector(bytes))
                {
                    Type = TagLib.PictureType.FrontCover,
                    MimeType = mimeType,
                    Description = "Cover",
                },
            ];
            tagFile.Save();
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not write album art into {Path}", path);
            return false;
        }
    }

    // Drops every picture, not just the front cover: the reader takes the first
    // picture of any type, so leaving a back cover or a band photo behind would
    // read as "the remove did nothing" rather than as a partial success.
    public static bool TryRemove(string path, ILogger? logger = null)
    {
        try
        {
            using var tagFile = TagLib.File.Create(path);
            if (tagFile.Tag.Pictures.Length == 0)
                return true;

            tagFile.Tag.Pictures = [];
            tagFile.Save();
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not remove the album art from {Path}", path);
            return false;
        }
    }
}
