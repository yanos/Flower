using System.Collections.Generic;
using System.Linq;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels.Mobile;

using Xunit;

namespace Flower.Tests.ViewModels.Mobile;

// Tab selection, settings persistence, and the Queue tab's playback commands.
// Pure tab editing is covered in the shared test project.
[Collection("PlatformDataDirectory")]
public class MobileTabsTests : PinnedDataDirectory
{
    private static List<Track> Album() => Enumerable.Range(0, 5).Select(i => new Track
    {
        Title = $"Track {i}", Path = $"/music/{i}.mp3", Album = "Album", Artists = "An Artist", TrackNumber = (uint)i + 1,
    }).ToList();

    private static MainViewModelHarness.MobileParts Build(IReadOnlyList<MobileTab>? tabs = null, List<Track>? tracks = null)
    {
        tracks ??= Album();
        var parts = MainViewModelHarness.BuildParts(new Library(tracks), new MainPlaylist(tracks));
        // Null here is "never chosen", which is the app's own default - unlike
        // BuildMobile's, which is the suite's.
        parts.AppSettings.MobileTabs = tabs?.Select(t => t.ToString()).ToList();
        var mobile = new MobileMainViewModel(parts.Main, parts.PlaylistControl, parts.CurrentlyPlaying,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MobileMainViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        return new MainViewModelHarness.MobileParts(mobile, parts);
    }

    private static MobileTabSettingRow Row(MobileMainViewModel mobile, MobileTab tab) =>
        mobile.VisibleTabRows.Concat(mobile.HiddenTabRows).Single(r => r.Tab == tab);

    [AvaloniaFact]
    public void The_app_opens_on_whichever_tab_is_first()
    {
        using var scope = Build([MobileTab.Queue, MobileTab.Songs]);

        Assert.Equal(MobileTab.Queue, scope.Mobile.SelectedTab);
        Assert.True(scope.Mobile.IsShowingQueue);
    }

    [AvaloniaFact]
    public void Hiding_and_showing_a_tab_changes_the_bar_and_is_remembered()
    {
        using var scope = Build();
        var mobile = scope.Mobile;

        var history = Assert.Single(mobile.HiddenTabRows);
        Assert.False(history.CanToggle);
        mobile.ToggleTabShownCommand.Execute(history);
        Assert.DoesNotContain(MobileTab.History, mobile.VisibleTabs);

        mobile.ToggleTabShownCommand.Execute(Row(mobile, MobileTab.Playlists));
        Assert.True(Row(mobile, MobileTab.History).CanToggle);
        mobile.ToggleTabShownCommand.Execute(Row(mobile, MobileTab.Home));
        mobile.ToggleTabShownCommand.Execute(Row(mobile, MobileTab.Home));

        var expected = new[] { MobileTab.Songs, MobileTab.Albums, MobileTab.Artists, MobileTab.Queue, MobileTab.Search, MobileTab.Home };
        Assert.Equal(expected, mobile.VisibleTabs);
        Assert.Equal(expected.Select(t => t.ToString()), scope.Parts.AppSettings.MobileTabs);
    }

    // Taking the tab on screen out of the bar, from the page over it, lands
    // on the first tab left - with nothing to go back to, since back would be
    // the tab just removed.
    [AvaloniaFact]
    public void Hiding_the_tab_on_screen_moves_to_the_first_tab()
    {
        using var scope = Build();
        var mobile = scope.Mobile;
        mobile.SelectTabCommand.Execute(nameof(MobileTab.Songs));

        mobile.ToggleTabShownCommand.Execute(Row(mobile, MobileTab.Songs));

        Assert.Equal(MobileTab.Home, mobile.SelectedTab);
        Assert.False(mobile.CanGoBack);
    }

    // ── Queue ──────────────────────────────────────────────────────────

    private static void PlayFrom(MainViewModelHarness.MobileParts scope, int index)
    {
        var tracks = scope.Parts.Library.Tracks.OrderBy(t => t.TrackNumber).ToList();
        scope.Parts.Main.SetPlayQueue(tracks);
        scope.Parts.PlaylistControl.Play(tracks[index], index);
        Dispatcher.UIThread.RunJobs();
    }

    // It follows playback while it is on screen, not only on the way in.
    [AvaloniaFact]
    public void The_queue_moves_on_when_the_song_does()
    {
        using var scope = Build();
        PlayFrom(scope, 2);
        scope.Mobile.SelectTabCommand.Execute(nameof(MobileTab.Queue));

        scope.Parts.PlaylistControl.Next();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(3, scope.Mobile.QueuePlayedCount);
        Assert.True(scope.Mobile.QueueRows[3].IsCurrentlyPlaying);
        Assert.True(scope.Mobile.QueueRows[2].IsPlayed);
    }

    // A tap on the queue is a jump along it - it must not replace the queue
    // with the part of it that is on screen.
    [AvaloniaFact]
    public void Tapping_a_song_in_the_queue_plays_it_and_keeps_the_queue()
    {
        using var scope = Build();
        PlayFrom(scope, 1);
        var queue = scope.Parts.PlaylistControl.CurrentPlaylist;
        scope.Mobile.SelectTabCommand.Execute(nameof(MobileTab.Queue));

        scope.Mobile.PlayTrackCommand.Execute(scope.Mobile.QueueRows.Single(r => r.Track.Title == "Track 3"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Track 3", scope.Parts.PlaylistControl.CurrentlyPlayingTrack?.Title);
        Assert.Same(queue, scope.Parts.PlaylistControl.CurrentPlaylist);
        Assert.Equal(3, scope.Parts.PlaylistControl.QueueIndex);
    }

    // Before anything has played, the queue underneath is only the library
    // in the order it was scanned - nothing anyone lined up.
    [AvaloniaFact]
    public void With_nothing_played_yet_the_queue_is_empty_and_says_so()
    {
        using var scope = Build([MobileTab.Queue]);

        Assert.True(scope.Mobile.IsContentEmpty);
    }
}
