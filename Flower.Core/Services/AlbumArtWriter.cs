using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.Extensions.Logging;

using Flower.Models;

namespace Flower.Services;

// Where a changed cover goes: into each song's file, or into its album's
// folder as cover.jpg (or .png, ...), which every song there without a picture
// of its own is drawn with (LocalAlbumArtReader.FirstFor). On the wire as the
// lowercase name, ?to=folder.
public enum ArtworkPlacement
{
    Files,
    Folder,
}

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
    // A cover changed for these songs - the one way every caller changes one,
    // so a song's file and an album's folder are written alike everywhere:
    // Track Info on this device, and the server for a song it holds. art null
    // removes the cover. Each song whose cover this changed comes back with
    // whether it took; with Folder that is every song in the folder, since the
    // cover file is theirs too.
    //
    // A picture in a song's own file wins over the folder's, so putting one in
    // the folder takes the songs' own out - otherwise the change would not
    // show - and removing one there takes out both. And a folder only gets
    // the cover when every song in it is from these songs' albums: a folder of
    // loose songs from several albums would lend one album's cover to them
    // all, so those songs get theirs in their files instead.
    public static List<(Track Track, bool Written)> Apply(
        Library library, IReadOnlyList<Track> tracks, LocalAlbumArt? art, ArtworkPlacement placement, ILogger? logger = null)
    {
        var results = new List<(Track Track, bool Written)>();
        var handled = new HashSet<string>(StringComparer.Ordinal);

        if (placement == ArtworkPlacement.Folder)
        {
            var albums = tracks.Where(t => !string.IsNullOrEmpty(t.Album))
                .Select(CatalogIdentity.AlbumIdFor).ToHashSet(StringComparer.Ordinal);
            var folders = tracks.Where(t => t.Path is { Length: > 0 })
                .Select(t => Path.GetDirectoryName(t.Path!)).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var songsIn = library.Tracks.Concat(tracks)
                .Where(t => t.Path is { Length: > 0 } path && Path.GetDirectoryName(path) is { } folder && folders.Contains(folder))
                .DistinctBy(t => t.Path, StringComparer.Ordinal)
                .GroupBy(t => Path.GetDirectoryName(t.Path!)!, StringComparer.Ordinal);

            foreach (var songs in songsIn)
            {
                if (!songs.All(t => !string.IsNullOrEmpty(t.Album) && albums.Contains(CatalogIdentity.AlbumIdFor(t))))
                    continue;

                var folderWritten = art != null
                    ? TryWriteCoverFile(songs.Key, art, logger)
                    : TryRemoveCoverFiles(songs.Key, logger);
                foreach (var song in songs)
                {
                    results.Add((song, folderWritten && TryRemove(song.Path!, logger)));
                    handled.Add(song.Path!);
                }
            }
        }

        foreach (var track in tracks)
        {
            if (track.Path is not { Length: > 0 } path || !handled.Add(path))
                continue;

            results.Add((track, art != null ? TryWrite(path, art.Bytes, art.MimeType, logger) : TryRemove(path, logger)));
        }

        return results;
    }

    // The folder's cover, as cover.<its type>, in place of any cover.* or
    // folder.* already there - the reader takes the first of those it lists,
    // so one left behind could still be the one drawn.
    public static bool TryWriteCoverFile(string folder, LocalAlbumArt art, ILogger? logger = null)
    {
        try
        {
            var target = Path.Combine(folder, "cover" + LocalAlbumArtReader.ExtensionForMimeType(art.MimeType));
            foreach (var old in CoverFiles(folder).Where(f => !f.Equals(target, StringComparison.Ordinal)))
                File.Delete(old);
            File.WriteAllBytes(target, art.Bytes);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not write the album art into {Folder}", folder);
            return false;
        }
    }

    public static bool TryRemoveCoverFiles(string folder, ILogger? logger = null)
    {
        try
        {
            foreach (var old in CoverFiles(folder))
                File.Delete(old);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not remove the album art from {Folder}", folder);
            return false;
        }
    }

    private static List<string> CoverFiles(string folder) =>
        [.. Directory.EnumerateFiles(folder).Where(LocalAlbumArtReader.IsCoverFile)];

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
