using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;

using Flower.Models;
using Flower.Services;
using Flower.ViewModels.Mobile;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Flower.Tests;

// The cover on an Artists tab row (ArtistPickerRow.LoadCoversAsync): the most
// played of the artist's albums that have art, four of them when there are
// four, the top two split along the diagonal when there are two or three, and
// the one when there is one.
//
// In the AlbumArtLoader collection because it installs its own loader - see
// AlbumTileMergeTests.
[Collection("AlbumArtLoader")]
public class ArtistCoverTests : IDisposable
{
    // Art for every album but the ones named bare, one bitmap per album so a
    // test can tell which album a cover came from.
    private sealed class FakeArtLoader(HashSet<string> bare) : AlbumArtLoader(null, null, NullLogger<AlbumArtLoader>.Instance)
    {
        public readonly Dictionary<Bitmap, string> AlbumOf = new();
        public int Loads { get; private set; }

        public override Task<Bitmap?> LoadAsync(Track track)
        {
            Loads++;
            if (bare.Contains(track.Album!))
                return Task.FromResult<Bitmap?>(null);
            var bitmap = new RenderTargetBitmap(new PixelSize(4, 4));
            AlbumOf[bitmap] = track.Album!;
            return Task.FromResult<Bitmap?>(bitmap);
        }
    }

    private readonly AlbumArtLoader _previousLoader = AlbumArtLoader.Current;
    public void Dispose() => AlbumArtLoader.Current = _previousLoader;

    // Album "A" played least, then "B", and so on - each over two songs, so
    // what counts is the album's plays rather than one song's.
    private static List<Track> Albums(int count) =>
        Enumerable.Range(0, count)
            .SelectMany(i => new[]
            {
                new Track { Title = "one", Album = ((char)('A' + i)).ToString(), Artists = "X", PlayCount = i },
                new Track { Title = "two", Album = ((char)('A' + i)).ToString(), Artists = "X", ImportedPlayCount = 1 },
            })
            .ToList();

    private static async Task<(string[] Albums, int Loads)> CoverOf(List<Track> tracks, params string[] bare)
    {
        var loader = new FakeArtLoader(bare.ToHashSet());
        AlbumArtLoader.Current = loader;
        var covers = await ArtistPickerRow.LoadCoversAsync(ArtistPickerRow.AlbumsByPlays(tracks));
        return (covers.Select(c => loader.AlbumOf[c]).ToArray(), loader.Loads);
    }

    [AvaloniaTheory]
    [InlineData(1, new[] { "A" })]
    [InlineData(2, new[] { "B", "A" })]
    [InlineData(3, new[] { "C", "B" })]
    [InlineData(4, new[] { "D", "C", "B", "A" })]
    [InlineData(6, new[] { "F", "E", "D", "C" })]
    public async Task An_artists_cover_is_made_of_their_most_played_albums(int albums, string[] expected)
    {
        var (covers, _) = await CoverOf(Albums(albums));
        Assert.Equal(expected, covers);
    }

    // Counted over the albums that have art: five albums with two of them
    // bare is three to choose from, which is a diagonal rather than a grid
    // with a hole in it - and a bare album is passed over, not left blank.
    [AvaloniaFact]
    public async Task Albums_without_art_are_not_counted()
    {
        var (covers, _) = await CoverOf(Albums(5), "E", "C");
        Assert.Equal(["D", "B"], covers);
    }

    [AvaloniaFact]
    public async Task Bare_albums_are_passed_over_until_four_with_art_are_found()
    {
        var (covers, _) = await CoverOf(Albums(7), "G", "E");
        Assert.Equal(["F", "D", "C", "B"], covers);
    }

    // Four albums with art in the first four asked for is all it takes: an
    // artist's whole catalog is not loaded to draw one row.
    [AvaloniaFact]
    public async Task Only_as_many_albums_are_loaded_as_it_takes()
    {
        var (_, loads) = await CoverOf(Albums(10));
        Assert.Equal(4, loads);
    }

    [AvaloniaFact]
    public async Task An_artist_whose_albums_have_no_art_has_no_cover()
    {
        var (covers, _) = await CoverOf(Albums(2), "A", "B");
        Assert.Empty(covers);
    }

    [AvaloniaFact]
    public async Task An_artist_with_no_albums_has_no_cover()
    {
        var (covers, loads) = await CoverOf([new Track { Title = "Loose", Artists = "X" }]);
        Assert.Empty(covers);
        Assert.Equal(0, loads);
    }
}
