using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Flower.Manager;
using Flower.Models;
using Flower.Persistence;
using Flower.ViewModels;

namespace Flower.Tests;

public class PlaylistControlViewModelTests
{
    // Minimal stand-in for VlcAudioManager - PlaylistControlViewModel only ever
    // calls Play() on it directly (Resume/Pause/Stop are driven by user actions
    // this test suite doesn't exercise), and its events are wired up but never
    // raised here since doing so would run the EndReached handler's
    // Dispatcher.UIThread.Post callback, which needs a running Avalonia
    // dispatcher this headless test host doesn't have.
    private sealed class FakeAudioManager : IAudioManager
    {
        public bool IsPlaying { get; set; }
        public int Volume { get; set; }
        public float Position { get; set; }
        public long Time { get; set; }
        public long Length { get; set; }

        public Track? LastPlayed { get; private set; }

        public void Play(Track track) => LastPlayed = track;
        public void Resume() { }
        public void Pause() { }
        public void Stop() { }

#pragma warning disable CS0067 // required by IAudioManager, unused by these tests
        public event EventHandler? Paused;
        public event EventHandler? Stopped;
        public event EventHandler? Playing;
        public event EventHandler? PositionChanged;
        public event EventHandler? VolumeChanged;
        public event EventHandler? EndReached;
#pragma warning restore CS0067
    }

    private static Track T(string title, string? path = null) =>
        new Track { Title = title, Path = path ?? $"/music/{title}.mp3" };

    private static PlaylistControlViewModel MakeViewModel(
        List<Track> tracks, out FakeAudioManager audio, AppSettings? appSettings = null)
    {
        audio = new FakeAudioManager();
        var library = new Library(tracks);
        var playlist = new MainPlaylist(tracks);
        var libraryStore = new LibraryStore(NullLogger<LibraryStore>.Instance);
        var appSettingsStore = new AppSettingsStore(NullLogger<AppSettingsStore>.Instance);
        return new PlaylistControlViewModel(
            audio, playlist, library, appSettings ?? new AppSettings(), libraryStore, appSettingsStore,
            NullLogger<PlaylistControlViewModel>.Instance);
    }

    [Fact]
    public void Constructor_restores_repeat_and_shuffle_state_from_AppSettings()
    {
        var settings = new AppSettings { IsRepeatEnabled = true, IsShuffleEnabled = true };
        var vm = MakeViewModel(new List<Track> { T("A") }, out _, settings);

        Assert.True(vm.IsRepeatEnabled);
        Assert.True(vm.IsShuffleEnabled);
    }

    [Fact]
    public void ToggleRepeat_flips_IsRepeatEnabled_and_updates_the_shared_AppSettings_instance()
    {
        var settings = new AppSettings();
        var vm = MakeViewModel(new List<Track> { T("A") }, out _, settings);

        vm.ToggleRepeat();

        Assert.True(vm.IsRepeatEnabled);
        Assert.True(settings.IsRepeatEnabled);

        vm.ToggleRepeat();

        Assert.False(vm.IsRepeatEnabled);
        Assert.False(settings.IsRepeatEnabled);
    }

    [Fact]
    public void ToggleShuffle_flips_IsShuffleEnabled_and_updates_the_shared_AppSettings_instance()
    {
        var settings = new AppSettings();
        var vm = MakeViewModel(new List<Track> { T("A") }, out _, settings);

        vm.ToggleShuffle();

        Assert.True(vm.IsShuffleEnabled);
        Assert.True(settings.IsShuffleEnabled);
    }

    [Fact]
    public void Play_sets_CurrentlyPlayingTrack_and_SelectedTrack_and_calls_the_audio_manager()
    {
        var track = T("A");
        var vm = MakeViewModel(new List<Track> { track }, out var audio);

        vm.Play(track);

        Assert.Same(track, vm.CurrentlyPlayingTrack);
        Assert.Same(track, vm.SelectedTrack);
        Assert.Same(track, audio.LastPlayed);
    }

    [Fact]
    public void Next_without_shuffle_follows_playlist_order()
    {
        var a = T("A");
        var b = T("B");
        var vm = MakeViewModel(new List<Track> { a, b }, out _);
        vm.Play(a);

        vm.Next();

        Assert.Same(b, vm.CurrentlyPlayingTrack);
    }

    [Fact]
    public void Next_without_shuffle_wraps_around_to_the_first_track_after_the_last()
    {
        var a = T("A");
        var b = T("B");
        var vm = MakeViewModel(new List<Track> { a, b }, out _);
        vm.Play(b);

        vm.Next();

        Assert.Same(a, vm.CurrentlyPlayingTrack);
    }

    [Fact]
    public void Next_with_shuffle_enabled_never_returns_the_current_track_when_more_than_one_track_exists()
    {
        var tracks = new List<Track> { T("A"), T("B"), T("C") };
        var vm = MakeViewModel(tracks, out _);
        vm.IsShuffleEnabled = true;
        vm.Play(tracks[0]);

        for (int i = 0; i < 20; i++)
        {
            var before = vm.CurrentlyPlayingTrack;
            vm.Next();
            Assert.NotSame(before, vm.CurrentlyPlayingTrack);
            Assert.Contains(vm.CurrentlyPlayingTrack, tracks);
        }
    }

    [Fact]
    public void Next_with_shuffle_enabled_and_a_single_track_returns_that_track_without_hanging()
    {
        var only = T("Only");
        var vm = MakeViewModel(new List<Track> { only }, out _);
        vm.IsShuffleEnabled = true;
        vm.Play(only);

        vm.Next();

        Assert.Same(only, vm.CurrentlyPlayingTrack);
    }
}
