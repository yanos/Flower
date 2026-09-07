using System.Collections.Generic;
using System.Linq;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels.Mobile;

using Xunit;

namespace Flower.Tests;

// Which way a newly-navigated-to mobile screen should arrive - the ViewModel
// half of the forward slide-in (ScreenStackPanel plays it; see
// ScreenStackPanelSwipeTests for the animation itself). Back and Forward are
// deliberately silent here: their transition is the OUTGOING screen sliding
// off a destination that was already sitting underneath it, which the panel
// drives on its own.
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

    // The tab bar is a left-to-right strip, so a tap on it has the same
    // direction the swipe that reaches the same tab would.
    [AvaloniaFact]
    public void A_tab_to_the_right_arrives_from_the_right()
    {
        using var scope = Build();

        // RecentlyAdded is the leftmost/default tab - see MobileMainViewModel.
        scope.Mobile.SelectTabCommand.Execute(nameof(MobileTab.Albums));

        Assert.Equal(MobileNavigationTransition.FromRight, scope.Mobile.ConsumePendingTransition());
    }

    [AvaloniaFact]
    public void A_tab_to_the_left_arrives_from_the_left()
    {
        using var scope = Build();
        scope.Mobile.SelectTabCommand.Execute(nameof(MobileTab.Search));
        scope.Mobile.ConsumePendingTransition();

        scope.Mobile.SelectTabCommand.Execute(nameof(MobileTab.Albums));

        Assert.Equal(MobileNavigationTransition.FromLeft, scope.Mobile.ConsumePendingTransition());
    }

    // A drill-in has no left/right ordering to respect - it is a push, and a
    // pushed screen comes in from the right whichever tab it was reached from.
    [AvaloniaFact]
    public void Drilling_into_an_album_arrives_from_the_right()
    {
        using var scope = Build();
        scope.Mobile.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        scope.Mobile.ConsumePendingTransition();

        scope.Mobile.SelectAlbumOrArtistCommand.Execute("Album 0");
        MainViewModelHarness.WaitForTheDrillIn(scope.Mobile, "Album 0");

        Assert.Equal(MobileNavigationTransition.FromRight, scope.Mobile.ConsumePendingTransition());
    }

    [AvaloniaFact]
    public void Going_back_asks_for_no_entrance()
    {
        using var scope = Build();
        scope.Mobile.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        scope.Mobile.SelectAlbumOrArtistCommand.Execute("Album 0");
        MainViewModelHarness.WaitForTheDrillIn(scope.Mobile, "Album 0");
        scope.Mobile.ConsumePendingTransition();

        scope.Mobile.BackCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(MobileNavigationTransition.None, scope.Mobile.ConsumePendingTransition());
    }

    // A compound jump - a search result, or the Now Playing sheet's album art,
    // straight into an album's own track list - switches tab and drills in, and
    // is one navigation. Raising NavigationChanged for the tab switch too meant
    // the first sync consumed the pending entrance on behalf of the Albums
    // picker, a screen that was replaced in the same breath, and the album the
    // user actually asked for cut in with nothing pending.
    [AvaloniaFact]
    public void A_jump_from_search_into_an_album_is_one_navigation_that_arrives_from_the_right()
    {
        using var scope = Build();
        scope.Mobile.SelectTabCommand.Execute(nameof(MobileTab.Search));
        scope.Mobile.ConsumePendingTransition();

        var navigations = 0;
        scope.Mobile.NavigationChanged += (_, _) => navigations++;

        scope.Mobile.SelectSearchAlbumCommand.Execute("Album 0");
        MainViewModelHarness.WaitForTheDrillIn(scope.Mobile, "Album 0");

        Assert.Equal(1, navigations);
        Assert.Equal(MobileNavigationTransition.FromRight, scope.Mobile.ConsumePendingTransition());
    }

    // The same jump, made from the Now Playing sheet's album art. It is a push
    // like any other, so the album arrives from the right - and the sheet has
    // to be shoved off to the LEFT to let it (SlidingSheet.ExitsForward), or
    // the album is uncovered from the left edge, which is what going back looks
    // like. The drill-in has to land first, too: the sheet is opaque and
    // full-screen, so an entrance that started before it began leaving would
    // spend its whole length hidden behind it.
    [AvaloniaFact]
    public void The_album_art_pushes_the_now_playing_sheet_off_to_the_left()
    {
        using var scope = Build();
        scope.Parts.PlaylistControl.Play(scope.Parts.Library.Tracks[0]);
        scope.Mobile.OpenNowPlayingCommand.Execute(null);
        Assert.True(scope.Mobile.IsShowingNowPlaying);
        Assert.False(scope.Mobile.NowPlayingExitsForward);

        bool? sheetStillUpWhenTheAlbumLanded = null;
        scope.Mobile.NavigationChanged += (_, _) => sheetStillUpWhenTheAlbumLanded ??= scope.Mobile.IsShowingNowPlaying;

        scope.Mobile.GoToCurrentlyPlayingAlbumCommand.Execute(null);
        MainViewModelHarness.WaitForTheDrillIn(scope.Mobile, "Album 0");

        Assert.True(sheetStillUpWhenTheAlbumLanded);
        Assert.False(scope.Mobile.IsShowingNowPlaying);
        Assert.True(scope.Mobile.NowPlayingExitsForward);
        Assert.Equal(MobileNavigationTransition.FromRight, scope.Mobile.ConsumePendingTransition());
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
        scope.Mobile.SelectAlbumOrArtistCommand.Execute("Album 0");
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
        // An ordinary dismissal, so the sheet retreats the way it came.
        Assert.False(scope.Mobile.NowPlayingExitsForward);
    }

    // One forward exit must not colour the next ordinary dismissal - reopening
    // the sheet puts it back to leaving by the edge it arrived from.
    [AvaloniaFact]
    public void Reopening_now_playing_restores_the_ordinary_dismissal()
    {
        using var scope = Build();
        scope.Parts.PlaylistControl.Play(scope.Parts.Library.Tracks[0]);
        scope.Mobile.OpenNowPlayingCommand.Execute(null);
        scope.Mobile.GoToCurrentlyPlayingAlbumCommand.Execute(null);
        MainViewModelHarness.WaitForTheDrillIn(scope.Mobile, "Album 0");
        Assert.True(scope.Mobile.NowPlayingExitsForward);

        scope.Mobile.OpenNowPlayingCommand.Execute(null);

        Assert.True(scope.Mobile.IsShowingNowPlaying);
        Assert.False(scope.Mobile.NowPlayingExitsForward);
    }

    // The user's own report: play something from one album, walk to a second
    // one, raise Now Playing over it and tap the album art - and then go back.
    // Back has to undo the whole compound step, sheet included: the album art
    // tap pushed an album AND dismissed the sheet as a single navigation, so
    // popping it without the sheet leaves the user on a screen they were never
    // standing on, with the song they were looking at nowhere in sight. See
    // MobileNavigationFrame.Sheet.
    [AvaloniaFact]
    public void Back_from_the_album_art_returns_to_now_playing()
    {
        using var scope = Build();
        scope.Mobile.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        scope.Mobile.SelectAlbumOrArtistCommand.Execute("Album 0");
        MainViewModelHarness.WaitForTheDrillIn(scope.Mobile, "Album 0");
        scope.Parts.PlaylistControl.Play(scope.Parts.Library.Tracks[0]);

        // Back out to the grid and into a different album, so the screen the
        // sheet is raised over is not the one its album art leads to.
        scope.Mobile.BackCommand.Execute(null);
        WaitUntil(() => !scope.Mobile.IsShowingAlbumTrackList);
        scope.Mobile.SelectAlbumOrArtistCommand.Execute("Album 1");
        MainViewModelHarness.WaitForTheDrillIn(scope.Mobile, "Album 1");

        scope.Mobile.OpenNowPlayingCommand.Execute(null);
        scope.Mobile.GoToCurrentlyPlayingAlbumCommand.Execute(null);
        MainViewModelHarness.WaitForTheDrillIn(scope.Mobile, "Album 0");
        Assert.False(scope.Mobile.IsShowingNowPlaying);

        scope.Mobile.BackCommand.Execute(null);
        WaitUntil(() => scope.Mobile.IsShowingNowPlaying);

        Assert.Equal("Album 1", scope.Mobile.Main.SelectedSubItem);
        // And it returns by the edge it was pushed out of, rather than playing
        // a back step as a fresh push from the right.
        Assert.True(scope.Mobile.NowPlayingEntersBackward);
    }

    // Redoing that same step is the push again, so the sheet leaves the way it
    // left the first time - forward, off to the left, ahead of the album.
    [AvaloniaFact]
    public void Forward_past_the_album_art_dismisses_the_sheet_forward_again()
    {
        using var scope = Build();
        scope.Mobile.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        scope.Mobile.SelectAlbumOrArtistCommand.Execute("Album 1");
        MainViewModelHarness.WaitForTheDrillIn(scope.Mobile, "Album 1");
        scope.Parts.PlaylistControl.Play(scope.Parts.Library.Tracks[0]);
        scope.Mobile.OpenNowPlayingCommand.Execute(null);
        scope.Mobile.GoToCurrentlyPlayingAlbumCommand.Execute(null);
        MainViewModelHarness.WaitForTheDrillIn(scope.Mobile, "Album 0");
        scope.Mobile.BackCommand.Execute(null);
        WaitUntil(() => scope.Mobile.IsShowingNowPlaying);

        scope.Mobile.SwipeForward();
        WaitUntil(() => !scope.Mobile.IsShowingNowPlaying);

        Assert.Equal("Album 0", scope.Mobile.Main.SelectedSubItem);
        Assert.True(scope.Mobile.NowPlayingExitsForward);
    }

    // An ordinary drill-in carries no sheet, so going back to one must not
    // raise anything - the frame's Sheet is None and restoring it is a no-op.
    [AvaloniaFact]
    public void Back_to_a_screen_that_had_no_sheet_raises_none()
    {
        using var scope = Build();
        scope.Mobile.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        scope.Mobile.SelectAlbumOrArtistCommand.Execute("Album 0");
        MainViewModelHarness.WaitForTheDrillIn(scope.Mobile, "Album 0");

        scope.Mobile.BackCommand.Execute(null);
        WaitUntil(() => !scope.Mobile.IsShowingAlbumTrackList);

        Assert.False(scope.Mobile.IsShowingNowPlaying);
        Assert.False(scope.Mobile.NowPlayingEntersBackward);
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

    // NavigationChanged is raised for things that are not navigations at all
    // (a search refresh, a rescan landing). Consuming the transition rather
    // than merely reading it is what stops the next one of those from replaying
    // the entrance the last real navigation asked for.
    [AvaloniaFact]
    public void A_transition_is_only_handed_out_once()
    {
        using var scope = Build();
        scope.Mobile.SelectTabCommand.Execute(nameof(MobileTab.Albums));

        Assert.Equal(MobileNavigationTransition.FromRight, scope.Mobile.ConsumePendingTransition());
        Assert.Equal(MobileNavigationTransition.None, scope.Mobile.ConsumePendingTransition());
    }
}
