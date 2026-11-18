using System;
using System.Collections.Generic;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Models;
using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests.Playback;

// What counts as a skip (Track.SkipCount): Next pressed within
// ListenMeter.SkipWithin of a song's start, before it counted as a play.
// Through PlaylistControlViewModel for PlayCountingTests' reason - the rule is
// as much about which gesture tells the meter as about the meter.
[Collection("PlatformDataDirectory")]
public class SkipCountingTests : PinnedDataDirectory
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

        vm.OffPlaybackThread = work => work();
        vm.OnUiThread = work => work();
        return vm;
    }

    private void HearUpTo(TimeSpan position)
    {
        var target = (long)position.TotalMilliseconds;
        while (_audio.Time < target)
        {
            var step = Math.Min(250, target - _audio.Time);
            _now += TimeSpan.FromMilliseconds(step);
            _audio.Time += step;
            _audio.RaisePositionChanged();
        }
    }

    [Fact]
    public void Next_early_in_a_song_counts_a_skip_and_not_a_play()
    {
        var song = T("Song", ThreeMinutes);
        var next = T("Next", ThreeMinutes);
        var vm = Player(song, next);
        vm.Play(song);

        HearUpTo(TimeSpan.FromSeconds(10));
        vm.Next();

        Assert.Equal(1, song.SkipCount);
        Assert.Equal(0, song.PlayCount);
        Assert.Null(song.LastPlayedAt);
        Assert.Equal(0, next.SkipCount);
    }

    [Fact]
    public void Next_once_a_song_has_had_its_chance_is_not_a_skip()
    {
        var song = T("Song", ThreeMinutes);
        var next = T("Next", ThreeMinutes);
        var vm = Player(song, next);
        vm.Play(song);

        HearUpTo(ListenMeter.SkipWithin + TimeSpan.FromSeconds(1));
        vm.Next();

        Assert.Equal(0, song.SkipCount);
    }

    // Choosing a song is not rejecting the one playing.
    [Fact]
    public void Picking_another_song_early_is_not_a_skip()
    {
        var song = T("Song", ThreeMinutes);
        var other = T("Other", ThreeMinutes);
        var vm = Player(song, other);
        vm.Play(song);

        HearUpTo(TimeSpan.FromSeconds(10));
        vm.Play(other);

        Assert.Equal(0, song.SkipCount);
    }

    // A song shorter than the window, heard through to its last seconds, was
    // listened to.
    [Fact]
    public void Next_after_a_short_song_has_counted_as_a_play_is_not_a_skip()
    {
        var jingle = T("Jingle", TimeSpan.FromSeconds(20));
        var next = T("Next", ThreeMinutes);
        var vm = Player(jingle, next);
        vm.Play(jingle);

        HearUpTo(TimeSpan.FromSeconds(19.5));
        vm.Next();

        Assert.Equal(1, jingle.PlayCount);
        Assert.Equal(0, jingle.SkipCount);
    }
}
