using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

using Flower.Models;
using Flower.Services;
using Flower.ViewModels;
using Flower.ViewModels.Mobile;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Flower.Tests.ViewModels;

// A cover replaced in Track Info, and the album it belongs to already on
// screen behind it. The tiles and rows reload when their art is read after
// the replacement, but a view reads it only when told it changed - so a
// screen kept alive under Track Info went on showing the old cover when the
// phone went back to it.
[Collection("AlbumArtLoader")]
public class AlbumArtReplacementTests : IDisposable
{
    private sealed class CountingArtLoader(IReadOnlySet<Guid> ours) : AlbumArtLoader(null, null, NullLogger<AlbumArtLoader>.Instance)
    {
        public int Loads { get; private set; }

        public override Task<Bitmap?> LoadAsync(Track track) => Count(track);
        public override Task<Bitmap?> LoadThumbnailAsync(Track track) => Count(track);

        private Task<Bitmap?> Count(Track track)
        {
            if (ours.Contains(track.Id))
                Loads++;
            return Task.FromResult<Bitmap?>(null);
        }
    }

    private readonly AlbumArtLoader _previousLoader = AlbumArtLoader.Current;
    private readonly HashSet<Guid> _ours = new();
    private readonly CountingArtLoader _art;

    public AlbumArtReplacementTests()
    {
        _art = new CountingArtLoader(_ours);
        AlbumArtLoader.Current = _art;
    }

    public void Dispose() => AlbumArtLoader.Current = _previousLoader;

    private Track T()
    {
        var track = new Track { Title = "Song", Album = "Album", Artists = "Artist", Path = "/music/x.mp3" };
        _ours.Add(track.Id);
        return track;
    }

    [AvaloniaFact]
    public void A_tile_showing_a_replaced_cover_reloads_it()
    {
        var track = T();
        var tile = AlbumGridBuilder.Build([track])[0];
        _ = tile.AlbumArt;
        var changes = new List<string?>();
        tile.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        changes.Clear();

        AlbumArtLoader.Invalidate(track);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(nameof(AlbumTileViewModel.AlbumArt), changes);
        _ = tile.AlbumArt;
        Assert.Equal(2, _art.Loads);
    }

    [AvaloniaFact]
    public void A_row_showing_a_replaced_cover_reloads_it()
    {
        var track = T();
        var row = new TrackRowViewModel { Track = track };
        _ = row.AlbumArt;
        var changes = new List<string?>();
        row.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        AlbumArtLoader.Invalidate(track);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(nameof(TrackRowViewModel.AlbumArt), changes);
        _ = row.AlbumArt;
        Assert.Equal(2, _art.Loads);
    }

    // Nothing has drawn it, so there is nothing to put right - and a list the
    // size of the library must not load every cover in it because one changed.
    [AvaloniaFact]
    public void A_tile_never_shown_is_left_alone()
    {
        var track = T();
        var tile = AlbumGridBuilder.Build([track])[0];
        var changes = new List<string?>();
        tile.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        AlbumArtLoader.Invalidate(track);
        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain(nameof(AlbumTileViewModel.AlbumArt), changes);
        Assert.Equal(0, _art.Loads);
    }
}
