using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Media.Imaging;

using Flower.Models;
using Flower.Services;
using Flower.ViewModels.Mobile;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Flower.Tests;

// Tile reuse across a grid rebuild - TrackRowMergeTests' counterpart, and for
// the same reason the row version exists: a rebuild that replaces the instance
// on screen loses whatever transient state it was holding. On a tile that is
// most visibly the album download button's own spinner, and the rebuild is
// triggered by the very download it is animating.
public class AlbumTileMergeTests : IDisposable
{
    private sealed class CountingArtLoader : AlbumArtLoader
    {
        public CountingArtLoader() : base(null, null, NullLogger<AlbumArtLoader>.Instance) { }
        public int Loads { get; private set; }
        public override Task<Bitmap?> LoadAsync(Track track)
        {
            Loads++;
            return Task.FromResult<Bitmap?>(null);
        }
    }

    private readonly AlbumArtLoader _previousLoader = AlbumArtLoader.Current;
    private readonly CountingArtLoader _art = new();

    public AlbumTileMergeTests() => AlbumArtLoader.Current = _art;
    public void Dispose() => AlbumArtLoader.Current = _previousLoader;

    private static Track T(string album, string artist = "Artist", string? path = "/music/x.mp3", string title = "Song") =>
        new() { Title = title, Album = album, Artists = artist, Path = path, DateAdded = DateTimeOffset.UnixEpoch };

    private static List<AlbumTileViewModel> Build(IEnumerable<Track> tracks, IReadOnlyList<AlbumTileViewModel>? previous, out List<AlbumTileViewModel> retired) =>
        AlbumTileMerge.Apply(previous, AlbumGridBuilder.Build(tracks), out retired);

    [Fact]
    public void A_rebuild_reuses_the_tile_showing_the_same_album()
    {
        var first = Build([T("Album")], null, out _);

        var second = Build([T("Album")], first, out var retired);

        Assert.Same(first[0], second[0]);
        Assert.Empty(retired);
    }

    // The reported bug: clicking an album's download button starts a batch, the
    // first track to land fires TracksUpdated, and the rebuild that follows used
    // to swap in a fresh tile with IsDownloading false - the spinner reverting to
    // the download icon while the download was still running.
    [Fact]
    public void A_tile_downloading_keeps_its_spinner_across_the_rebuild_its_download_triggers()
    {
        var first = Build([T("Album", path: null)], null, out _);
        first[0].IsDownloading = true;

        var second = Build([T("Album", path: null)], first, out _);

        Assert.True(second[0].IsDownloading);
        Assert.False(second[0].IsDownloadIdle);

        // Still spinning is the whole point, so nothing here has stopped the
        // animation clock's timer - and a subscription left running past the
        // end of a test is exactly what HeadlessSessionWarmup fails the run
        // over. Dispose stops it, the same way a retired tile is disposed.
        second[0].Dispose();
    }

    [Fact]
    public void A_reused_tile_takes_the_new_tracks_and_representative()
    {
        var first = Build([T("Album", title: "One")], null, out _);

        var rescanned = T("Album", title: "One");
        var second = Build([rescanned], first, out _);

        Assert.Same(rescanned, second[0].RepresentativeTrack);
        Assert.Same(rescanned, Assert.Single(second[0].Tracks));
    }

    [Fact]
    public void A_reused_tile_keeps_the_art_it_already_loaded()
    {
        var first = Build([T("Album")], null, out _);
        _ = first[0].AlbumArt;
        Assert.Equal(1, _art.Loads);

        var second = Build([T("Album")], first, out _);
        _ = second[0].AlbumArt;

        Assert.Equal(1, _art.Loads);
    }

    // ...but not when the art would now come from somewhere else - here a
    // placeholder that has since been downloaded and has a real file to read.
    [Fact]
    public void A_reused_tile_reloads_art_when_its_representative_track_gains_a_file()
    {
        var first = Build([T("Album", path: null)], null, out _);
        _ = first[0].AlbumArt;
        Assert.Equal(1, _art.Loads);

        var second = Build([T("Album", path: "/music/downloaded.mp3")], first, out _);
        _ = second[0].AlbumArt;

        Assert.Equal(2, _art.Loads);
    }

    [Fact]
    public void An_album_that_no_longer_exists_is_retired_for_the_caller_to_dispose()
    {
        var first = Build([T("Gone"), T("Kept")], null, out _);
        var gone = first.Single(t => t.Name == "Gone");

        var second = Build([T("Kept")], first, out var retired);

        Assert.Same(gone, Assert.Single(retired));
        Assert.DoesNotContain(gone, second);
    }
}
