using System.Collections.Generic;
using System.Linq;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels.Mobile;

using Xunit;

namespace Flower.Tests.ViewModels.Mobile;

// The phone's Now Playing sheet: its menu - the hamburger every screen has,
// with the song playing's own entries - and the artist's and album's names,
// which go there.
[Collection("PlatformDataDirectory")]
public class NowPlayingMenuTests : PinnedDataDirectory
{
    private static MainViewModelHarness.MobileParts PlayingOnTheSheet()
    {
        var tracks = new List<Track>
        {
            new() { Title = "Windflower", Album = "Ethio Jazz", Artists = "Mulatu Astatke", Path = "/m/1.mp3", Year = "1972" },
            new() { Title = "Yègellé Tezeta", Album = "Ethio Jazz", Artists = "Mulatu Astatke", Path = "/m/2.mp3", Year = "1972" },
            new() { Title = "Other", Album = "Other Album", Artists = "Someone Else", Path = "/m/3.mp3" },
        };
        var scope = MainViewModelHarness.BuildMobile(new Library(tracks), new MainPlaylist(tracks));
        Dispatcher.UIThread.RunJobs();
        scope.Parts.PlaylistControl.Play(tracks[0]);
        scope.Mobile.OpenNowPlayingCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        return scope;
    }

    private static void Pick(MobileMainViewModel mobile, string label) =>
        mobile.BuildNowPlayingMenu().SelectMany(s => s.Entries).Single(e => e.Label == label).Invoke();

    [AvaloniaFact]
    public void Going_to_the_album_opens_the_playing_songs_album_and_closes_the_sheet()
    {
        using var scope = PlayingOnTheSheet();

        Pick(scope.Mobile, "Go to Album");
        MainViewModelHarness.WaitForTheDrillIn(scope.Mobile, "Ethio Jazz");

        Assert.False(scope.Mobile.IsShowingNowPlaying);
        Assert.Equal(MobileTab.Albums, scope.Mobile.SelectedTab);
        Assert.Equal("Ethio Jazz", scope.Mobile.Main.SelectedSubItem);
    }

    [AvaloniaFact]
    public void Nothing_playing_leaves_only_settings()
    {
        var tracks = new List<Track> { new() { Title = "A", Album = "Al", Artists = "Ar", Path = "/m/a.mp3" } };
        using var scope = MainViewModelHarness.BuildMobile(new Library(tracks), new MainPlaylist(tracks));

        var entry = Assert.Single(scope.Mobile.BuildNowPlayingMenu().SelectMany(s => s.Entries));
        entry.Invoke();
        Assert.True(scope.Mobile.IsShowingSettings);
    }

    // A tap on the artist's name, or Go to Artist: the Artists tab, that
    // artist's albums, and the sheet out of the way.
    [AvaloniaFact]
    public void Going_to_the_artist_lands_on_their_albums_and_closes_the_sheet()
    {
        using var scope = PlayingOnTheSheet();
        var mobile = scope.Mobile;

        Pick(mobile, "Go to Artist");
        Dispatcher.UIThread.RunJobs();

        Assert.False(mobile.IsShowingNowPlaying);
        Assert.Equal(MobileTab.Artists, mobile.SelectedTab);
        Assert.True(mobile.IsShowingArtistAlbumGrid);
        Assert.Equal("Mulatu Astatke", mobile.CurrentFrame.SelectedArtistName);
        Assert.True(mobile.CanGoBack);
    }

    [AvaloniaFact]
    public void Track_info_opens_on_the_song_playing()
    {
        using var scope = PlayingOnTheSheet();
        var mobile = scope.Mobile;

        Pick(mobile, "Info");

        Assert.Equal(MobileSheet.TrackInfo, mobile.ActiveSheet);
        Assert.Equal("Windflower", mobile.ActionTarget?.Title);
    }
}
