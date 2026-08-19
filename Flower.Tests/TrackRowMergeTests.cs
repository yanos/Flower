using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Flower.Models;
using Flower.Services;
using Flower.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Flower.Tests;

// Row reuse across a rebuild - docs/ARCHITECTURE-REVIEW.md Tier 1.5's largest
// deferred item. Everything here goes through TrackListBuilder.Plan +
// TrackRowMerge.Apply, the pair LibraryBrowserViewModel.RebuildRowsAsync
// actually uses, rather than through an extracted copy of the matching rule.
public class TrackRowMergeTests : IDisposable
{
    // Counts how often a row asks for its art, which is the whole cost reuse
    // exists to avoid. Returning null is enough - AlbumArt's state machine
    // treats a null result as "loaded, there is none" exactly like a bitmap.
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

    public TrackRowMergeTests() => AlbumArtLoader.Current = _art;
    public void Dispose() => AlbumArtLoader.Current = _previousLoader;

    private static Track T(string title, string album = "Album", string? path = "/music/x.mp3", Guid id = default) =>
        new() { Id = id == default ? Guid.NewGuid() : id, Title = title, Album = album, Artists = "Artist", Path = path };

    private static List<TrackRowViewModel> Build(IEnumerable<Track> tracks, IReadOnlyList<TrackRowViewModel>? previous, out List<TrackRowViewModel> retired) =>
        TrackRowMerge.Apply(previous, TrackListBuilder.Plan(tracks, null, "Title", true), out retired);

    // The case that fires on every single launch: a rescan hands the library
    // brand-new Track objects carrying the old Ids (Library.
    // CarryForwardMutableState), so reference equality would miss every row.
    [Fact]
    public void A_rescan_producing_new_Track_instances_reuses_the_same_rows()
    {
        var id = Guid.NewGuid();
        var first = Build(new[] { T("Song", id: id) }, null, out _);

        var rescanned = T("Song", id: id);
        var second = Build(new[] { rescanned }, first, out var retired);

        Assert.Same(first[0], second[0]);
        Assert.Empty(retired);
        // ...and pointing at the *new* instance, not the stale one the library
        // no longer holds.
        Assert.Same(rescanned, second[0].Track);
    }

    [Fact]
    public void A_reused_row_keeps_the_album_art_it_already_loaded()
    {
        var id = Guid.NewGuid();
        var first = Build(new[] { T("Song", id: id) }, null, out _);
        _ = first[0].AlbumArt;
        Assert.Equal(1, _art.Loads);

        var second = Build(new[] { T("Song", id: id) }, first, out _);
        _ = second[0].AlbumArt;

        Assert.Equal(1, _art.Loads);
    }

    // The one case reuse must *not* keep the bitmap: the art would now come
    // from somewhere else.
    [Fact]
    public void A_reused_row_whose_album_changed_reloads_its_art()
    {
        var id = Guid.NewGuid();
        var first = Build(new[] { T("Song", album: "Old", id: id) }, null, out _);
        _ = first[0].AlbumArt;

        var second = Build(new[] { T("Song", album: "New", id: id) }, first, out _);
        _ = second[0].AlbumArt;

        Assert.Equal(2, _art.Loads);
    }

    [Fact]
    public void A_placeholder_that_has_since_been_downloaded_reloads_its_art()
    {
        var id = Guid.NewGuid();
        var first = Build(new[] { T("Song", path: null, id: id) }, null, out _);
        _ = first[0].AlbumArt;

        var second = Build(new[] { T("Song", path: "/music/downloaded.mp3", id: id) }, first, out _);
        _ = second[0].AlbumArt;

        Assert.Equal(2, _art.Loads);
        Assert.False(second[0].IsPlaceholder);
    }

    [Fact]
    public void Rows_that_no_longer_appear_are_retired_for_the_caller_to_dispose()
    {
        var keep = T("Keep");
        var drop = T("Drop");
        var first = Build(new[] { keep, drop }, null, out _);
        var droppedRow = first.Single(r => r.Track == drop);

        var second = Build(new[] { keep }, first, out var retired);

        Assert.Single(second);
        Assert.Same(droppedRow, Assert.Single(retired));
    }

    // A filter narrowing the list must not leave the surviving row's spinner
    // subscription dangling on the shared clock - and must not kill it either.
    [Fact]
    public void A_reused_row_keeps_a_download_in_progress_and_a_retired_one_does_not()
    {
        var keep = T("Keep");
        var drop = T("Drop");
        var first = Build(new[] { keep, drop }, null, out _);
        foreach (var row in first)
            row.IsDownloading = true;

        var second = Build(new[] { keep }, first, out var retired);
        foreach (var row in retired)
            row.Dispose();

        Assert.True(second[0].IsDownloading);
        Assert.Equal(0, retired[0].SpinAngle);

        second[0].Dispose();
    }

    // Grouping is positional, so it changes under a row that itself did not.
    [Fact]
    public void A_reused_row_picks_up_its_new_album_grouping()
    {
        var one = T("B", album: "Album");
        var earlier = T("A", album: "Album");
        var first = Build(new[] { one }, null, out _);
        Assert.True(first[0].IsFirstInAlbumGroup);
        Assert.Equal(1, first[0].AlbumGroupSize);

        var second = Build(new[] { earlier, one }, first, out _);

        var reused = second.Single(r => r.Track == one);
        Assert.Same(first[0], reused);
        Assert.False(reused.IsFirstInAlbumGroup);
        Assert.Equal(2, reused.AlbumGroupSize);
    }

    [Fact]
    public void Changing_the_grouping_raises_the_properties_the_art_cell_binds_to()
    {
        var one = T("B", album: "Album");
        var first = Build(new[] { one }, null, out _);
        var changed = new List<string>();
        first[0].PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        Build(new[] { T("A", album: "Album"), one }, first, out _);

        Assert.Contains(nameof(TrackRowViewModel.IsFirstInAlbumGroup), changed);
        Assert.Contains(nameof(TrackRowViewModel.AlbumGroupSize), changed);
        Assert.Contains(nameof(TrackRowViewModel.AlbumArtDisplaySize), changed);
    }

    [Fact]
    public void Re_pointing_a_row_at_a_new_Track_re_raises_the_bound_paths()
    {
        var id = Guid.NewGuid();
        var first = Build(new[] { T("Song", id: id) }, null, out _);
        var changed = new List<string>();
        first[0].PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        Build(new[] { T("Song", id: id) }, first, out _);

        // TrackRowControl binds Track.Title/Track.Artists/... through this one.
        Assert.Contains(nameof(TrackRowViewModel.Track), changed);
        Assert.Contains(nameof(TrackRowViewModel.DurationDisplay), changed);
    }

    // Nothing changed at all: the row must not churn notifications either.
    [Fact]
    public void An_identical_rebuild_raises_nothing()
    {
        var track = T("Song");
        var first = Build(new[] { track }, null, out _);
        var changed = new List<string>();
        first[0].PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        Build(new[] { track }, first, out var retired);

        Assert.Empty(changed);
        Assert.Empty(retired);
    }

    // The clock is injected (container -> MainViewModel -> browser -> row)
    // rather than reached for statically, so a row's spinner is drivable
    // without touching the process-wide default at all.
    [Fact]
    public void A_row_built_with_an_injected_clock_animates_on_that_clock()
    {
        var now = TimeSpan.Zero;
        var clock = new AnimationClock(() => now);
        var rows = TrackRowMerge.Apply(null, TrackListBuilder.Plan(new[] { T("Song") }, null, "Title", true), out _, clock);

        rows[0].IsDownloading = true;
        Assert.Equal(1, clock.SubscriberCount);

        now = TimeSpan.FromMilliseconds(250);
        clock.TickForTest();
        Assert.True(rows[0].SpinAngle > 0, "the injected clock never drove the spinner");

        rows[0].Dispose();
        Assert.Equal(0, clock.SubscriberCount);
    }

    // A playlist may legitimately hold the same track twice.
    [Fact]
    public void A_duplicated_track_gets_one_reused_row_and_one_fresh_one()
    {
        var track = T("Song");
        var first = Build(new[] { track }, null, out _);

        var second = TrackRowMerge.Apply(
            first,
            TrackListBuilder.Plan(new[] { track, track }, null, "PlaylistOrder", true),
            out var retired);

        Assert.Equal(2, second.Count);
        Assert.Same(first[0], second[0]);
        Assert.NotSame(second[0], second[1]);
        Assert.Empty(retired);
    }

    // The mirror of the above: an old list holding it twice must not leave the
    // second instance undisposed when only one row is reused.
    [Fact]
    public void A_previously_duplicated_track_retires_the_copy_it_cannot_reuse()
    {
        var track = T("Song");
        var first = TrackRowMerge.Apply(null, TrackListBuilder.Plan(new[] { track, track }, null, "PlaylistOrder", true), out _);

        var second = Build(new[] { track }, first, out var retired);

        Assert.Single(second);
        Assert.Same(first[0], second[0]);
        Assert.Same(first[1], Assert.Single(retired));
    }
}
