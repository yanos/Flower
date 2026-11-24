using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;
using Flower.Services;

using Xunit;

namespace Flower.Tests.Playback;

// UF-8, for a playlist edit that arrives as nothing but a new order, the way
// another device's does: which copy of a song it changed comes from the order.
public class QueueFollowsPlaylistCopiesTests
{
    private static readonly Track A = new() { Title = "A" };
    private static readonly Track B = new() { Title = "B" };
    private static readonly Track C = new() { Title = "C" };

    private static List<Playlist.StoredEntry> Unkeyed(Track[] tracks) =>
        tracks.Select(t => new Playlist.StoredEntry(t.Id, 0)).ToList();

    private static QueueFollowsPlaylist.Result Edit(Track[] before, int playing, Track[] after) =>
        QueueFollowsPlaylist.Apply(
            before, Enumerable.Range(0, before.Length).ToList(), playing, linedUp: 0, playingHasLeft: false,
            followsOrder: true, Unkeyed(before), Unkeyed(after), after);

    private static string[] Titles(QueueFollowsPlaylist.Result result) =>
        result.Queue.Select(t => t.Title!).ToArray();

    [Fact]
    public void Removing_the_other_copy_leaves_the_song_playing_alone()
    {
        var result = Edit([A, B, A], playing: 0, after: [A, B]);

        Assert.Equal(["A", "B"], Titles(result));
        Assert.Equal(0, result.Place);
        Assert.False(result.PlayingLeft);
    }

    [Fact]
    public void Removing_the_first_copy_while_the_second_plays_keeps_the_second_playing()
    {
        var result = Edit([A, B, A], playing: 2, after: [B, A]);

        Assert.Equal(["B", "A"], Titles(result));
        Assert.Equal(1, result.Place);
        Assert.False(result.PlayingLeft);
    }

    [Fact]
    public void Removing_the_copy_that_is_playing_lets_it_finish()
    {
        var result = Edit([A, B, A], playing: 2, after: [A, B]);

        Assert.True(result.PlayingLeft);
        Assert.Equal(2, result.Place);
        Assert.Equal(["A", "B", "A"], Titles(result));
    }

    [Fact]
    public void Dragging_the_playing_copy_up_takes_the_place_with_it()
    {
        var result = Edit([A, B, C, A], playing: 3, after: [A, A, B, C]);

        Assert.Equal(["A", "A", "B", "C"], Titles(result));
        Assert.Equal(1, result.Place);
        Assert.False(result.PlayingLeft);
    }

    [Fact]
    public void Dragging_the_other_copy_down_leaves_the_place_on_the_one_playing()
    {
        var result = Edit([A, B, A, C], playing: 2, after: [B, A, C, A]);

        Assert.Equal(["B", "A", "C", "A"], Titles(result));
        Assert.Equal(1, result.Place);
    }
}
