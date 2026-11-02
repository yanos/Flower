using System;
using System.Collections.Generic;
using System.Linq;

using Flower.SemanticAudio;

using Xunit;

namespace Flower.Tests.SemanticAudio;

// Which windows are a song's clips (docs/VIBE-PLAYLIST-PLAN.md, "Clips"). The
// fresh selection is phase 0's, which the owner's listening was done on; the
// selection given stored windows is what lets lowering the setting analyse
// nothing.
public class ClipGridTests
{
    private static TimeSpan Seconds(double s) => TimeSpan.FromSeconds(s);

    [Theory]
    [InlineData(0.5, 1)]    // shorter than a tail: still window 0
    [InlineData(10, 1)]
    [InlineData(12.9, 1)]   // a 2.9 s tail is dropped
    [InlineData(13, 2)]     // a 3 s tail is kept
    [InlineData(20, 2)]
    [InlineData(243.5, 25)]
    public void Counts_full_windows_and_a_tail_of_three_seconds_or_more(double seconds, int windows)
    {
        Assert.Equal(windows, ClipGrid.WindowCount(Seconds(seconds)));
    }

    [Fact]
    public void Spreads_clips_evenly_inside_the_middle_ninety_percent()
    {
        // Points at 30, 66, 102, 138, 174 and 210 s of a 240 s song.
        Assert.Equal([3, 6, 10, 13, 17, 21], ClipGrid.Select(24, Seconds(240), 6));
    }

    [Fact]
    public void Gives_a_short_song_fewer_clips_rather_than_one_twice()
    {
        // 25 s: points at 3.1, 6.9, 10.6, 14.4, 18.1 and 21.9 s.
        Assert.Equal([0, 1, 2], ClipGrid.Select(3, Seconds(25), 6));
    }

    [Fact]
    public void A_point_in_a_dropped_tail_falls_back_to_the_last_window()
    {
        // 21 s is two windows; the last point, 18.4 s, is inside window 1, and
        // 20.6 s at 12 clips is in the dropped tail.
        Assert.Equal(1, ClipGrid.Select(2, Seconds(21), 12).Max());
    }

    [Fact]
    public void The_whole_song_is_every_window()
    {
        Assert.Equal(Enumerable.Range(0, 25), ClipGrid.Select(25, Seconds(243.5), ClipGrid.WholeSong));
    }

    [Theory]
    [InlineData(12, 6)]
    [InlineData(6, 3)]
    [InlineData(12, 3)]
    [InlineData(ClipGrid.WholeSong, 3)]
    [InlineData(ClipGrid.WholeSong, 12)]
    public void Lowering_the_setting_selects_only_windows_already_stored(int from, int to)
    {
        foreach (var seconds in new[] { 45.0, 95, 183, 240, 301, 512, 1260 })
        {
            var windows = ClipGrid.WindowCount(Seconds(seconds));
            var stored = ClipGrid.Select(windows, Seconds(seconds), from).ToHashSet();

            var lowered = ClipGrid.Select(windows, Seconds(seconds), to, stored);

            Assert.All(lowered, k => Assert.Contains(k, stored));
        }
    }

    [Fact]
    public void Raising_the_setting_adds_windows_of_its_own()
    {
        var stored = ClipGrid.Select(24, Seconds(240), 3).ToHashSet();

        var raised = ClipGrid.Select(24, Seconds(240), 6, stored);

        Assert.Equal(6, raised.Count);
        Assert.Contains(raised, k => !stored.Contains(k));
    }

    [Fact]
    public void Nothing_stored_is_the_fresh_selection()
    {
        Assert.Equal(ClipGrid.Select(24, Seconds(240), 6), ClipGrid.Select(24, Seconds(240), 6, new HashSet<int>()));
    }

    [Fact]
    public void Refuses_a_setting_that_is_not_a_count()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ClipGrid.Select(24, Seconds(240), -1));
    }
}
