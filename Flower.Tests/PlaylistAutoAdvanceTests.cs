using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Manager;
using Flower.Models;
using Flower.Persistence;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

namespace Flower.Tests;

// PlaylistControlViewModelTests' FakeAudioManager is never actually raised
// there, since running the real EndReached handler needs a Dispatcher.
// These tests supply that (via Avalonia.Headless's [AvaloniaFact]) to cover
// the auto-advance track-selection logic itself - repeat/shuffle/wrap
// choices made when a track naturally ends - without needing real audio or
// LibVLC decode at all, since that logic lives entirely in
// PlaylistControlViewModel/Palylist, not in the audio pipeline.
//
// PlatformDataDirectory.Current is pinned to an isolated temp directory
// (same pattern as StoreRoundTripTests) because the real EndReached handler
// calls LibraryStore.SaveAsync, which would otherwise write to the real
// developer's library.json.
[Collection("PlatformDataDirectory")]
public class PlaylistAutoAdvanceTests : IDisposable
{
    private readonly string _tempHome;

    public PlaylistAutoAdvanceTests()
    {
        _tempHome = Path.Combine(Path.GetTempPath(), "flower-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempHome);
        PlatformDataDirectory.Current = _tempHome;
    }

    public void Dispose()
    {
        PlatformDataDirectory.Current = null;
        try { Directory.Delete(_tempHome, recursive: true); } catch { /* best effort */ }
    }

    private static Track T(string title) => new() { Title = title, Path = $"/music/{title}.mp3" };

    private static PlaylistControlViewModel MakeViewModel(List<Track> tracks, out FakeAudioManager audio)
    {
        audio = new FakeAudioManager();
        var library = new Library(tracks);
        var playlist = new MainPlaylist(tracks);
        var libraryStore = new LibraryStore(NullLogger<LibraryStore>.Instance);
        var appSettingsStore = new AppSettingsStore(NullLogger<AppSettingsStore>.Instance);
        return new PlaylistControlViewModel(
            audio, playlist, library, new AppSettings(), libraryStore, appSettingsStore,
            NullLogger<PlaylistControlViewModel>.Instance);
    }

    // EndReached's handler is async void, awaiting a real (if isolated)
    // disk write before posting the next Play() back onto the dispatcher -
    // pumping Dispatcher.UIThread.RunJobs() in a loop is what lets both
    // that continuation and the posted Play() actually run, since a plain
    // blocking SpinWait on this same thread would just starve the
    // dispatcher instead of letting it process them.
    private static void PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("condition was not met within the timeout");
                return;
            }
            Thread.Sleep(1);
        }
    }

    [AvaloniaFact]
    public void EndReached_auto_advances_to_the_next_track_in_playlist_order()
    {
        var a = T("A");
        var b = T("B");
        var vm = MakeViewModel(new List<Track> { a, b }, out var audio);
        vm.Play(a);

        audio.RaiseEndReached();

        PumpUntil(() => vm.CurrentlyPlayingTrack == b, TimeSpan.FromSeconds(5));
    }

    [AvaloniaFact]
    public void EndReached_wraps_around_to_the_first_track_after_the_last()
    {
        var a = T("A");
        var b = T("B");
        var vm = MakeViewModel(new List<Track> { a, b }, out var audio);
        vm.Play(b);

        audio.RaiseEndReached();

        PumpUntil(() => vm.CurrentlyPlayingTrack == a, TimeSpan.FromSeconds(5));
    }

    [AvaloniaFact]
    public void EndReached_with_repeat_enabled_replays_the_same_track()
    {
        var a = T("A");
        var b = T("B");
        var vm = MakeViewModel(new List<Track> { a, b }, out var audio);
        vm.ToggleRepeat();
        vm.Play(a);

        audio.RaiseEndReached();

        PumpUntil(() => audio.LastPlayed == a, TimeSpan.FromSeconds(5));
        Assert.Same(a, vm.CurrentlyPlayingTrack);
    }

    [AvaloniaFact]
    public void EndReached_with_shuffle_enabled_never_immediately_repeats_the_finished_track()
    {
        var tracks = new List<Track> { T("A"), T("B"), T("C") };
        var vm = MakeViewModel(tracks, out var audio);
        vm.IsShuffleEnabled = true;
        vm.Play(tracks[0]);

        audio.RaiseEndReached();

        PumpUntil(() => vm.CurrentlyPlayingTrack != tracks[0], TimeSpan.FromSeconds(5));
        Assert.Contains(vm.CurrentlyPlayingTrack, tracks);
    }

    // ── Decode failure ───────────────────────────────────────────────────────
    //
    // A file that can't be decoded used to arrive on the same EndReached event
    // a finished track does, so it skipped past silently *and* collected a play
    // count on the way. TrackFailed separates the two.

    [AvaloniaFact]
    public void TrackFailed_advances_to_the_next_track()
    {
        var a = T("A");
        var b = T("B");
        var vm = MakeViewModel(new List<Track> { a, b }, out var audio);
        vm.Play(a);

        audio.RaiseTrackFailed(a);

        PumpUntil(() => vm.CurrentlyPlayingTrack == b, TimeSpan.FromSeconds(5));
    }

    [AvaloniaFact]
    public void TrackFailed_does_not_count_a_play_for_the_track_that_failed()
    {
        var a = T("A");
        var b = T("B");
        var vm = MakeViewModel(new List<Track> { a, b }, out var audio);
        vm.Play(a);

        audio.RaiseTrackFailed(a);

        PumpUntil(() => vm.CurrentlyPlayingTrack == b, TimeSpan.FromSeconds(5));
        Assert.Equal(0, a.PlayCount);
    }

    // Repeat would otherwise retry the same unplayable file forever.
    [AvaloniaFact]
    public void TrackFailed_with_repeat_enabled_moves_on_instead_of_retrying_the_broken_track()
    {
        var a = T("A");
        var b = T("B");
        var vm = MakeViewModel(new List<Track> { a, b }, out var audio);
        vm.ToggleRepeat();
        vm.Play(a);

        audio.RaiseTrackFailed(a);

        PumpUntil(() => vm.CurrentlyPlayingTrack == b, TimeSpan.FromSeconds(5));
    }

    [AvaloniaFact]
    public void TrackFailed_raises_PlaybackFailed_for_the_UI()
    {
        var a = T("A");
        var vm = MakeViewModel(new List<Track> { a }, out var audio);
        Track? reported = null;
        vm.PlaybackFailed += (_, e) => reported = e.Track;

        audio.RaiseTrackFailed(a);

        Assert.Same(a, reported);
    }
}
