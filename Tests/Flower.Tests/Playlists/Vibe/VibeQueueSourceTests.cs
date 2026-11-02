using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;
using Flower.Services;

namespace Flower.Tests.Playlists.Vibe;

// The endless queue's draw (docs/VIBE-PLAYLIST-PLAN.md, "The endless queue").
public class VibeQueueSourceTests
{
    private static Track Song(int n, string? artist = null, string? album = null) =>
        new() { Title = $"Song {n}", Artists = artist ?? $"Artist {n}", Album = album ?? $"Album {n}", Path = $"/m/{n}.mp3" };

    private static Playlist Pool(IEnumerable<Track> songs) =>
        new("Rainy", songs.ToList()) { VibePrompt = "music for a rainy day" };

    private static List<Track> Draw(VibeQueueSource source, int count)
    {
        var queue = new List<Track>();
        for (var i = 0; i < count; i++)
        {
            if (source.Next(queue) is { } next)
                queue.Add(next);
        }
        return queue;
    }

    [Fact]
    public void No_song_comes_back_until_every_song_in_the_pool_has_been_queued()
    {
        var source = new VibeQueueSource(Pool(Enumerable.Range(0, 40).Select(n => Song(n))), random: new Random(1));

        var drawn = Draw(source, 40);

        Assert.Equal(40, drawn.Select(t => t.Id).Distinct().Count());
    }

    [Fact]
    public void Once_the_pool_is_spent_only_its_latest_quarter_stays_out()
    {
        var source = new VibeQueueSource(Pool(Enumerable.Range(0, 20).Select(n => Song(n))), random: new Random(2));
        var first = Draw(source, 20);

        var next = source.Next(first)!;

        Assert.DoesNotContain(next.Id, first.TakeLast(5).Select(t => t.Id));
    }

    [Fact]
    public void Asks_for_more_once_fewer_than_a_quarter_of_the_pool_is_left()
    {
        var source = new VibeQueueSource(Pool(Enumerable.Range(0, 20).Select(n => Song(n))), random: new Random(3));

        Draw(source, 15);
        Assert.False(source.WantsMore);

        Draw(source, 1);
        Assert.True(source.WantsMore);
    }

    [Fact]
    public void Songs_an_extension_brings_join_the_pool_and_say_so()
    {
        var source = new VibeQueueSource(Pool(Enumerable.Range(0, 4).Select(n => Song(n))), random: new Random(4));
        var more = Enumerable.Range(4, 3).Select(n => Song(n)).ToList();

        source.Add(more);
        source.Add(more);

        Assert.Equal(7, source.Pool.Count);
        Assert.Equal(more.Select(t => t.Id), source.Extensions.Select(t => t.Id));
        Assert.Equal(7, Draw(source, 7).Select(t => t.Id).Distinct().Count());
    }

    [Fact]
    public void Never_two_songs_in_a_row_from_one_album_while_another_is_there_to_play()
    {
        var songs = Enumerable.Range(0, 12).Select(n => Song(n, album: n < 6 ? "One" : "Two")).ToList();
        for (var seed = 0; seed < 20; seed++)
        {
            var drawn = Draw(new VibeQueueSource(Pool(songs), random: new Random(seed)), 12);

            for (var i = 1; i < drawn.Count; i++)
            {
                if (drawn[i].Album == drawn[i - 1].Album)
                    Assert.Fail($"Seed {seed}: {drawn[i - 1].Title} then {drawn[i].Title}, both from {drawn[i].Album}");
            }
        }
    }

    [Fact]
    public void An_artist_plays_at_most_once_in_any_four_songs_while_the_pool_allows()
    {
        var songs = Enumerable.Range(0, 24).Select(n => Song(n, artist: $"Band {n % 6}")).ToList();
        for (var seed = 0; seed < 20; seed++)
        {
            var drawn = Draw(new VibeQueueSource(Pool(songs), random: new Random(seed)), 12);

            for (var i = 0; i + 4 <= drawn.Count; i++)
                Assert.Equal(4, drawn.Skip(i).Take(4).Select(t => t.Artists).Distinct().Count());
        }
    }

    [Fact]
    public void A_pool_of_one_album_still_plays()
    {
        var songs = Enumerable.Range(0, 5).Select(n => Song(n, artist: "Band", album: "Only")).ToList();

        var drawn = Draw(new VibeQueueSource(Pool(songs), random: new Random(5)), 5);

        Assert.Equal(5, drawn.Select(t => t.Id).Distinct().Count());
    }

    [Fact]
    public void A_skipped_song_is_drawn_first_less_often_than_the_song_beside_it()
    {
        var songs = Enumerable.Range(0, 2).Select(n => Song(n)).ToList();
        var skippedFirst = 0;
        var plainFirst = 0;
        for (var seed = 0; seed < 2000; seed++)
        {
            var first = new VibeQueueSource(Pool(songs), skipped: [songs[0].Id], random: new Random(seed)).Next([])!;
            if (first.Id == songs[0].Id)
                skippedFirst++;
            var plain = new VibeQueueSource(Pool(songs), random: new Random(seed)).Next([])!;
            if (plain.Id == songs[0].Id)
                plainFirst++;
        }

        // Unskipped, the better-ranked song leads most of the time; skipped,
        // its weight is quartered and it leads far less.
        Assert.True(plainFirst > 1000, $"plain {plainFirst}");
        Assert.True(skippedFirst < plainFirst / 2, $"skipped {skippedFirst} vs plain {plainFirst}");
    }

    [Fact]
    public void An_empty_pool_draws_nothing()
    {
        var source = new VibeQueueSource(Pool([]));

        Assert.Null(source.Next([]));
        Assert.False(source.WantsMore);
    }
}
