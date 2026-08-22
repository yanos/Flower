using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels;
using Flower.ViewModels.Mobile;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Flower.Tests;

// MobileMainViewModel used to reimplement MainViewModel's placeholder-
// resolving play path and SyncPlayQueueToCurrentView line for line, purely
// because both were private, and hand-rolled the same
// push-history/set-scope/rebuild sequence five times - the work
// docs/ARCHITECTURE-REVIEW.md Tier 4.2 parked. These pin the behaviour that
// sharing them has to preserve.
[Collection("PlatformDataDirectory")]
public class MobileSharedPlaybackTests : PinnedDataDirectory
{
    private static Track T(string title, string album = "Album", string? path = "/music/x.mp3") =>
        new() { Title = title, Album = album, Artists = "Artist", Path = path == null ? null : $"/music/{title}.mp3" };

    // Returns the disposable pair rather than a bare ViewModel: the
    // MainViewModel underneath owns a periodic log-push DispatcherTimer that
    // has to be stopped when the test ends - see MainViewModelHarness.Parts.
    private static MainViewModelHarness.MobileParts Build(params Track[] tracks)
    {
        var parts = MainViewModelHarness.BuildParts(new Library(tracks.ToList()), new MainPlaylist(new List<Track>()));
        var mobile = new MobileMainViewModel(parts.Main, parts.PlaylistControl, parts.CurrentlyPlaying, NullLogger<MobileMainViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        return new MainViewModelHarness.MobileParts(mobile, parts);
    }

    // Mobile's queue used to stay pinned to the Importer's raw filesystem-scan
    // order whatever list the tapped track came from - confirmed on a real
    // device as Next/Previous walking an apparently random order. Both sides
    // now re-anchor through MainViewModel.SetPlayQueue.
    [AvaloniaFact]
    public async Task Tapping_a_track_re_anchors_the_queue_to_what_is_on_screen()
    {
        var first = T("First");
        var second = T("Second");
        using var scope = Build(first, second);
        var (mobile, parts) = (scope.Mobile, scope.Parts);
        await parts.Main.Browser.RebuildRowsImmediatelyAsync();

        mobile.PlayTrackCommand.Execute(parts.Main.Rows.Single(r => r.Track == second));

        var queue = parts.PlaylistControl.CurrentPlaylist.Tracks;
        Assert.Equal(parts.Main.Rows.Select(r => r.Track.Title), queue.Select(t => t.Title));
        Assert.Equal("Second", parts.PlaylistControl.CurrentlyPlayingTrack?.Title);
    }

    // A placeholder (not yet downloaded) with no peer to stream from must be
    // declined rather than handed to the audio manager - passing a null Path
    // straight to Play is what used to crash inside the old VlcAudioManager.
    // That guard lived in MainViewModel.PlayResolvingPlaceholder and in a copy
    // of it here, then in one shared place; it now lives one layer lower still,
    // inside PlaylistControlViewModel.Play itself, so no caller can miss it -
    // see IStreamUrlResolver.
    [AvaloniaFact]
    public async Task Tapping_a_placeholder_with_nowhere_to_stream_from_plays_nothing()
    {
        var placeholder = new Track { Title = "Remote", Album = "Album", Artists = "Artist", Path = null };
        using var scope = Build(placeholder);
        var (mobile, parts) = (scope.Mobile, scope.Parts);
        await parts.Main.Browser.RebuildRowsImmediatelyAsync();

        mobile.PlayTrackCommand.Execute(parts.Main.Rows.Single(r => r.Track == placeholder));

        Assert.Null(parts.PlaylistControl.CurrentlyPlayingTrack);
        // The queue is still re-anchored: what was declined is the playback,
        // not the navigation.
        Assert.NotEmpty(parts.PlaylistControl.CurrentPlaylist.Tracks);
    }

    // The one reason mobile could not simply call
    // MainViewModel.SyncPlayQueueToCurrentView: its search results are a
    // separate mirror of Rows rather than Rows itself.
    [AvaloniaFact]
    public async Task Tapping_a_search_result_anchors_the_queue_to_the_search_results()
    {
        var inView = T("Aurora");
        var elsewhere = T("Zephyr");
        using var scope = Build(inView, elsewhere);
        var (mobile, parts) = (scope.Mobile, scope.Parts);

        mobile.SelectTabCommand.Execute(nameof(MobileTab.Search));
        mobile.SearchQuery = "Aurora";
        Assert.True(await WaitFor(() => mobile.SearchSongResults.Count == 1),
            "the search results never arrived");

        mobile.PlayTrackCommand.Execute(mobile.SearchSongResults.Single(r => r.Track == inView));

        var queue = parts.PlaylistControl.CurrentPlaylist.Tracks;
        Assert.Equal(new[] { "Aurora" }, queue.Select(t => t.Title));
    }

    // Awaited rather than spun on: the work being waited for finishes on this
    // very thread (the headless UI thread), so blocking it would deadlock
    // instead of waiting.
    private static async Task<bool> WaitFor(System.Func<bool> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (condition())
                return true;
            await Task.Delay(10);
        }
        return condition();
    }
}
