using System.Collections.Generic;
using System.Linq;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels.Mobile;

using Xunit;

namespace Flower.Tests;

// Which tabs a phone's bar shows, and in what order, is chosen in Settings -
// and that order is also what a tap and a swipe read left and right from. The
// Queue tab is here too, being the one tab the choice brought with it.
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
        mobile.TabSettingRows.Single(r => r.Tab == tab);

    [AvaloniaFact]
    public void Out_of_the_box_the_bar_has_no_recent_tab_and_opens_on_albums()
    {
        using var scope = Build();

        Assert.Equal(
            new[] { MobileTab.Albums, MobileTab.Artists, MobileTab.Songs, MobileTab.Playlists, MobileTab.Queue, MobileTab.Search },
            scope.Mobile.VisibleTabs);
        Assert.Equal(MobileTab.Albums, scope.Mobile.SelectedTab);
        Assert.True(scope.Mobile.IsShowingAlbumGrid);
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

    // Settings lists the bar first, in its order, then what is not in it.
    [AvaloniaFact]
    public void Settings_lists_the_bar_in_order_then_the_rest()
    {
        using var scope = Build();

        Assert.Equal(
            new[] { MobileTab.Albums, MobileTab.Artists, MobileTab.Songs, MobileTab.Playlists, MobileTab.Queue, MobileTab.Search, MobileTab.RecentlyAdded },
            scope.Mobile.TabSettingRows.Select(r => r.Tab));
        Assert.False(Row(scope.Mobile, MobileTab.RecentlyAdded).IsShown);
        Assert.False(Row(scope.Mobile, MobileTab.Albums).CanMoveUp);
        Assert.False(Row(scope.Mobile, MobileTab.Search).CanMoveDown);
    }

    [AvaloniaFact]
    public void Hiding_and_showing_a_tab_changes_the_bar_and_is_remembered()
    {
        using var scope = Build();
        var mobile = scope.Mobile;

        mobile.ToggleTabShownCommand.Execute(Row(mobile, MobileTab.Playlists));
        mobile.ToggleTabShownCommand.Execute(Row(mobile, MobileTab.RecentlyAdded));

        var expected = new[] { MobileTab.Albums, MobileTab.Artists, MobileTab.Songs, MobileTab.Queue, MobileTab.Search, MobileTab.RecentlyAdded };
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

    [AvaloniaFact]
    public void Moving_a_tab_reorders_the_bar_and_is_remembered()
    {
        using var scope = Build();
        var mobile = scope.Mobile;

        mobile.MoveTabUpCommand.Execute(Row(mobile, MobileTab.Songs));
        mobile.MoveTabDownCommand.Execute(Row(mobile, MobileTab.Albums));

        var expected = new[] { MobileTab.Songs, MobileTab.Albums, MobileTab.Artists, MobileTab.Playlists, MobileTab.Queue, MobileTab.Search };
        Assert.Equal(expected, mobile.VisibleTabs);
        Assert.Equal(expected.Select(t => t.ToString()), scope.Parts.AppSettings.MobileTabs);
    }

    // All seven fit, drawn smaller (MobileTabBarLayoutTests); a bar of none
    // is no way to get anywhere.
    [AvaloniaFact]
    public void The_bar_holds_every_tab_at_most_and_one_at_least()
    {
        using var scope = Build();
        var mobile = scope.Mobile;

        mobile.ToggleTabShownCommand.Execute(Row(mobile, MobileTab.RecentlyAdded));
        Assert.Equal(7, mobile.VisibleTabs.Count);
        mobile.ToggleTabShownCommand.Execute(Row(mobile, MobileTab.RecentlyAdded));
        Assert.DoesNotContain(MobileTab.RecentlyAdded, mobile.VisibleTabs);

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

    // Taking the tab on screen out of the bar, from Settings over it, lands
    // on the first tab left - with nothing to go back to, since back would be
    // the tab just removed.
    [AvaloniaFact]
    public void Hiding_the_tab_on_screen_moves_to_the_first_tab()
    {
        using var scope = Build();
        var mobile = scope.Mobile;
        mobile.SelectTabCommand.Execute(nameof(MobileTab.Songs));

        mobile.ToggleTabShownCommand.Execute(Row(mobile, MobileTab.Songs));

        Assert.Equal(MobileTab.Albums, mobile.SelectedTab);
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
