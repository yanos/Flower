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
        // A repeat/auto-advance Play() posts its own fire-and-forget
        // LibraryStore.SaveAsync as a Dispatcher job, which can still be
        // queued when the test body returns (nothing awaits it) - drain the
        // dispatcher first so that write lands against a directory that
        // still exists, instead of racing the delete below and surfacing as
        // a spurious test-cleanup failure.
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(200);
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }

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
}
