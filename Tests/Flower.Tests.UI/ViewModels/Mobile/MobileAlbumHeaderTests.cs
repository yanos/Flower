using System.Collections.Generic;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels.Mobile;

using Xunit;

namespace Flower.Tests.ViewModels.Mobile;

// The artist named by the album header opens the corresponding artist page.
[Collection("PlatformDataDirectory")]
public class MobileAlbumHeaderTests : PinnedDataDirectory
{
    private static Track Song(string title, string artists, string? albumArtists = null, string? year = null) => new()
    {
        Title = title, Path = $"/music/{title}.mp3", Album = "Album", Artists = artists, AlbumArtists = albumArtists,
        Year = year,
    };

    [AvaloniaFact]
    public void Tapping_the_artist_line_lands_on_their_albums()
    {
        var tracks = new List<Track> { Song("One", "Mulatu Astatke") };
        using var parts = MainViewModelHarness.BuildMobile(new Library(tracks), new MainPlaylist(tracks));
        var mobile = parts.Mobile;
        mobile.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        mobile.SelectAlbumOrArtistCommand.Execute(MainViewModelHarness.AlbumKey(mobile, "Album"));
        MainViewModelHarness.WaitForTheDrillIn(mobile, "Album");

        mobile.GoToArtistCommand.Execute(mobile.CurrentAlbumHeader?.ArtistPage);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(MobileTab.Artists, mobile.SelectedTab);
        Assert.True(mobile.IsShowingArtistAlbumGrid);
        Assert.Equal("Mulatu Astatke", mobile.CurrentFrame.SelectedArtistName);
        Assert.True(mobile.CanGoBack);
    }
}
