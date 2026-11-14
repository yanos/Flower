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

[Collection("PlatformDataDirectory")]
public class CurrentlyPlayingControlViewModelTests : PinnedDataDirectory
{
    private CurrentlyPlayingControlViewModel Make(
        out FakeAudioManager audio, out PlaylistControlViewModel playback, params Track[] tracks) =>
        Make(out audio, out playback, out _, tracks);

    private CurrentlyPlayingControlViewModel Make(
        out FakeAudioManager audio, out PlaylistControlViewModel playback, out Library library, params Track[] tracks)
    {
        audio = new FakeAudioManager();
        library = new Library(tracks.ToList());
        playback = Own(new PlaylistControlViewModel(
            audio, new MainPlaylist(tracks.ToList()), library, new AppSettings(),
            new AppSettingsStore(NullLogger<AppSettingsStore>.Instance),
            NullLogger<PlaylistControlViewModel>.Instance));
        playback.OffPlaybackThread = work => work();
        playback.OnUiThread = work => work();
        return Own(new CurrentlyPlayingControlViewModel(
            playback, audio, library, new AlbumArtLoader(null, null, NullLogger<AlbumArtLoader>.Instance), NullLogger<CurrentlyPlayingControlViewModel>.Instance));
    }

    private static Track T(string title) => new()
    {
        Title = title, Path = $"/music/{title}.mp3", Artists = "An Artist", Album = "An Album", Year = "1999",
        Duration = TimeSpan.FromSeconds(215),
    };

    [Fact]
    public void With_nothing_playing_the_bar_has_no_track()
    {
        var vm = Make(out _, out _);

        Assert.Null(vm.CurrentlyPlayingTrack);
    }

    [Fact]
    public void TotalTime_is_null_when_neither_source_knows_a_duration()
    {
        var vm = Make(out _, out _);

        Assert.Null(vm.TotalTime);
    }

    // The seek debounce exists because firing a native seek per pointer-move
    // tick wedged the decode pipeline. Nothing may reach the audio manager
    // until the drag pauses.
    [Fact]
    public void Dragging_the_seek_bar_does_not_seek_immediately()
    {
        var vm = Make(out var audio, out _);
        audio.IsPlaying = true;
        audio.Position = 0;

        vm.SeekPosition = 0.1;
        vm.SeekPosition = 0.2;
        vm.SeekPosition = 0.3;

        Assert.Equal(0, audio.Position);
        Assert.Equal(0.3, vm.SeekPosition); // the slider itself still tracks the drag
    }

    [Fact]
    public void Only_the_last_position_after_the_drag_settles_is_seeked_to()
    {
        var vm = Make(out var audio, out _);
        audio.IsPlaying = true;

        vm.SeekPosition = 0.1;
        vm.SeekPosition = 0.9;

        Assert.True(SpinWait.SpinUntil(() => audio.Position > 0, TimeSpan.FromSeconds(5)),
                    "the debounced seek never reached the audio manager");
        Assert.Equal(0.9f, audio.Position, 3);
    }

    // Nothing is playing, so there is nothing to seek in - a slider reset must
    // not turn into a seek.
    [Fact]
    public void Setting_SeekPosition_while_stopped_never_seeks()
    {
        var vm = Make(out var audio, out _);
        audio.IsPlaying = false;

        vm.SeekPosition = 0.5;

        Thread.Sleep(300); // longer than the 150ms debounce
        Assert.Equal(0, audio.Position);
    }

    [Fact]
    public void Title_artist_and_album_lines_are_the_track_once_a_track_is_playing()
    {
        var track = T("A");
        var vm = Make(out _, out var playback, track);

        playback.Play(track);

        Assert.Equal("A", vm.Title);
        Assert.Equal("An Artist", vm.ArtistLine);
        Assert.Same(track, vm.CurrentlyPlayingTrack);
    }

    [Fact]
    public void A_track_change_re_raises_the_title_artist_and_total_time()
    {
        var track = T("A");
        var vm = Make(out _, out var playback, track);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        playback.Play(track);

        Assert.Contains(nameof(vm.CurrentlyPlayingTrack), raised);
        Assert.Contains(nameof(vm.Title), raised);
        Assert.Contains(nameof(vm.ArtistLine), raised);
        Assert.Contains(nameof(vm.TotalTime), raised);
    }

    // The track's own tagged duration wins over the audio manager's, which is
    // only known once the file has actually been opened.
    [Fact]
    public void TotalTime_prefers_the_tracks_duration_over_the_audio_managers()
    {
        var track = T("A");
        var vm = Make(out var audio, out var playback, track);
        audio.Length = (long)TimeSpan.FromSeconds(999).TotalMilliseconds;

        playback.Play(track);

        Assert.Equal("3:35", vm.TotalTime); // 215s, not 999s
    }

    [Fact]
    public void TotalTime_falls_back_to_the_audio_manager_for_an_untagged_duration()
    {
        var track = new Track { Title = "A", Path = "/music/A.mp3", Duration = TimeSpan.Zero };
        var vm = Make(out var audio, out var playback, track);
        audio.Length = (long)TimeSpan.FromSeconds(90).TotalMilliseconds;

        playback.Play(track);

        Assert.Equal("1:30", vm.TotalTime);
    }

    [Fact]
    public void Repeat_and_shuffle_are_read_from_the_playback_view_model_and_re_raised()
    {
        var vm = Make(out _, out var playback);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        playback.ToggleRepeat();
        playback.ToggleShuffle();

        Assert.Equal(playback.IsRepeatEnabled, vm.IsRepeatEnabled);
        Assert.Equal(playback.IsShuffleEnabled, vm.IsShuffleEnabled);
        Assert.True(vm.IsRepeatEnabled);
        Assert.True(vm.IsShuffleEnabled);
        Assert.Contains(nameof(vm.IsRepeatEnabled), raised);
        Assert.Contains(nameof(vm.IsShuffleEnabled), raised);
    }
}
