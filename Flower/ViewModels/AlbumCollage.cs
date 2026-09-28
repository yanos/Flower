using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Media.Imaging;

using CommunityToolkit.Mvvm.ComponentModel;

using Flower.Models;
using Flower.Services;

namespace Flower.ViewModels;

// A cover for something that holds several albums - an artist, a playlist -
// made of the art of the most played of them (see AlbumCollageView for how
// it is drawn). Built from the songs it stands for; the art is only read once
// something binds CoverArt.
public sealed class AlbumCollage : ObservableObject
{
    // Albums by name alone, as an artist's own grid groups them - one artist's
    // album is one album whoever the album artist says it is.
    public static AlbumCollage ByAlbumName(IEnumerable<Track> tracks) =>
        new(AlbumsByPlays(tracks, t => t.Album!));

    // Albums by name and album artist, as the Albums grid groups them - a
    // playlist can hold two different albums called Greatest Hits.
    public static AlbumCollage ByAlbumAndArtist(IEnumerable<Track> tracks) =>
        new(AlbumsByPlays(tracks, t => (t.Album!, t.EffectiveAlbumArtist)));

    public static readonly AlbumCollage Empty = new([]);

    private AlbumCollage(IReadOnlyList<Track> albumsByPlays)
    {
        AlbumsInPlayOrder = albumsByPlays;
    }

    // One track per album, whose art stands for that album, most played
    // first - every album the cover could be made of.
    public IReadOnlyList<Track> AlbumsInPlayOrder { get; }

    // Every play of every song on an album, wherever it was played
    // (Track.TotalPlayCount), and only the songs this collage stands for. A
    // library nobody has played yet falls back to the most recently added.
    // Each album is represented by its most recently added song, whose art is
    // the album's on its tile too.
    public static IReadOnlyList<Track> AlbumsByPlays<TKey>(IEnumerable<Track> tracks, Func<Track, TKey> album) =>
        tracks
            .Where(t => !string.IsNullOrEmpty(t.Album))
            .GroupBy(album)
            .Select(g => (
                Plays: g.Sum(t => t.TotalPlayCount),
                Added: g.Max(t => t.DateAdded),
                Name: g.First().Album!,
                Cover: g.MaxBy(t => t.DateAdded)!))
            .OrderByDescending(a => a.Plays)
            .ThenByDescending(a => a.Added)
            .ThenBy(a => a.Name, StringComparer.Ordinal)
            .Select(a => a.Cover)
            .ToList();

    // How many covers the collage shows, given how many albums have one: the
    // four most played two by two, the top two halved along the diagonal when
    // there are only two or three - a 2x2 with a gap in it, or the same cover
    // twice, reads as a mistake - and the one when there is one. Counted over
    // albums with art rather than albums, so an album with none never leaves
    // a hole: five albums, two of them bare, make a diagonal.
    public static int CoverCountFor(int albumsWithArt) =>
        albumsWithArt >= 4 ? 4 : Math.Min(albumsWithArt, 2);

    // The art of the most played albums that have any, as many as
    // CoverCountFor allows. Which albums have art is only known once it is
    // loaded - embedded art means reading the file - so it is loaded in play
    // order, a round at a time, each round asking for as many as are still
    // missing, until four are in or the albums run out. Most need one round;
    // it is one request a round against a server, since AlbumArtLoader
    // batches whatever is asked for together.
    public static async Task<IReadOnlyList<Bitmap>> LoadCoversAsync(IReadOnlyList<Track> albumsByPlays)
    {
        var found = new List<Bitmap>();
        var next = 0;
        while (found.Count < 4 && next < albumsByPlays.Count)
        {
            var round = albumsByPlays.Skip(next).Take(4 - found.Count).ToList();
            next += round.Count;
            var art = await Task.WhenAll(round.Select(t => AlbumArtLoader.Current.LoadAsync(t)));
            found.AddRange(art.OfType<Bitmap>());
        }
        return found.Take(CoverCountFor(found.Count)).ToList();
    }

    // Same lazy-load-on-first-bind as AlbumTileViewModel.AlbumArt, for every
    // cover at once: the collage is drawn when all of them are in, rather than
    // redrawn as each one lands.
    private IReadOnlyList<Bitmap>? _coverArt;
    private int _artState; // 0=idle, 1=loading, 2=done
    private int _artCacheGeneration;

    public IReadOnlyList<Bitmap>? CoverArt
    {
        get
        {
            // Art replaced on disk - see TrackRowViewModel.AlbumArt.
            if (Volatile.Read(ref _artState) == 2 && Volatile.Read(ref _artCacheGeneration) != AlbumArtLoader.CacheGeneration)
                Interlocked.Exchange(ref _artState, 0);

            if (Interlocked.CompareExchange(ref _artState, 1, 0) == 0)
                _ = LoadArtAsync();
            return _coverArt;
        }
        private set => SetProperty(ref _coverArt, value);
    }

    private async Task LoadArtAsync()
    {
        var cacheGeneration = AlbumArtLoader.CacheGeneration;
        var art = await LoadCoversAsync(AlbumsInPlayOrder);
        Volatile.Write(ref _artCacheGeneration, cacheGeneration);
        Interlocked.Exchange(ref _artState, 2);
        CoverArt = art;
    }
}
