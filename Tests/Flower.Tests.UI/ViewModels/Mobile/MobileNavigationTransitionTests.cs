using System.Collections.Generic;
using System.Linq;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels.Mobile;

using Xunit;

namespace Flower.Tests.ViewModels.Mobile;

// Navigation away from Now Playing dismisses it and Back restores the screen.
[Collection("PlatformDataDirectory")]
public class MobileNavigationTransitionTests : PinnedDataDirectory
{
    private static MainViewModelHarness.MobileParts Build()
    {
        var tracks = Enumerable.Range(0, 8).Select(i => new Track
        {
            Title = $"Track {i}", Path = $"/music/{i}.mp3", Album = $"Album {i / 4}", Artists = "An Artist",
        }).ToList();
        var parts = MainViewModelHarness.BuildMobile(new Library(tracks), new MainPlaylist(tracks));
        Dispatcher.UIThread.RunJobs();
        return parts;
    }

    // Tapping the album art of the album already being shown is not a
    // navigation at all: nothing to push, nothing to slide in. Pushing anyway
    // put a history entry for the destination itself into the stack - a back
    // step that goes nowhere, and one ScreenStackPanel would be handed as both
    // "current" and "one back", one control for two slots (see
    // ScreenStackPanelSwipeTests' own account of the empty screen that made).
    [AvaloniaFact]
    public void The_album_art_of_the_album_already_showing_just_dismisses_the_sheet()
    {
        using var scope = Build();
        scope.Parts.PlaylistControl.Play(scope.Parts.Library.Tracks[0]);
        scope.Mobile.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        scope.Mobile.SelectAlbumOrArtistCommand.Execute(MainViewModelHarness.AlbumKey(scope.Mobile, "Album 0"));
        MainViewModelHarness.WaitForTheDrillIn(scope.Mobile, "Album 0");
        scope.Mobile.OpenNowPlayingCommand.Execute(null);
        scope.Mobile.ConsumePendingTransition();

        var navigations = 0;
        scope.Mobile.NavigationChanged += (_, _) => navigations++;

        scope.Mobile.GoToCurrentlyPlayingAlbumCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(scope.Mobile.IsShowingNowPlaying);
        Assert.Equal(0, navigations);
        Assert.Equal(MobileNavigationTransition.None, scope.Mobile.ConsumePendingTransition());
    }

    // The tab oval stays up over Now Playing, so a tab tapped there leaves the
    // sheet. Another tab is a tab switch as well, the way the album art's
    // drill-in is: the tab arrives from the right as the sheet slides down.
    // Back returns to the tab the sheet was raised over, without the sheet -
    // it is an overlay, not a step in the history.
    [AvaloniaFact]
    public void A_tab_tapped_over_now_playing_switches_to_it_and_takes_the_sheet_away()
    {
        using var scope = Build();
        scope.Parts.PlaylistControl.Play(scope.Parts.Library.Tracks[0]);
        scope.Mobile.OpenNowPlayingCommand.Execute(null);
        scope.Mobile.ConsumePendingTransition();

        scope.Mobile.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(MobileTab.Albums, scope.Mobile.SelectedTab);
        Assert.False(scope.Mobile.IsShowingNowPlaying);

        scope.Mobile.BackCommand.Execute(null);
        WaitUntil(() => scope.Mobile.SelectedTab == MobileTab.Home);
        Dispatcher.UIThread.RunJobs();
        Assert.False(scope.Mobile.IsShowingNowPlaying);
    }

    // Now Playing is an overlay, not a step in the history: its album art
    // pushes an album and takes the sheet away as one step, and Back from that
    // album lands on the screen the sheet was raised over, without raising the
    // sheet again. See MobileNavigationFrame.
    [AvaloniaFact]
    public void Back_from_the_album_art_does_not_bring_now_playing_back()
    {
        using var scope = Build();
        scope.Mobile.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        scope.Mobile.SelectAlbumOrArtistCommand.Execute(MainViewModelHarness.AlbumKey(scope.Mobile, "Album 0"));
        MainViewModelHarness.WaitForTheDrillIn(scope.Mobile, "Album 0");
        scope.Parts.PlaylistControl.Play(scope.Parts.Library.Tracks[0]);

        // Back out to the grid and into a different album, so the screen the
        // sheet is raised over is not the one its album art leads to.
        scope.Mobile.BackCommand.Execute(null);
        WaitUntil(() => !scope.Mobile.IsShowingAlbumTrackList);
        scope.Mobile.SelectAlbumOrArtistCommand.Execute(MainViewModelHarness.AlbumKey(scope.Mobile, "Album 1"));
        MainViewModelHarness.WaitForTheDrillIn(scope.Mobile, "Album 1");

        scope.Mobile.OpenNowPlayingCommand.Execute(null);
        scope.Mobile.GoToCurrentlyPlayingAlbumCommand.Execute(null);
        MainViewModelHarness.WaitForTheDrillIn(scope.Mobile, "Album 0");
        Assert.False(scope.Mobile.IsShowingNowPlaying);

        scope.Mobile.BackCommand.Execute(null);
        WaitUntil(() => scope.Mobile.CurrentAlbumHeader?.Name == "Album 1");
        Dispatcher.UIThread.RunJobs();

        Assert.False(scope.Mobile.IsShowingNowPlaying);
    }

    // Back/Forward are async (ApplyFrame awaits the destination's own row
    // rebuild), so a command execute is only the start of one.
    private static void WaitUntil(System.Func<bool> done, int timeoutMs = 2000)
    {
        var deadline = System.Environment.TickCount64 + timeoutMs;
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            if (done())
                return;
            if (System.Environment.TickCount64 >= deadline)
                Assert.Fail($"the navigation never landed within {timeoutMs}ms");
            System.Threading.Thread.Sleep(10);
        }
    }
}
