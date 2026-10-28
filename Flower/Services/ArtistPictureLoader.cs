using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

using Avalonia.Media.Imaging;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Flower.Models;

namespace Flower.Services;

// An artist's picture as something to draw (see AlbumCollage.ForArtist):
// asked of IOnlineMetadata, decoded the way a cover is, and kept while
// anything still draws it. Null for an artist with no picture, which the
// collage of their covers then stands in for.
//
// Static Current for the same reason AlbumArtLoader has one: AlbumCollage is
// built by static builders that have no container to inject through. The
// fallback has no IOnlineMetadata and finds no pictures, which is what a head
// or a test that never registers one should get.
public sealed class ArtistPictureLoader(IOnlineMetadata? online, ILogger<ArtistPictureLoader> logger)
{
    private static ArtistPictureLoader? _current;
    public static ArtistPictureLoader Current
    {
        get => _current ??= new ArtistPictureLoader(null, NullLogger<ArtistPictureLoader>.Instance);
        set => _current = value;
    }

    // Weakly, as AlbumArtLoader keeps covers: whatever is drawing a picture
    // holds it, and one nothing draws any more is decoded again from the disk
    // cache - which is cheap - rather than held for a list of every artist.
    private readonly ConcurrentDictionary<string, WeakReference<Bitmap>> _decoded = new(StringComparer.Ordinal);

    // An artist's row and their page asking at once share one answer.
    // Lazily, so the asking starts only once it is in the table and its
    // removal when done cannot come first.
    private readonly ConcurrentDictionary<string, Lazy<Task<byte[]?>>> _asking = new(StringComparer.Ordinal);

    // thumbnail: for a row, at a row's size; otherwise for a page header.
    public async Task<Bitmap?> LoadAsync(string artist, IReadOnlyList<Track> tracks, bool thumbnail)
    {
        if (online == null)
            return null;

        var name = MetadataNames.Normalize(artist);
        if (name.Length == 0)
            return null;

        var key = (thumbnail ? "thumb|" : "") + name;
        if (_decoded.TryGetValue(key, out var weak) && weak.TryGetTarget(out var cached))
            return cached;

        var bytes = await _asking.GetOrAdd(name, _ => new Lazy<Task<byte[]?>>(() => AskAsync(name, artist, tracks))).Value;
        if (bytes is not { Length: > 0 })
            return null;

        var bitmap = await Task.Run(() => AlbumArtLoader.TryDecodePicture(bytes, thumbnail));
        if (bitmap == null)
        {
            logger.LogDebug("The picture of {Artist} would not decode; drawing their covers instead", artist);
            return null;
        }

        _decoded[key] = new WeakReference<Bitmap>(bitmap);
        return bitmap;
    }

    // Off the UI thread, where the rows ask from: the first thing either
    // implementation does is read its cache from disk.
    private async Task<byte[]?> AskAsync(string name, string artist, IReadOnlyList<Track> tracks)
    {
        try
        {
            return await Task.Run(() => online!.ArtistPictureAsync(artist, tracks));
        }
        finally
        {
            _asking.TryRemove(name, out _);
        }
    }
}
