using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Media.Imaging;

using CommunityToolkit.Mvvm.ComponentModel;

using Flower.Models;
using Flower.Services;

namespace Flower.ViewModels.Mobile;

// One row of mobile's Artists tab: the artist, how much of theirs there is,
// and a cover made of their most played albums' art (see LoadCoversAsync, and
// MobileMainViewModel.RebuildArtistPickerRows).
public sealed class ArtistPickerRow : ObservableObject
{
    public ArtistPickerRow(string name, int albumCount, int songCount, IReadOnlyList<Track> coverTracks)
    {
        Name = name;
        AlbumCount = albumCount;
        SongCount = songCount;
        CoverTracks = coverTracks;
    }

    public string Name { get; }
    public int AlbumCount { get; }
    public int SongCount { get; }

    // One track per album, whose art stands for that album, most played
    // first - every album the cover could be made of (see AlbumsByPlays).
    public IReadOnlyList<Track> CoverTracks { get; }

    // The line under the name, rather than a bare number beside it that could
    // be counting anything.
    public string SummaryText => Summary(AlbumCount, SongCount);

    public static string Summary(int albums, int songs)
    {
        var albumText = albums switch
        {
            0 => "No albums",
            1 => "1 album",
            _ => $"{albums} albums",
        };
        return $"{albumText} · {songs} {(songs == 1 ? "song" : "songs")}";
    }

    // An artist's albums, most played first: every play of every song on
    // one, wherever it was played (Track.TotalPlayCount). A library nobody has
    // played yet falls back to the most recently added, the same album the
    // artist's own menu shows.
    //
    // Albums are grouped by name alone, the way the artist's grid groups them,
    // and each one is represented by its most recently added song, whose art
    // is the album's on its tile too.
    public static IReadOnlyList<Track> AlbumsByPlays(IEnumerable<Track> artistTracks) =>
        artistTracks
            .Where(t => !string.IsNullOrEmpty(t.Album))
            .GroupBy(t => t.Album!)
            .Select(g => (
                Plays: g.Sum(t => t.TotalPlayCount),
                Added: g.Max(t => t.DateAdded),
                Name: g.Key,
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
    // a hole: an artist with five albums, two of them bare, gets a diagonal.
    public static int CoverCountFor(int albumsWithArt) =>
        albumsWithArt >= 4 ? 4 : Math.Min(albumsWithArt, 2);

    // The art of the most played albums that have any, as many as
    // CoverCountFor allows. Which albums have art is only known once it is
    // loaded - embedded art means reading the file - so it is loaded in play
    // order, a round at a time, each round asking for as many as are still
    // missing, until four are in or the albums run out. Most artists need one
    // round; it is one request a round against a server, since AlbumArtLoader
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
        var art = await LoadCoversAsync(CoverTracks);
        Volatile.Write(ref _artCacheGeneration, cacheGeneration);
        Interlocked.Exchange(ref _artState, 2);
        CoverArt = art;
    }
}
