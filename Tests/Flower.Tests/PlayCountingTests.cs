using System;
using System.Collections.Generic;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Models;
using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests;

// What counts as a play: nine tenths of the track heard, where "heard" is the
// position moving at the speed of the clock and a seek is not (see
// ListenMeter). Tested through PlaylistControlViewModel rather than against the
// meter alone, because the rule is as much about where the meter is told things
// - a start, a pause, a resume, an end - as about its arithmetic.
//
// The clock is the test's own. Everything here is a claim about how far the
// position moved in how much time, and a real clock would make each of them a
// claim about how fast the test ran.
[Collection("PlatformDataDirectory")]
public class PlayCountingTests : PinnedDataDirectory
{
    private static readonly TimeSpan ThreeMinutes = TimeSpan.FromMinutes(3);

    private readonly FakeAudioManager _audio = new();
    private TimeSpan _now = TimeSpan.FromHours(1);

    private static Track T(string title, TimeSpan duration) =>
        new() { Title = title, Path = $"/music/{title}.mp3", Duration = duration };

    private PlaylistControlViewModel Player(params Track[] tracks)
    {
        var list = new List<Track>(tracks);
        var vm = Own(new PlaylistControlViewModel(
            _audio, new MainPlaylist(list), new Library(list), new AppSettings(),
            new AppSettingsStore(NullLogger<AppSettingsStore>.Instance),
            NullLogger<PlaylistControlViewModel>.Instance,
            listenMeter: new ListenMeter(() => _now)));

        // Inline, so an assertion straight after the report that counts a play
        // is not racing the pool - see PlaylistControlViewModel.OffPlaybackThread.
        vm.OffPlaybackThread = work => work();
        return vm;
    }

    // Playback carrying on to a position, reported the way the position timer
    // reports it: the clock and the position moving together.
    private void HearUpTo(TimeSpan position, int everyMs = 250)
    {
        var target = (long)position.TotalMilliseconds;
        while (_audio.Time < target)
        {
            var step = Math.Min(everyMs, target - _audio.Time);
            _now += TimeSpan.FromMilliseconds(step);
            _audio.Time += step;
            _audio.RaisePositionChanged();
        }
    }

    // The position moving with no time passing, and the report that follows.
    private void SeekTo(TimeSpan position)
    {
        _audio.Time = (long)position.TotalMilliseconds;
        _audio.RaisePositionChanged();
    }

    [Fact]
    public void A_track_is_counted_once_nine_tenths_of_it_has_been_heard_and_not_before()
    {
        var song = T("Song", ThreeMinutes);
        var vm = Player(song);
        vm.Play(song);

        HearUpTo(TimeSpan.FromSeconds(161));
        Assert.Equal(0, song.PlayCount);

        HearUpTo(TimeSpan.FromSeconds(162));
        Assert.Equal(1, song.PlayCount);
    }

    [Fact]
    public void Playing_on_to_the_end_does_not_count_it_a_second_time()
    {
        var song = T("Song", ThreeMinutes);
        var vm = Player(song);
        vm.Play(song);

        HearUpTo(ThreeMinutes);
        _audio.RaiseEndReached();

        Assert.Equal(1, song.PlayCount);
    }

    // The case the old rule got wrong in one direction: a song heard right
    // through to its fade-out and then skipped never reached its end.
    [Fact]
    public void Skipping_on_after_nine_tenths_keeps_the_play()
    {
        var song = T("Song", ThreeMinutes);
        var next = T("Next", ThreeMinutes);
        var vm = Player(song, next);
        vm.Play(song);

        HearUpTo(TimeSpan.FromSeconds(170));
        vm.Next();

        Assert.Equal(1, song.PlayCount);
        Assert.Equal(0, next.PlayCount);
    }

    [Fact]
    public void Skipping_on_before_nine_tenths_counts_nothing()
    {
        var song = T("Song", ThreeMinutes);
        var next = T("Next", ThreeMinutes);
        var vm = Player(song, next);
        vm.Play(song);

        HearUpTo(TimeSpan.FromSeconds(150));
        vm.Next();

        Assert.Equal(0, song.PlayCount);
    }

    // And the case it got wrong in the other: the end reached without the
    // song having been listened to.
    [Fact]
    public void Seeking_to_the_end_and_letting_it_finish_counts_nothing()
    {
        var song = T("Song", ThreeMinutes);
        var vm = Player(song);
        vm.Play(song);

        HearUpTo(TimeSpan.FromSeconds(10));
        SeekTo(TimeSpan.FromSeconds(170));
        HearUpTo(ThreeMinutes);
        _audio.RaiseEndReached();

        Assert.Equal(0, song.PlayCount);
    }

    [Fact]
    public void Going_back_over_part_of_a_song_does_not_stop_it_counting()
    {
        var song = T("Song", ThreeMinutes);
        var vm = Player(song);
        vm.Play(song);

        HearUpTo(TimeSpan.FromSeconds(100));
        SeekTo(TimeSpan.FromSeconds(40));
        HearUpTo(TimeSpan.FromSeconds(101));
        Assert.Equal(0, song.PlayCount);

        HearUpTo(TimeSpan.FromSeconds(105));
        Assert.Equal(1, song.PlayCount);
    }

    // A long pause makes the clock say any distance could have been played.
    // It was not: the position moved because it was dragged.
    [Fact]
    public void A_seek_made_while_paused_is_not_listening()
    {
        var song = T("Song", ThreeMinutes);
        var vm = Player(song);
        vm.Play(song);

        HearUpTo(TimeSpan.FromSeconds(10));
        _audio.RaisePaused();
        _now += TimeSpan.FromMinutes(10);
        _audio.Time = 170_000;
        HearUpTo(ThreeMinutes);
        _audio.RaiseEndReached();

        Assert.Equal(0, song.PlayCount);
    }

    [Fact]
    public void A_pause_in_the_middle_does_not_cost_the_play()
    {
        var song = T("Song", ThreeMinutes);
        var vm = Player(song);
        vm.Play(song);

        HearUpTo(TimeSpan.FromSeconds(90));
        _audio.RaisePaused();
        _now += TimeSpan.FromMinutes(10);
        HearUpTo(TimeSpan.FromSeconds(165));

        Assert.Equal(1, song.PlayCount);
    }

    // A browser tab in the background is told the position about once a
    // second, and a busy phone can miss several reports in a row.
    [Fact]
    public void Reports_far_apart_count_as_long_as_the_clock_agrees_with_them()
    {
        var song = T("Song", ThreeMinutes);
        var vm = Player(song);
        vm.Play(song);

        HearUpTo(TimeSpan.FromSeconds(165), everyMs: 15_000);

        Assert.Equal(1, song.PlayCount);
    }

    // The decoder runs out a couple of seconds before the listener does - the
    // tail is still buffered - so a short track ends well short of nine tenths
    // of its length as reported. Nothing was skipped, and it counts.
    [Fact]
    public void A_short_track_that_ends_with_its_tail_still_buffered_is_counted()
    {
        var interlude = T("Interlude", TimeSpan.FromSeconds(8));
        var vm = Player(interlude);
        vm.Play(interlude);

        HearUpTo(TimeSpan.FromSeconds(5));
        _audio.RaiseEndReached();

        Assert.Equal(1, interlude.PlayCount);
    }

    [Fact]
    public void A_track_tagged_longer_than_it_is_counts_when_it_ends()
    {
        var song = T("Song", TimeSpan.FromMinutes(5));
        var vm = Player(song);
        vm.Play(song);

        HearUpTo(ThreeMinutes);
        _audio.RaiseEndReached();

        Assert.Equal(1, song.PlayCount);
    }

    [Fact]
    public void A_track_with_no_known_length_counts_when_it_ends_and_only_then()
    {
        var song = T("Song", TimeSpan.Zero);
        var vm = Player(song);
        vm.Play(song);

        HearUpTo(ThreeMinutes);
        Assert.Equal(0, song.PlayCount);

        _audio.RaiseEndReached();
        Assert.Equal(1, song.PlayCount);
    }

    // Repeat, or simply putting the same song on again.
    [Fact]
    public void Starting_the_same_track_again_is_a_second_play()
    {
        var song = T("Song", ThreeMinutes);
        var vm = Player(song);

        vm.Play(song);
        HearUpTo(ThreeMinutes);

        vm.Play(song);
        _audio.Time = 0;
        HearUpTo(ThreeMinutes);

        Assert.Equal(2, song.PlayCount);
    }

    // ── Picked back up where it was left ────────────────────────────────────

    private static Track Podcast(TimeSpan leftAt)
    {
        var podcast = T("Podcast", TimeSpan.FromMinutes(60));
        podcast.RememberPlaybackPosition = true;
        podcast.ResumePosition = leftAt;
        return podcast;
    }

    // One listen in two sittings. Without this a track long enough to be put
    // down half-way - which is what the option is for - could never count.
    [Fact]
    public void A_track_resumed_where_it_was_left_counts_what_came_before()
    {
        var podcast = Podcast(leftAt: TimeSpan.FromMinutes(30));
        var vm = Player(podcast);
        vm.Play(podcast);
        _audio.RaisePlaying();
        _audio.Time = (long)TimeSpan.FromMinutes(30).TotalMilliseconds;

        HearUpTo(TimeSpan.FromMinutes(53));
        Assert.Equal(0, podcast.PlayCount);

        HearUpTo(TimeSpan.FromMinutes(55));
        Assert.Equal(1, podcast.PlayCount);
    }

    // It crossed the line in the sitting that left it there.
    [Fact]
    public void A_track_resumed_past_nine_tenths_is_not_counted_again()
    {
        var podcast = Podcast(leftAt: TimeSpan.FromMinutes(57));
        var vm = Player(podcast);
        vm.Play(podcast);
        _audio.RaisePlaying();
        _audio.Time = (long)TimeSpan.FromMinutes(57).TotalMilliseconds;

        HearUpTo(TimeSpan.FromMinutes(60));
        _audio.RaiseEndReached();

        Assert.Equal(0, podcast.PlayCount);
    }
}
