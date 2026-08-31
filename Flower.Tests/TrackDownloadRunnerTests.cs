using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Flower.Models;
using Flower.Services;
using Flower.ViewModels;
using Flower.ViewModels.Mobile;

using Xunit;

namespace Flower.Tests;

// The UI-side download driver shared by desktop's track-list icon/right-click
// menus and mobile's own download button (see TrackDownloadRunner) - the row
// state it leaves behind, and what a batch actually asks for.
public class TrackDownloadRunnerTests
{
    // A clock with no dispatcher timer behind it (see AnimationClock's
    // test constructor): the spinner these rows start while downloading would
    // otherwise leave the shared 60 Hz timer running past the end of the run.
    private static TrackRowViewModel Row(Track track) =>
        new() { Track = track, Clock = new AnimationClock(() => TimeSpan.Zero) };

    private static Track Placeholder(string title) => new() { Title = title, Path = null };

    private static Track Downloaded(string title) => new() { Title = title, Path = $"/music/{title}.mp3" };

    // Rows are found the way the real hosts find them: by SyncKey against
    // whatever list is currently on screen.
    private static Func<string, TrackRowViewModel?> Lookup(IReadOnlyList<TrackRowViewModel> rows) =>
        key => rows.FirstOrDefault(r => r.Track.SyncKey == key);

    [Fact]
    public async Task SuccessfulRowDownloadLeavesNoErrorState()
    {
        var row = Row(Placeholder("A"));
        var runner = new TrackDownloadRunner(_ => Task.FromResult(TrackDownloadResult.Downloaded), Lookup([row]));

        await runner.DownloadRowAsync(row);

        Assert.False(row.IsDownloading);
        Assert.False(row.IsDownloadUnavailable);
        Assert.True(row.IsDownloadIdle);
    }

    [Theory]
    [InlineData(TrackDownloadResult.Failed)]
    [InlineData(TrackDownloadResult.PeerUnavailable)]
    public async Task FailedRowDownloadMarksTheRow(TrackDownloadResult result)
    {
        var row = Row(Placeholder("A"));
        var runner = new TrackDownloadRunner(_ => Task.FromResult(result), Lookup([row]));

        await runner.DownloadRowAsync(row);

        Assert.True(row.IsDownloadUnavailable);
        Assert.False(row.IsDownloadIdle);
    }

    // A row already mid-download must not be started a second time - the icon
    // is clickable throughout, and the batch path can reach the same row.
    [Fact]
    public async Task RowAlreadyDownloadingIsNotStartedAgain()
    {
        var row = Row(Placeholder("A"));
        row.IsDownloading = true;
        var calls = 0;
        var runner = new TrackDownloadRunner(_ => { calls++; return Task.FromResult(TrackDownloadResult.Downloaded); }, Lookup([row]));

        await runner.DownloadRowAsync(row);

        Assert.Equal(0, calls);
        row.Dispose(); // stops the spin subscription IsDownloading started
    }

    [Fact]
    public async Task BatchSkipsTracksThatAreAlreadyLocal()
    {
        var placeholder = Placeholder("A");
        var rows = new List<TrackRowViewModel> { Row(placeholder), Row(Downloaded("B")) };
        var asked = new List<string?>();
        var runner = new TrackDownloadRunner(
            t => { asked.Add(t.Title); return Task.FromResult(TrackDownloadResult.Downloaded); },
            Lookup(rows));

        await runner.DownloadAllAsync(rows.Select(r => r.Track).ToList());

        Assert.Equal(["A"], asked);
    }

    // The desktop album grid's case: the right-clicked album's songs need not
    // be in the track list at all, so there is no row to animate - the
    // download still has to happen.
    [Fact]
    public async Task BatchDownloadsTracksWithNoRowBehindThem()
    {
        var asked = new List<string?>();
        var runner = new TrackDownloadRunner(
            t => { asked.Add(t.Title); return Task.FromResult(TrackDownloadResult.Downloaded); },
            _ => null);

        await runner.DownloadAllAsync([Placeholder("A"), Placeholder("B")]);

        Assert.Equal(["A", "B"], asked.OrderBy(t => t));
    }

    [Fact]
    public async Task BatchRaisesAndClearsTheBulkFlag()
    {
        var row = Row(Placeholder("A"));
        var runner = new TrackDownloadRunner(_ => Task.FromResult(TrackDownloadResult.Downloaded), Lookup([row]));
        var seen = new List<bool>();
        runner.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TrackDownloadRunner.IsBulkDownloading))
                seen.Add(runner.IsBulkDownloading);
        };

        await runner.DownloadAllAsync([row.Track]);

        Assert.Equal([true, false], seen);
        Assert.False(runner.IsBulkDownloading);
    }

    // Nothing to fetch means no spinner and no busy scope at all, rather than
    // one that flickers up and straight back down.
    [Fact]
    public async Task BatchWithNothingToDownloadDoesNotStart()
    {
        var busyScopes = 0;
        var runner = new TrackDownloadRunner(
            _ => Task.FromResult(TrackDownloadResult.Downloaded),
            _ => null,
            _ => { busyScopes++; return new NoopScope(); });

        await runner.DownloadAllAsync([Downloaded("A")]);

        Assert.Equal(0, busyScopes);
        Assert.False(runner.IsBulkDownloading);
    }

    [Fact]
    public async Task BatchOpensOneBusyScopeAndClosesIt()
    {
        var scope = new NoopScope();
        var runner = new TrackDownloadRunner(
            _ => Task.FromResult(TrackDownloadResult.Downloaded),
            _ => null,
            _ => scope);

        await runner.DownloadAllAsync([Placeholder("A"), Placeholder("B")]);

        Assert.True(scope.Disposed);
    }

    // The album-level icon (a grid tile, downloading its whole album behind
    // one spinner) - the same runner, driving an indicator that stands for
    // several tracks rather than one.
    [Fact]
    public async Task AlbumDownloadClearsItsIndicatorWhenEveryTrackArrives()
    {
        var tile = Tile();
        var runner = new TrackDownloadRunner(_ => Task.FromResult(TrackDownloadResult.Downloaded), _ => null);

        await runner.DownloadAlbumAsync(tile, [Placeholder("A"), Placeholder("B")]);

        Assert.False(tile.IsDownloading);
        Assert.False(tile.IsDownloadUnavailable);
        Assert.False(runner.IsBulkDownloading);
    }

    [Fact]
    public async Task AlbumDownloadMarksItsIndicatorWhenATrackFails()
    {
        var tile = Tile();
        var runner = new TrackDownloadRunner(
            track => Task.FromResult(track.Title == "B" ? TrackDownloadResult.Failed : TrackDownloadResult.Downloaded),
            _ => null);

        await runner.DownloadAlbumAsync(tile, [Placeholder("A"), Placeholder("B")]);

        Assert.False(tile.IsDownloading);
        Assert.True(tile.IsDownloadUnavailable);
    }

    // An expanded album's own song rows are a different view-model over the
    // same icon - they get the same single-track treatment the track list's
    // rows do.
    [Fact]
    public async Task ExpandedAlbumRowDownloadMarksItsOwnRow()
    {
        var row = new ExpandedTrackRowViewModel
        {
            Track = Placeholder("A"),
            Clock = new AnimationClock(() => TimeSpan.Zero),
        };
        var runner = new TrackDownloadRunner(_ => Task.FromResult(TrackDownloadResult.PeerUnavailable), _ => null);

        await runner.DownloadRowAsync(row);

        Assert.False(row.IsDownloading);
        Assert.True(row.IsDownloadUnavailable);
        row.Dispose();
    }

    private static AlbumTileViewModel Tile() => new()
    {
        Name = "An Album",
        RepresentativeTrack = Placeholder("A"),
        Tracks = [Placeholder("A"), Placeholder("B")],
        Clock = new AnimationClock(() => TimeSpan.Zero),
    };

    private sealed class NoopScope : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
