using System.Collections.Generic;
using System.IO;

using Flower.Models;

namespace Flower.Services;

// How big a song's picture is - Track.ArtWidth and ArtHeight, measured. Every
// place that changes what a file's picture is asks this afterwards: a scan,
// Track Info replacing or removing a cover, a picture arriving by sync, and the
// server's two artwork routes. Library itself never reads a file, so it is
// told rather than asking.
//
// Read from the image header alone (ImageHeader), never by decoding. A picture
// whose header is not one of the formats that reads counts as none - which,
// with GIF and BMP read too, leaves only TIFF and a corrupt picture, and a
// corrupt one is drawn as the placeholder everywhere anyway.
public readonly record struct ArtworkDimensions(int Width, int Height)
{
    public static readonly ArtworkDimensions None = default;

    public bool IsNone => Width <= 0 || Height <= 0;

    public static ArtworkDimensions Of(LocalAlbumArt? art) =>
        art != null && ImageHeader.TryReadSize(art.Bytes) is { } size ? new(size.Width, size.Height) : None;

    public static ArtworkDimensions Of(byte[] bytes) =>
        ImageHeader.TryReadSize(bytes) is { } size ? new(size.Width, size.Height) : None;

    // What the app would draw for this file: its own picture, or a cover file
    // beside it.
    public static ArtworkDimensions OfFile(string path) => Of(LocalAlbumArtReader.ForFile(path));

    // The scan's half, for a file whose tags are already open. coverFiles holds
    // the answer for each folder a scan has already looked in, so an album
    // with no embedded art costs one listing of its folder rather than one per
    // song; null for a caller reading a single file.
    public static ArtworkDimensions Read(
        TagLib.File tagFile, string path, IDictionary<string, ArtworkDimensions>? coverFiles = null)
    {
        if (LocalAlbumArtReader.PictureIn(tagFile) is { } embedded)
            return Of(embedded);

        if (Path.GetDirectoryName(path) is not { } directory)
            return None;

        if (coverFiles != null && coverFiles.TryGetValue(directory, out var known))
            return known;

        var measured = Of(LocalAlbumArtReader.CoverFileIn(directory));
        if (coverFiles != null)
            coverFiles[directory] = measured;
        return measured;
    }

    public static ArtworkDimensions Of(Track track) => new(track.ArtWidth, track.ArtHeight);

    public void ApplyTo(Track track)
    {
        track.ArtWidth = IsNone ? 0 : Width;
        track.ArtHeight = IsNone ? 0 : Height;
    }
}
