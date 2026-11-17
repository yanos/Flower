using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;

using Flower.Models;
using Flower.Services;
using Flower.ViewModels;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Flower.Tests.ViewModels;

// A tile or a row keeps its cover only while something draws it. The grids
// keep one tile per album and the lists one row per song, and each kept its
// cover for as long as it lived: one fling down a phone's Albums tab held a
// cover for every album scrolled past, 2.6 GB on an iPhone.
[Collection("AlbumArtLoader")]
public class ShownArtTests : IDisposable
{
    // Hands out a fresh bitmap per load and keeps none of them, as the real
    // loader's caches would once they had moved on.
    private sealed class ForgetfulArtLoader : AlbumArtLoader
    {
        public ForgetfulArtLoader() : base(null, null, NullLogger<AlbumArtLoader>.Instance)
        {
        }

        public int Loads { get; private set; }

        public override Task<Bitmap?> LoadAsync(Track track) => Load();
        public override Task<Bitmap?> LoadThumbnailAsync(Track track) => Load();

        private Task<Bitmap?> Load()
        {
            Loads++;
            return Task.FromResult<Bitmap?>(new RenderTargetBitmap(new PixelSize(4, 4)));
        }
    }

    public enum Shows
    {
        Tile,
        Row,
        ExpandedRow,
    }

    private readonly AlbumArtLoader _previousLoader = AlbumArtLoader.Current;
    private readonly ForgetfulArtLoader _art = new();

    public ShownArtTests() => AlbumArtLoader.Current = _art;

    public void Dispose() => AlbumArtLoader.Current = _previousLoader;

    private static Func<Bitmap?> Showing(Shows shows)
    {
        var track = new Track { Title = "Song", Album = "Album", Artists = "Artist", Path = "/music/x.mp3" };
        switch (shows)
        {
            case Shows.Tile:
            {
                var tile = AlbumGridBuilder.Build([track])[0];
                return () => tile.AlbumArt;
            }
            case Shows.Row:
            {
                var row = new TrackRowViewModel { Track = track };
                return () => row.AlbumArt;
            }
            default:
            {
                var row = new ExpandedTrackRowViewModel { Track = track, ShowsArt = true };
                return () => row.AlbumArt;
            }
        }
    }

    // Read in a frame of its own, so nothing on the test's stack still holds
    // the bitmap when it collects.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<Bitmap> Drawn(Func<Bitmap?> art) => new(art()!);

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    [AvaloniaTheory]
    [InlineData(Shows.Tile)]
    [InlineData(Shows.Row)]
    [InlineData(Shows.ExpandedRow)]
    public void A_cover_nothing_draws_is_let_go(Shows shows)
    {
        var art = Showing(shows);

        var drawn = Drawn(art);
        Collect();

        Assert.False(drawn.TryGetTarget(out _));
        GC.KeepAlive(art);
    }

    [AvaloniaTheory]
    [InlineData(Shows.Tile)]
    [InlineData(Shows.Row)]
    [InlineData(Shows.ExpandedRow)]
    public void A_cover_let_go_is_loaded_again_when_drawn_again(Shows shows)
    {
        var art = Showing(shows);
        Drawn(art);
        Collect();

        Assert.NotNull(art());
        Assert.Equal(2, _art.Loads);
    }

    [AvaloniaTheory]
    [InlineData(Shows.Tile)]
    [InlineData(Shows.Row)]
    [InlineData(Shows.ExpandedRow)]
    public void A_cover_still_drawn_is_kept(Shows shows)
    {
        var art = Showing(shows);
        var onScreen = new List<Bitmap?> { art() };
        Collect();

        Assert.Same(onScreen[0], art());
        Assert.Equal(1, _art.Loads);
    }
}
