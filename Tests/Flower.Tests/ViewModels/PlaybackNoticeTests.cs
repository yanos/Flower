using System.Linq;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Flower.Models;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests.ViewModels;

// A song that would not play used to be skipped without a word, and a run of
// them stopped playback in a state that looked like playing with nothing
// coming out (MainViewModel.PlaybackNotice). Where each head shows the
// sentence is the view's business and not tested.
[Collection("PlatformDataDirectory")]
public class PlaybackNoticeTests : PinnedDataDirectory
{
    private static MainViewModelHarness.Parts Build()
    {
        var tracks = Enumerable.Range(0, 10).Select(i => new Track
        {
            Title = $"Song {i}", Path = $"/music/{i}.mp3", Album = "An Album", Artists = "An Artist",
        }).ToList();
        return MainViewModelHarness.BuildParts(new Library(tracks), new MainPlaylist(tracks));
    }

    private static void Fail(MainViewModelHarness.Parts parts)
    {
        parts.Audio.RaiseTrackFailed(parts.PlaylistControl.CurrentlyPlayingTrack!);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void A_song_that_will_not_play_is_named()
    {
        using var parts = Build();
        parts.PlaylistControl.Play(parts.PlaylistControl.CurrentPlaylist.Tracks[0]);

        Fail(parts);

        Assert.Contains("Song 0", parts.Main.PlaybackNotice);
    }

    [AvaloniaFact]
    public void Giving_up_is_said_and_stays_until_something_plays()
    {
        using var parts = Build();
        parts.PlaylistControl.Play(parts.PlaylistControl.CurrentPlaylist.Tracks[0]);

        for (var i = 0; i < 5; i++)
            Fail(parts);

        Assert.StartsWith("Stopped", parts.Main.PlaybackNotice);

        parts.Audio.IsPlaying = true;
        parts.Audio.RaisePlaying();
        Assert.Null(parts.Main.PlaybackNotice);
    }
}
