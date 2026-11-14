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
public class CurrentlyPlayingDispatcherTests : PinnedDataDirectory
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
        return Own(new CurrentlyPlayingControlViewModel(
            playback, audio, library, new AlbumArtLoader(null, null, NullLogger<AlbumArtLoader>.Instance), NullLogger<CurrentlyPlayingControlViewModel>.Instance));
    }

    private static Track T(string title) => new()
    {
        Title = title, Path = $"/music/{title}.mp3", Artists = "An Artist", Album = "An Album", Year = "1999",
        Duration = TimeSpan.FromSeconds(215),
    };

    // The player bar's star stars the song playing in the library itself,
    // and follows a star set anywhere else.
    [AvaloniaFact]
    public void The_star_is_the_playing_song_s_own()
    {
        var track = T("A");
        var vm = Make(out _, out var playback, out var library, track);
        playback.Play(track);
        Assert.False(vm.IsStarred);

        vm.ToggleStar();
        Assert.True(track.Starred);
        Assert.True(vm.IsStarred);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        library.SetStarred(StarTarget.Song, track.Id.ToString(), false);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsStarred);
        Assert.Contains(nameof(vm.IsStarred), raised);
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

    // A click on the bar waits out the debounce before it seeks, and the
    // position ticks carry on meanwhile. One landing in that window used to
    // put the thumb back where the audio still was, before the seek took it
    // to the click again.
    [AvaloniaFact]
    public void A_position_update_before_the_seek_is_sent_does_not_pull_the_thumb_back()
    {
        var vm = Make(out var audio, out _);
        audio.IsPlaying = true;
        audio.Position = 0.2f;

        vm.SeekPosition = 0.7;
        audio.RaisePositionChanged();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0.7, vm.SeekPosition, 3);
    }

    // Once the seek has gone out, the thumb follows the audio again.
    [AvaloniaFact]
    public void After_the_seek_is_sent_the_thumb_follows_playback_again()
    {
        var vm = Make(out var audio, out _);
        audio.IsPlaying = true;
        audio.Position = 0.2f;

        vm.SeekPosition = 0.7;
        Assert.True(SpinWait.SpinUntil(() => Math.Abs(audio.Position - 0.7f) < 0.001f, TimeSpan.FromSeconds(5)),
                    "the debounced seek never reached the audio manager");

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        do
        {
            Dispatcher.UIThread.RunJobs();
            audio.Position = 0.75f;
            audio.RaisePositionChanged();
            Dispatcher.UIThread.RunJobs();
        }
        while (Math.Abs(vm.SeekPosition - 0.75) > 0.001 && DateTime.UtcNow < deadline);

        Assert.Equal(0.75, vm.SeekPosition, 3);
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
        var playback = Own(new PlaylistControlViewModel(
            audio, new MainPlaylist(new List<Track> { streamed }), library, new AppSettings(),
            new AppSettingsStore(NullLogger<AppSettingsStore>.Instance),
            NullLogger<PlaylistControlViewModel>.Instance));
        var logger = new RecordingLogger<CurrentlyPlayingControlViewModel>();
        var loader = new RecordingArtLoader();
        var vm = Own(new CurrentlyPlayingControlViewModel(playback, audio, library, loader, logger));

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
        // Each load starts on the thread pool, which a simulator under Mono
        // can take longer than any fixed sleep to get to.
        Assert.True(SpinWait.SpinUntil(() =>
        {
            lock (loader.Asked)
                return loader.Asked.Count == 2;
        }, TimeSpan.FromSeconds(10)));
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
        lock (loader.Asked)
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

    private NowPlayingHarness NowPlaying(
        Track track, FakeAudioManager audio, Library library, AlbumArtLoader loader,
        MainPlaylist? playlist = null)
    {
        // A placeholder is unplayable without one, so Play would return before
        // ever changing the current track - see PlaylistControlViewModel's own
        // note on why this dependency is nullable.
        var playback = Own(new PlaylistControlViewModel(
            audio, playlist ?? new MainPlaylist(new List<Track> { track }), library, new AppSettings(),
            new AppSettingsStore(NullLogger<AppSettingsStore>.Instance),
            NullLogger<PlaylistControlViewModel>.Instance,
            new ImmediateStreamUrls()));
        var vm = Own(new CurrentlyPlayingControlViewModel(
            playback, audio, library, loader, NullLogger<CurrentlyPlayingControlViewModel>.Instance));
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
