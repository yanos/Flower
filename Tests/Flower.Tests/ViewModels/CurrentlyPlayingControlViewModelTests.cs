using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

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
    private static CurrentlyPlayingControlViewModel Make(
        out FakeAudioManager audio, out PlaylistControlViewModel playback, params Track[] tracks)
    {
        audio = new FakeAudioManager();
        var library = new Library(tracks.ToList());
        playback = new PlaylistControlViewModel(
            audio, new MainPlaylist(tracks.ToList()), library, new AppSettings(),
            new AppSettingsStore(NullLogger<AppSettingsStore>.Instance),
            NullLogger<PlaylistControlViewModel>.Instance);
        return new CurrentlyPlayingControlViewModel(
            playback, audio, library, new AlbumArtLoader(null, null, NullLogger<AlbumArtLoader>.Instance), NullLogger<CurrentlyPlayingControlViewModel>.Instance);
    }

    private static Track T(string title) => new()
    {
        Title = title, Path = $"/music/{title}.mp3", Artists = "An Artist", Album = "An Album", Year = "1999",
        Duration = TimeSpan.FromSeconds(215),
    };

    // Deliberately a single space, not empty and not hidden: the control is
    // always rendered so its height stays constant whether or not anything is
    // playing.
    [Fact]
    public void Subtitle_is_a_single_space_with_nothing_playing()
    {
        var vm = Make(out _, out _);

        Assert.Equal(" ", vm.Subtitle);
        Assert.Null(vm.CurrentlyPlayingTrack);
    }

    [AvaloniaFact]
    public void Subtitle_is_artist_album_and_year_once_a_track_is_playing()
    {
        var track = T("A");
        var vm = Make(out _, out var playback, track);

        playback.Play(track);

        Assert.Equal("An Artist — An Album (1999)", vm.Subtitle);
    }

    [AvaloniaFact]
    public void A_track_change_re_raises_the_track_subtitle_and_total_time()
    {
        var track = T("A");
        var vm = Make(out _, out var playback, track);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        playback.Play(track);

        Assert.Contains(nameof(vm.CurrentlyPlayingTrack), raised);
        Assert.Contains(nameof(vm.Subtitle), raised);
        Assert.Contains(nameof(vm.TotalTime), raised);
    }

    [Fact]
    public void ElapsedTime_is_null_before_playback_starts_and_formatted_after()
    {
        var vm = Make(out var audio, out _);

        Assert.Null(vm.ElapsedTime);

        audio.Time = (long)TimeSpan.FromSeconds(75).TotalMilliseconds;
        Assert.Equal("1:15", vm.ElapsedTime);

        audio.Time = (long)TimeSpan.FromMinutes(61).TotalMilliseconds;
        Assert.Equal("1:01:00", vm.ElapsedTime);
    }

    // The track's own tagged duration wins over the audio manager's, which is
    // only known once the file has actually been opened.
    [AvaloniaFact]
    public void TotalTime_prefers_the_tracks_duration_over_the_audio_managers()
    {
        var track = T("A");
        var vm = Make(out var audio, out var playback, track);
        audio.Length = (long)TimeSpan.FromSeconds(999).TotalMilliseconds;

        playback.Play(track);

        Assert.Equal("3:35", vm.TotalTime); // 215s, not 999s
    }

    [AvaloniaFact]
    public void TotalTime_falls_back_to_the_audio_manager_for_an_untagged_duration()
    {
        var track = new Track { Title = "A", Path = "/music/A.mp3", Duration = TimeSpan.Zero };
        var vm = Make(out var audio, out var playback, track);
        audio.Length = (long)TimeSpan.FromSeconds(90).TotalMilliseconds;

        playback.Play(track);

        Assert.Equal("1:30", vm.TotalTime);
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

    // Position updates coming *from* the audio manager drive the slider, and
    // must not bounce straight back out as a seek.
    [AvaloniaFact]
    public void A_position_update_from_the_audio_manager_moves_the_slider_without_seeking_back()
    {
        var vm = Make(out var audio, out _);
        audio.IsPlaying = true;
        audio.Position = 0.42f;

        audio.RaisePositionChanged();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0.42, vm.SeekPosition, 3);

        // No debounced seek was armed by that. Playback moving on afterwards
        // must therefore stand: without the _isUpdatingFromAudio guard the
        // debounce fires here and stomps the position back to 0.42.
        audio.Position = 0.55f;
        Thread.Sleep(400);
        Assert.Equal(0.55f, audio.Position, 3);
    }

    [AvaloniaFact]
    public void Stopping_resets_the_slider_to_zero()
    {
        var vm = Make(out var audio, out _);
        audio.IsPlaying = true;
        audio.Position = 0.8f;
        audio.RaisePositionChanged();
        Dispatcher.UIThread.RunJobs();

        audio.IsPlaying = false;
        audio.RaiseStopped();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, vm.SeekPosition);
    }

    [AvaloniaFact]
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

    // A live peer-stream URL is a Path that no filesystem read can satisfy, and
    // reading it as one threw TagLib/IO exceptions per track change. It still
    // goes to the shared loader - that is where art for a track living on
    // another machine comes from - but by the remote road, not the local one
    // (see AlbumArtLoader.IsLocalFile).
    [AvaloniaFact]
    public void A_peer_stream_url_is_never_read_as_a_file_path()
    {
        var streamed = new Track { Title = "Remote", Path = "http://192.168.1.5:5001/stream/7" };
        var audio = new FakeAudioManager();
        var library = new Library(new List<Track> { streamed });
        var playback = new PlaylistControlViewModel(
            audio, new MainPlaylist(new List<Track> { streamed }), library, new AppSettings(),
            new AppSettingsStore(NullLogger<AppSettingsStore>.Instance),
            NullLogger<PlaylistControlViewModel>.Instance);
        var logger = new RecordingLogger<CurrentlyPlayingControlViewModel>();
        var loader = new RecordingArtLoader();
        var vm = new CurrentlyPlayingControlViewModel(playback, audio, library, loader, logger);

        playback.Play(streamed);
        Thread.Sleep(200); // the art load, if attempted, runs on a Task.Run
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.AlbumArt);
        Assert.DoesNotContain(logger.Messages, m => m.Contains("Could not read/decode"));
        Assert.Equal([streamed], loader.Asked);
    }

    // The now-playing panel used to run its own filesystem-only art lookup, so a
    // track with no local file - every track in a browser tab, and every
    // not-yet-downloaded placeholder on a desktop - was skipped before anything
    // was even attempted. Seen in a real tab as covers on every row (those go
    // through AlbumArtLoader, which knows to fetch from the origin) and a blank
    // square in the top bar.
    [AvaloniaFact]
    public void A_track_with_no_local_file_still_asks_for_its_art()
    {
        var placeholder = new Track { Title = "On A Server", Album = "Remote Album", Path = null };
        var audio = new FakeAudioManager();
        var library = new Library(new List<Track> { placeholder });
        var loader = new RecordingArtLoader();
        var vm = NowPlaying(placeholder, audio, library, loader);

        vm.Playback.Play(placeholder);
        Thread.Sleep(200);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([placeholder], loader.Asked);
    }

    // Art arrives asynchronously now that it can come off the network, so the
    // answer to a track the user has already skipped past must not overwrite
    // the answer to the one actually playing - the same out-of-order hazard
    // PlaylistControlViewModel guards when it resolves a stream URL.
    [AvaloniaFact]
    public void Art_that_arrives_after_the_track_changed_is_dropped()
    {
        var first = new Track { Title = "First", Album = "A", Path = null };
        var second = new Track { Title = "Second", Album = "B", Path = null };
        var audio = new FakeAudioManager();
        var library = new Library(new List<Track> { first, second });
        var loader = new RecordingArtLoader();
        var vm = NowPlaying(first, audio, library, loader, new MainPlaylist(new List<Track> { first, second }));

        loader.Gate = new TaskCompletionSource();
        vm.Playback.Play(first);
        vm.Playback.Play(second);
        loader.Gate.SetResult();          // let the first track's stale load finish last
        Thread.Sleep(200);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.Vm.AlbumArt);
        // Both were asked, in no promised order: the view model kicks each load
        // off with its own Task.Run, so which one reaches the loader first is up
        // to the thread pool. Asserting the sequence made this test fail under a
        // shuffled run order (see ShuffleOrderer) - a real defect in the test,
        // since the claim here is that the stale *answer* is dropped, which the
        // AlbumArt assertion above is what actually checks.
        Assert.Equal([first, second], loader.Asked.OrderBy(t => t.Title).ToList());
    }

    // Counts what was asked for rather than producing a bitmap: decoding a real
    // image would test Skia, and the claim under test is only which tracks
    // reach the shared loader at all.
    private sealed class RecordingArtLoader()
        : AlbumArtLoader(null, null, NullLogger<AlbumArtLoader>.Instance)
    {
        public List<Track> Asked { get; } = new();
        public TaskCompletionSource? Gate { get; set; }

        public override async Task<Avalonia.Media.Imaging.Bitmap?> LoadAsync(Track track)
        {
            lock (Asked)
                Asked.Add(track);

            if (Gate != null)
                await Gate.Task;

            return null;
        }
    }

    // A URL already in hand, so playback starts on the calling stack exactly as
    // it does for a local file - the deferred case has its own coverage in
    // PlaylistPlaybackIntegrationTests.
    private sealed class ImmediateStreamUrls : IStreamUrlResolver
    {
        public Task<string?> ResolveAsync(Track track) =>
            Task.FromResult<string?>($"http://origin.local/rest/stream?id={track.OriginTrackId}");
    }

    private sealed record NowPlayingHarness(
        CurrentlyPlayingControlViewModel Vm, PlaylistControlViewModel Playback)
    {
        public Avalonia.Media.Imaging.Bitmap? AlbumArt => Vm.AlbumArt;
    }

    private static NowPlayingHarness NowPlaying(
        Track track, FakeAudioManager audio, Library library, AlbumArtLoader loader,
        MainPlaylist? playlist = null)
    {
        // A placeholder is unplayable without one, so Play would return before
        // ever changing the current track - see PlaylistControlViewModel's own
        // note on why this dependency is nullable.
        var playback = new PlaylistControlViewModel(
            audio, playlist ?? new MainPlaylist(new List<Track> { track }), library, new AppSettings(),
            new AppSettingsStore(NullLogger<AppSettingsStore>.Instance),
            NullLogger<PlaylistControlViewModel>.Instance,
            new ImmediateStreamUrls());
        var vm = new CurrentlyPlayingControlViewModel(
            playback, audio, library, loader, NullLogger<CurrentlyPlayingControlViewModel>.Instance);
        return new NowPlayingHarness(vm, playback);
    }

    // Records what was logged so a test can assert a code path was never taken.
    private sealed class RecordingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel,
                                Microsoft.Extensions.Logging.EventId eventId,
                                TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Messages)
                Messages.Add(formatter(state, exception));
        }
    }
}
