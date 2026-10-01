using System.Collections.Generic;
using System.Linq;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels.Mobile;

using Xunit;

namespace Flower.Tests;

// Which tabs a phone's bar shows, and in what order, is chosen on Settings'
// Navigation Bar page - and that order is also what a tap and a swipe read
// left and right from. The Queue tab is here too, being the one tab the
// choice brought with it.
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
    public void Out_of_the_box_the_bar_has_every_tab_and_opens_on_home()
    {
        using var scope = Build();

        Assert.Equal(
            new[] { MobileTab.Home, MobileTab.Albums, MobileTab.Artists, MobileTab.Songs, MobileTab.Playlists, MobileTab.Queue, MobileTab.Search },
            scope.Mobile.VisibleTabs);
        Assert.Equal(MobileTab.Home, scope.Mobile.SelectedTab);
        Assert.True(scope.Mobile.IsShowingHome);
    }

    [AvaloniaFact]
    public void The_app_opens_on_whichever_tab_is_first()
    {
        using var scope = Build([MobileTab.Queue, MobileTab.Songs]);

        Assert.Equal(MobileTab.Queue, scope.Mobile.SelectedTab);
        Assert.True(scope.Mobile.IsShowingQueue);
    }

    [Fact]
    public void A_saved_bar_is_cleaned_rather_than_trusted()
    {
        Assert.Equal(MobileTabs.Default, MobileTabs.Parse(null));
        Assert.Equal(MobileTabs.Default, MobileTabs.Parse(["NotATab"]));
        Assert.Equal(new[] { MobileTab.Songs, MobileTab.Queue },
            MobileTabs.Parse(["Songs", "Bogus", "Songs", "Queue"]));
        Assert.Equal(MobileTabs.All, MobileTabs.Parse(MobileTabs.All.Select(t => t.ToString())));
    }

    // The Navigation Bar page lists the bar in its order, then what is not in
    // it, in the order MobileTabs.All gives them.
    [AvaloniaFact]
    public void The_page_lists_the_bar_in_order_then_the_rest()
    {
        using var scope = Build([MobileTab.Search, MobileTab.Albums, MobileTab.Songs]);

        Assert.Equal(new[] { MobileTab.Search, MobileTab.Albums, MobileTab.Songs },
            scope.Mobile.VisibleTabRows.Select(r => r.Tab));
        Assert.All(scope.Mobile.VisibleTabRows, r => Assert.True(r.IsShown));
        Assert.Equal(new[] { MobileTab.Home, MobileTab.Artists, MobileTab.Playlists, MobileTab.Queue },
            scope.Mobile.HiddenTabRows.Select(r => r.Tab));
        Assert.All(scope.Mobile.HiddenTabRows, r => Assert.False(r.IsShown));
        Assert.True(scope.Mobile.HasHiddenTabs);
    }

    [AvaloniaFact]
    public void Hiding_and_showing_a_tab_changes_the_bar_and_is_remembered()
    {
        using var scope = Build();
        var mobile = scope.Mobile;

        mobile.ToggleTabShownCommand.Execute(Row(mobile, MobileTab.Playlists));
        mobile.ToggleTabShownCommand.Execute(Row(mobile, MobileTab.Home));
        mobile.ToggleTabShownCommand.Execute(Row(mobile, MobileTab.Home));

        var expected = new[] { MobileTab.Albums, MobileTab.Artists, MobileTab.Songs, MobileTab.Queue, MobileTab.Search, MobileTab.Home };
        Assert.Equal(expected, mobile.VisibleTabs);
        Assert.Equal(expected.Select(t => t.ToString()), scope.Parts.AppSettings.MobileTabs);
    }

    [AvaloniaFact]
    public void Turning_the_names_off_is_remembered()
    {
        using var scope = Build();
        Assert.True(scope.Mobile.ShowTabLabels);

        scope.Mobile.ShowTabLabels = false;

        Assert.False(scope.Parts.AppSettings.MobileTabLabels);
    }

    // A drop names where the tab lands in the bar as it will be, the tab
    // counted in its new place - so down and up are the same arithmetic.
    [AvaloniaFact]
    public void Dropping_a_tab_reorders_the_bar_and_is_remembered()
    {
        using var scope = Build();
        var mobile = scope.Mobile;

        mobile.MoveTab(MobileTab.Playlists, 1);
        mobile.MoveTab(MobileTab.Home, 2);

        var expected = new[] { MobileTab.Playlists, MobileTab.Albums, MobileTab.Home, MobileTab.Artists, MobileTab.Songs, MobileTab.Queue, MobileTab.Search };
        Assert.Equal(expected, mobile.VisibleTabs);
        Assert.Equal(expected, mobile.VisibleTabRows.Select(r => r.Tab));
        Assert.Equal(expected.Select(t => t.ToString()), scope.Parts.AppSettings.MobileTabs);
    }

    // Only the bar is ordered: a tab outside it goes back in by its switch.
    [AvaloniaFact]
    public void Dropping_a_tab_not_in_the_bar_changes_nothing()
    {
        using var scope = Build([MobileTab.Albums, MobileTab.Songs]);

        scope.Mobile.MoveTab(MobileTab.Queue, 0);

        Assert.Equal(new[] { MobileTab.Albums, MobileTab.Songs }, scope.Mobile.VisibleTabs);
    }

    // The page is pushed over Settings, so it goes wherever Settings goes.
    [AvaloniaFact]
    public void The_navigation_bar_page_opens_over_settings_and_closes_with_it()
    {
        using var scope = Build();
        var mobile = scope.Mobile;

        mobile.OpenNavigationBarSettingsCommand.Execute(null);
        Assert.False(mobile.IsShowingNavigationBarSettings);

        mobile.OpenSettingsCommand.Execute(null);
        mobile.OpenNavigationBarSettingsCommand.Execute(null);
        Assert.True(mobile.IsShowingNavigationBarSettings);

        mobile.CloseNavigationBarSettingsCommand.Execute(null);
        Assert.False(mobile.IsShowingNavigationBarSettings);
        Assert.True(mobile.IsShowingSettings);

        mobile.OpenNavigationBarSettingsCommand.Execute(null);
        mobile.CloseSheetCommand.Execute(null);
        Assert.False(mobile.IsShowingNavigationBarSettings);
    }

    // All seven fit, drawn smaller (MobileTabBarLayoutTests); a bar of none
    // is no way to get anywhere.
    [AvaloniaFact]
    public void The_bar_holds_every_tab_at_most_and_one_at_least()
    {
        using var scope = Build();
        var mobile = scope.Mobile;

        Assert.Equal(7, mobile.VisibleTabs.Count);
        mobile.ToggleTabShownCommand.Execute(Row(mobile, MobileTab.Home));
        Assert.DoesNotContain(MobileTab.Home, mobile.VisibleTabs);

        foreach (var tab in mobile.VisibleTabs.Skip(1).ToList())
            mobile.ToggleTabShownCommand.Execute(Row(mobile, tab));
        Assert.Equal(new[] { MobileTab.Albums }, mobile.VisibleTabs);
        Assert.False(Row(mobile, MobileTab.Albums).CanToggle);

        mobile.ToggleTabShownCommand.Execute(Row(mobile, MobileTab.Albums));
        Assert.Equal(new[] { MobileTab.Albums }, mobile.VisibleTabs);
    }

    // Left and right are the bar's, not the order the tabs were declared in.
    [AvaloniaFact]
    public void A_tap_arrives_from_the_side_its_tab_is_on_in_the_bar()
    {
        using var scope = Build([MobileTab.Search, MobileTab.Queue, MobileTab.Albums]);

        scope.Mobile.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        Assert.Equal(MobileNavigationTransition.FromRight, scope.Mobile.ConsumePendingTransition());

        scope.Mobile.SelectTabCommand.Execute(nameof(MobileTab.Queue));
        Assert.Equal(MobileNavigationTransition.FromLeft, scope.Mobile.ConsumePendingTransition());
    }

    [AvaloniaFact]
    public void A_swipe_pages_through_the_bar_in_its_order_and_stops_at_its_ends()
    {
        using var scope = Build([MobileTab.Search, MobileTab.Queue, MobileTab.Albums]);
        var mobile = scope.Mobile;

        mobile.SwipeBack();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(MobileTab.Search, mobile.SelectedTab);

        mobile.SwipeForward();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(MobileTab.Queue, mobile.SelectedTab);
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

    [AvaloniaFact]
    public void The_queue_is_the_song_playing_then_what_follows_it()
    {
        using var scope = Build();
        PlayFrom(scope, 2);

        scope.Mobile.SelectTabCommand.Execute(nameof(MobileTab.Queue));

        var rows = scope.Mobile.QueueRows;
        Assert.Equal(new[] { "Track 2", "Track 3", "Track 4" }, rows.Select(r => r.Track.Title));
        Assert.True(rows[0].IsCurrentlyPlaying);
        Assert.False(rows[1].IsCurrentlyPlaying);
        Assert.Null(scope.Mobile.QueueCaption);
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

        Assert.Equal(new[] { "Track 3", "Track 4" }, scope.Mobile.QueueRows.Select(r => r.Track.Title));
    }

    // Under shuffle any of them could be next, so all of them are shown, and
    // the caption says why they are not in the order they will play.
    [AvaloniaFact]
    public void Under_shuffle_the_queue_is_everything_else_and_says_so()
    {
        using var scope = Build();
        PlayFrom(scope, 2);
        scope.Parts.PlaylistControl.ToggleShuffle();

        scope.Mobile.SelectTabCommand.Execute(nameof(MobileTab.Queue));

        Assert.Equal(new[] { "Track 2", "Track 0", "Track 1", "Track 3", "Track 4" },
            scope.Mobile.QueueRows.Select(r => r.Track.Title));
        Assert.NotNull(scope.Mobile.QueueCaption);
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
        Assert.Equal("Nothing Queued", scope.Mobile.EmptyStateTitle);
    }
}
