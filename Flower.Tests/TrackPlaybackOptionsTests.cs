using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Models;
using Flower.Persistence;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests;

// The three per-track playback options set on Track Info's Options tab, tested
// where they actually take effect rather than where they are edited: shuffle's
// candidate list, the seek on start, and the volume around a track.
//
// PlatformDataDirectory is pinned for the same reason
// PlaylistControlViewModelTests pins it - Play() and the resume-position write
// both persist through a real store, and an unpinned run puts them in the
// developer's own library.
[Collection("PlatformDataDirectory")]
public class TrackPlaybackOptionsTests : IDisposable
{
    private readonly string _tempHome;

    public TrackPlaybackOptionsTests()
    {
        _tempHome = Path.Combine(Path.GetTempPath(), "flower-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempHome);
        PlatformDataDirectory.Current = _tempHome;
    }

    public void Dispose()
    {
        PlatformDataDirectory.Current = AssemblySetup.DefaultDataDirectory;
        try { Directory.Delete(_tempHome, recursive: true); } catch { /* best effort */ }
    }

    private static Track T(string title) => new() { Title = title, Path = $"/music/{title}.mp3" };

    private static Track T(string title, TimeSpan duration) =>
        new() { Title = title, Path = $"/music/{title}.mp3", Duration = duration };

    private static PlaylistControlViewModel MakeViewModel(List<Track> tracks, out FakeAudioManager audio, bool shuffle = false)
    {
        audio = new FakeAudioManager();
        return new PlaylistControlViewModel(
            audio,
            new MainPlaylist(tracks),
            new Library(tracks),
            new AppSettings { IsShuffleEnabled = shuffle },
            new AppSettingsStore(NullLogger<AppSettingsStore>.Instance),
            NullLogger<PlaylistControlViewModel>.Instance);
    }

    // ── Skip when shuffling ────────────────────────────────────────────────

    [Fact]
    public void Shuffle_never_lands_on_a_track_marked_to_be_skipped()
    {
        var a = T("A");
        var skipped = T("Interlude");
        skipped.IgnoreWhenShuffling = true;
        var c = T("C");

        var vm = MakeViewModel([a, skipped, c], out var audio, shuffle: true);

        // Enough rolls that a 1-in-2 chance of hitting the skipped track would
        // have done so long ago if the exclusion were not being applied.
        for (var i = 0; i < 200; i++)
        {
            vm.Play(a);
            vm.Next();
            Assert.NotSame(skipped, vm.CurrentlyPlayingTrack);
        }
    }

    [Fact]
    public void A_skipped_track_still_plays_when_the_queue_reaches_it_in_order()
    {
        var a = T("A");
        var skipped = T("Interlude");
        skipped.IgnoreWhenShuffling = true;

        var vm = MakeViewModel([a, skipped], out _);

        vm.Play(a);
        vm.Next();

        Assert.Same(skipped, vm.CurrentlyPlayingTrack);
    }

    // The marks cannot be allowed to mean "refuse to advance": a queue where
    // every other slot is excluded still has to go somewhere.
    [Fact]
    public void Shuffle_falls_back_to_an_unrestricted_pick_when_every_other_track_is_skipped()
    {
        var a = T("A");
        var b = T("B");
        b.IgnoreWhenShuffling = true;

        var vm = MakeViewModel([a, b], out _, shuffle: true);

        vm.Play(a);
        vm.Next();

        Assert.Same(b, vm.CurrentlyPlayingTrack);
    }

    // ── Remember playback position ─────────────────────────────────────────

    [Fact]
    public void A_paused_track_records_where_it_got_to()
    {
        var track = T("Podcast");
        track.RememberPlaybackPosition = true;
        track.Duration = TimeSpan.FromMinutes(60);

        var vm = MakeViewModel([track], out var audio);
        vm.Play(track);

        audio.Time = 90_000;
        audio.RaisePositionChanged();
        audio.RaisePaused();

        Assert.Equal(TimeSpan.FromSeconds(90), track.ResumePosition);
    }

    [Fact]
    public void A_track_without_the_option_records_nothing()
    {
        var track = T("Song");
        var vm = MakeViewModel([track], out var audio);
        vm.Play(track);

        audio.Time = 90_000;
        audio.RaisePositionChanged();
        audio.RaisePaused();

        Assert.Null(track.ResumePosition);
    }

    [Fact]
    public void Playing_a_remembered_track_seeks_to_where_it_left_off()
    {
        var track = T("Podcast");
        track.RememberPlaybackPosition = true;
        track.ResumePosition = TimeSpan.FromMinutes(15);
        track.Duration = TimeSpan.FromMinutes(60);

        var vm = MakeViewModel([track], out var audio);
        audio.Length = (long)TimeSpan.FromMinutes(60).TotalMilliseconds;

        vm.Play(track);
        audio.RaisePlaying();

        Assert.Equal(0.25f, audio.Position, 3);
    }

    // A position a few seconds in is not worth restoring, and one a few seconds
    // from the end would restart the track only for it to end again.
    [Theory]
    [InlineData(2)]
    [InlineData(3598)]
    public void An_edge_position_starts_the_track_from_the_beginning(int seconds)
    {
        var track = T("Podcast");
        track.RememberPlaybackPosition = true;
        track.ResumePosition = TimeSpan.FromSeconds(seconds);
        track.Duration = TimeSpan.FromMinutes(60);

        var vm = MakeViewModel([track], out var audio);
        audio.Length = (long)TimeSpan.FromMinutes(60).TotalMilliseconds;

        vm.Play(track);
        audio.RaisePlaying();

        Assert.Equal(0f, audio.Position);
    }

    [Fact]
    public void Reaching_the_end_of_a_remembered_track_clears_its_position()
    {
        var track = T("Podcast");
        track.RememberPlaybackPosition = true;
        track.Duration = TimeSpan.FromMinutes(60);

        var vm = MakeViewModel([track], out var audio);
        vm.Play(track);

        audio.Time = 3_599_000;
        audio.RaisePositionChanged();
        audio.RaiseEndReached();

        Assert.Null(track.ResumePosition);
    }

    // Decode-ahead splices the next track in from its very first sample, which
    // is the one thing a track that resumes part-way through must not do.
    [Fact]
    public void A_track_that_will_resume_is_never_armed_for_gapless_handover()
    {
        var a = T("A");
        var podcast = T("Podcast");
        podcast.RememberPlaybackPosition = true;
        podcast.ResumePosition = TimeSpan.FromMinutes(15);
        podcast.Duration = TimeSpan.FromMinutes(60);

        var vm = MakeViewModel([a, podcast], out var audio);
        vm.Play(a);

        Assert.Null(audio.LastUpcoming);
    }

    [Fact]
    public void A_remembered_track_with_nothing_recorded_yet_is_armed_normally()
    {
        var a = T("A");
        var podcast = T("Podcast");
        podcast.RememberPlaybackPosition = true;
        podcast.Duration = TimeSpan.FromMinutes(60);

        var vm = MakeViewModel([a, podcast], out var audio);
        vm.Play(a);

        Assert.Same(podcast, audio.LastUpcoming);
    }

    // ── Volume adjustment ──────────────────────────────────────────────────
    //
    // The adjustment is an offset (IAudioManager.VolumeOffset), never a write
    // to Volume: the slider shows the user's own setting and must not wander
    // because a track happens to be quiet. So every assertion here is about
    // EffectiveVolume - what the sink is actually driven with - plus Volume
    // staying exactly where the user left it.

    [Fact]
    public void A_tracks_volume_adjustment_is_applied_on_start_and_taken_off_at_the_end()
    {
        var quiet = T("Quiet");
        quiet.VolumeAdjustment = 20;

        var vm = MakeViewModel([quiet], out var audio);
        audio.Volume = 60;

        vm.Play(quiet);
        Assert.Equal(80, audio.EffectiveVolume);
        Assert.Equal(60, audio.Volume);

        audio.RaiseStopped();
        Assert.Equal(60, audio.EffectiveVolume);
        Assert.Equal(60, audio.Volume);
    }

    [Fact]
    public void The_adjusted_volume_is_clamped_rather_than_wrapped()
    {
        var loud = T("Loud");
        loud.VolumeAdjustment = 50;

        var vm = MakeViewModel([loud], out var audio);
        audio.Volume = 80;

        vm.Play(loud);

        Assert.Equal(100, audio.EffectiveVolume);
    }

    // The whole point of an offset rather than a write: the user reaching for
    // the slider mid-track sets the volume they want, and the adjustment goes
    // on applying against the new value instead of fighting it or being
    // abandoned.
    [Fact]
    public void A_volume_the_user_changed_mid_track_keeps_the_adjustment_on_top_of_it()
    {
        var quiet = T("Quiet");
        quiet.VolumeAdjustment = 20;
        var next = T("Next");

        var vm = MakeViewModel([quiet, next], out var audio);
        audio.Volume = 60;

        vm.Play(quiet);
        audio.Volume = 35;
        Assert.Equal(55, audio.EffectiveVolume);

        vm.Play(next);
        Assert.Equal(35, audio.Volume);
        Assert.Equal(35, audio.EffectiveVolume);
    }

    [Fact]
    public void Moving_between_two_adjusted_tracks_measures_each_against_the_users_own_volume()
    {
        var first = T("First");
        first.VolumeAdjustment = 20;
        var second = T("Second");
        second.VolumeAdjustment = -10;

        var vm = MakeViewModel([first, second], out var audio);
        audio.Volume = 50;

        vm.Play(first);
        Assert.Equal(70, audio.EffectiveVolume);

        vm.Play(second);
        Assert.Equal(40, audio.EffectiveVolume);
        Assert.Equal(50, audio.Volume);
    }

    // ── Quitting mid-track ─────────────────────────────────────────────────

    // The case the option exists for and the one every event-driven save
    // misses: nothing is paused or stopped, the app is simply going away. See
    // PlaylistControlViewModel.SavePlaybackState, wired in App.axaml.cs.
    [Fact]
    public void Quitting_mid_track_saves_the_position()
    {
        var podcast = T("Podcast", TimeSpan.FromHours(1));
        podcast.RememberPlaybackPosition = true;

        var vm = MakeViewModel([podcast], out var audio);
        vm.Play(podcast);

        audio.Time = 900_000;
        audio.RaisePositionChanged();

        vm.SavePlaybackState();

        Assert.Equal(TimeSpan.FromMinutes(15), podcast.ResumePosition);
    }

    [Fact]
    public void Quitting_while_a_track_without_the_option_plays_records_nothing()
    {
        var song = T("Song");

        var vm = MakeViewModel([song], out var audio);
        vm.Play(song);

        audio.Time = 30_000;
        audio.RaisePositionChanged();

        vm.SavePlaybackState();

        Assert.Null(song.ResumePosition);
    }
}
