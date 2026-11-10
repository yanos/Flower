using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels.Mobile;

using Xunit;

namespace Flower.Tests.ViewModels.Mobile;

// An album's own screen: the songs of the album tapped, and only those.
[Collection("PlatformDataDirectory")]
public class MobileAlbumScreenTests : PinnedDataDirectory
{
    private static Track Song(string title, string album, string artist = "Blur") => new()
    {
        Title = title, Path = $"/music/{title}.mp3", Album = album, Artists = artist,
    };

    private static void WaitFor(Func<bool> done)
    {
        var deadline = Environment.TickCount64 + 5_000;
        while (!done() && Environment.TickCount64 < deadline)
        {
            using var cts = new CancellationTokenSource(20);
            Dispatcher.UIThread.MainLoop(cts.Token);
        }
    }

    // Two bands' albums called "EP" are two covers in the grid, and each
    // opens on its own band's songs - not one screen holding both.
    [AvaloniaFact]
    public void An_album_shares_its_screen_with_no_other_album_of_its_name()
    {
        var tracks = new List<Track>
        {
            Song("Vexation", "EP", "Crystal Antlers"),
            Song("Single Again", "EP", "The Fiery Furnaces"),
            Song("Here Comes the Summer", "EP", "The Fiery Furnaces"),
        };
        using var parts = MainViewModelHarness.BuildMobile(new Library(tracks), new MainPlaylist(tracks));
        var mobile = parts.Mobile;
        mobile.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        Dispatcher.UIThread.RunJobs();

        var tiles = mobile.AlbumGridRows.SelectMany(r => r.Tiles).ToList();
        Assert.Equal(2, tiles.Count);

        mobile.SelectAlbumOrArtistCommand.Execute(tiles.Single(t => t.Artist == "The Fiery Furnaces"));
        WaitFor(() => mobile.CurrentAlbumHeader != null && mobile.AlbumDetailRows.Count > 0);

        Assert.Equal("The Fiery Furnaces", mobile.CurrentAlbumHeader?.Artist);
        Assert.Equal(
            new[] { "Here Comes the Summer", "Single Again" },
            mobile.AlbumDetailRows.Select(r => r.Track.Title).Order());
    }

    // Renaming the album on screen from Get Info keeps the screen on the
    // album, under its new name, rather than on the old name with no songs
    // left under it, which said "Nothing here".
    [AvaloniaFact]
    public void A_renamed_album_stays_on_screen_under_its_new_name()
    {
        var one = Song("One", "Album");
        var two = Song("Two", "Album");
        var tracks = new List<Track> { one, two, Song("Other", "Elsewhere") };
        using var parts = MainViewModelHarness.BuildMobile(new Library(tracks), new MainPlaylist(tracks));
        var mobile = parts.Mobile;
        mobile.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        Dispatcher.UIThread.RunJobs();
        mobile.SelectAlbumOrArtistCommand.Execute(MainViewModelHarness.AlbumKey(mobile, "Album"));
        MainViewModelHarness.WaitForTheDrillIn(mobile, "Album");

        one.Album = two.Album = "Album (Remastered)";
        parts.Parts.Library.NotifyTracksChanged([one, two], TrackChange.Tags);

        WaitFor(() => mobile.CurrentAlbumHeader?.Name == "Album (Remastered)" && mobile.AlbumDetailRows.Count == 2);

        Assert.True(mobile.IsShowingAlbumTrackList);
        Assert.Equal("Album (Remastered)", mobile.CurrentAlbumHeader?.Name);
        Assert.Equal(2, mobile.AlbumDetailRows.Count);
        Assert.False(mobile.IsContentEmpty);
    }
}
