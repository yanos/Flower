using System;
using System.Linq;

using Flower.Models;
using Flower.SemanticAudio;

using Xunit;

namespace Flower.Tests.SemanticAudio;

// What the settings page says each clip setting costs.
public class SemanticAudioCostsTests
{
    [Fact]
    public void Counts_each_setting_on_the_grid_a_song_is_cut_on()
    {
        // A minute is six windows: every setting of six or more keeps all of
        // them, and "whole song" is no dearer than twelve.
        Track[] tracks = [new() { Duration = TimeSpan.FromSeconds(60) }, new() { Duration = TimeSpan.FromMinutes(4) }];

        var costs = SemanticAudioCosts.For(tracks, clipsPerSecond: 4);

        Assert.Equal([(3, 6L), (6, 12L), (12, 18L), (ClipGrid.WholeSong, 30L)], costs.Select(c => (c.ClipsPerSong, c.Clips)));
        Assert.Equal(30 * 1024L, costs[^1].Bytes);
        Assert.Equal(TimeSpan.FromSeconds(7.5), costs[^1].Time);
    }

    [Fact]
    public void Gives_no_time_before_this_host_has_measured_itself()
    {
        Track[] tracks = [new() { Duration = TimeSpan.FromMinutes(4) }];

        Assert.All(SemanticAudioCosts.For(tracks, clipsPerSecond: null), c => Assert.Null(c.Time));
    }
}
