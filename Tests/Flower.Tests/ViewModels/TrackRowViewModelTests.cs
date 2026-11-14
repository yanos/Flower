using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Flower.Audio;
using Flower.Models;
using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Material.Icons;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Flower.Tests.ViewModels;

public class TrackRowViewModelTests
{
    private static TrackRowViewModel Row(Track track, bool first = true, int groupSize = 1) =>
        new() { Track = track, IsFirstInAlbumGroup = first, AlbumGroupSize = groupSize };

    // The display strings read straight off Track, which is not
    // INotifyPropertyChanged, so a play or a star has to be pushed in.
    [Fact]
    public void NotifyTrackChanged_raises_every_in_place_display_property()
    {
        var row = Row(new Track());
        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        row.NotifyTrackChanged();

        Assert.Contains(nameof(TrackRowViewModel.PlayCountDisplay), raised);
        Assert.Contains(nameof(TrackRowViewModel.LastPlayedDisplay), raised);
        Assert.Contains(nameof(TrackRowViewModel.StarredDisplay), raised);
    }

    // IsAvailable defaults to false so a just-built row never flashes as
    // available before it is actually known to be.
    [Fact]
    public void A_new_placeholder_row_starts_unavailable_and_not_downloadable()
    {
        var row = Row(new Track { Path = null });

        Assert.False(row.IsAvailable);
        Assert.True(row.IsUnavailable);
        Assert.False(row.IsDownloadable);
    }

    [Fact]
    public void Setting_IsAvailable_flips_both_derived_flags_and_raises_them()
    {
        var row = Row(new Track { Path = null });
        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        row.IsAvailable = true;

        Assert.False(row.IsUnavailable);
        Assert.True(row.IsDownloadable);
        Assert.Contains(nameof(TrackRowViewModel.IsUnavailable), raised);
        Assert.Contains(nameof(TrackRowViewModel.IsDownloadable), raised);
    }

    // A real (non-placeholder) track is never "unavailable" or "downloadable",
    // whatever IsAvailable happens to say.
    // Both flags are gated on IsPlaceholder, so an ordinary local track is
    // never either one - including in the IsAvailable=false default state,
    // which is the case that tells "gated on IsPlaceholder" apart from a bare
    // "!IsAvailable".
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_local_track_is_neither_unavailable_nor_downloadable(bool available)
    {
        var row = Row(new Track { Path = "/music/a.mp3" });
        row.IsAvailable = available;

        Assert.False(row.IsUnavailable);
        Assert.False(row.IsDownloadable);
    }

    [Fact]
    public void IsDownloadIdle_is_false_while_downloading_and_after_a_failure()
    {
        var row = Row(new Track { Path = null });
        Assert.True(row.IsDownloadIdle);

        row.IsDownloadUnavailable = true;
        Assert.False(row.IsDownloadIdle);

        row.IsDownloadUnavailable = false;
        Assert.True(row.IsDownloadIdle);
    }
}
