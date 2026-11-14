using System.Threading;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Flower.Models;
using Flower.Persistence;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Microsoft.Extensions.Logging.Abstractions;

namespace Flower.Tests.Playback;

// Real dispatcher delivery from backend events. Selection, queue mutations,
// resume positions and failure rules are covered in the shared test project.
[Collection("PlatformDataDirectory")]
public class PlaybackDispatcherTests : PinnedDataDirectory
{
    private static Track T(string title) => new() { Title = title, Path = $"/music/{title}.mp3" };

    private PlaylistControlViewModel MakeViewModel(List<Track> tracks, out FakeAudioManager audio)
    {
        audio = new FakeAudioManager();
        var vm = new PlaylistControlViewModel(audio, new MainPlaylist(tracks), new Library(tracks),
            new AppSettings(), new AppSettingsStore(NullLogger<AppSettingsStore>.Instance),
            NullLogger<PlaylistControlViewModel>.Instance);
        vm.OffPlaybackThread = work => work();
        return Own(vm);
    }

    // The end of a track is raised on its decode thread, and what plays next
    // is decided on the UI thread, which owns the queue and the shuffle order
    // (the decode thread dealing it while Next() did was a race). Decided
    // there, it is decided against what is playing by then: a song the user
    // picked in the meantime stands. Decided on the decode thread, the
    // advance to B was already posted, and it replaced the user's choice.
    [AvaloniaFact]
    public void A_song_picked_while_the_end_of_the_last_one_is_on_its_way_keeps_playing()
    {
        var a = T("A");
        var b = T("B");
        var c = T("C");
        var vm = MakeViewModel(new List<Track> { a, b, c }, out var audio);
        vm.Play(a);

        var decodeThread = new Thread(audio.RaiseEndReached);
        decodeThread.Start();
        decodeThread.Join();
        vm.Play(c);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(c, vm.CurrentlyPlayingTrack);
    }

    [AvaloniaFact]
    public void The_output_stopping_under_a_song_keeps_the_song_and_the_queue()
    {
        var library = new List<Track> { T("A"), T("B"), T("C") };
        var vm = MakeViewModel(library, out var audio);
        vm.SetCurrentPlaylist(new Playlist("Now Playing Queue", library));
        vm.Play(library[1], 1);

        audio.RaiseStopped();
        Dispatcher.UIThread.RunJobs();

        Assert.Same(library[1], vm.CurrentlyPlayingTrack);
        Assert.Equal(1, vm.QueueIndex);

        vm.PlayOrPause();

        Assert.Same(library[1], audio.LastPlayed);
        Assert.Equal(0, audio.ResumeCount);
    }

    // ── Volume adjustment ──────────────────────────────────────────────────
    //
    // The adjustment is an offset (IAudioManager.VolumeOffset), never a write
    // to Volume: the slider shows the user's own setting and must not wander
    // because a track happens to be quiet. So every assertion here is about
    // EffectiveVolume - what the sink is actually driven with - plus Volume
    // staying exactly where the user left it.

    // [AvaloniaFact] rather than [Fact]: the Stopped handler is posted to the
    // dispatcher (see PlaylistControlViewModel), so this needs a real one to
    // pump.
    [AvaloniaFact]
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
        // Stopped is marshalled onto the dispatcher now - it can arrive from
        // the sink's backend thread when the output device dies - so the
        // handler has to be pumped before asserting on what it did.
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(60, audio.EffectiveVolume);
        Assert.Equal(60, audio.Volume);
    }
}
