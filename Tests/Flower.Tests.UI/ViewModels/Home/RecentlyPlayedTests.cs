using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia.Headless.XUnit;

using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests.ViewModels.Home;

// Home's Recently Played, which is songs: played from the picked one down,
// taken off one at a time, and laid out so many to a row.
[Collection("PlatformDataDirectory")]
public class RecentlyPlayedTests : PinnedDataDirectory
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Now;

    private static List<Track> Album(string name, int playedMinutesAgo) =>
        Enumerable.Range(1, 2).Select(i => new Track
        {
            Title = $"{name} {i}",
            Path = $"/music/{name}/{i}.flac",
            Album = name,
            Artists = "Artist",
            TrackNumber = (uint)i,
            Duration = TimeSpan.FromMinutes(4),
            LastPlayedAt = Now.AddMinutes(-playedMinutesAgo - i),
        }).ToList();

    private MainViewModelHarness.Parts Build(params List<Track>[] albums)
    {
        var tracks = albums.SelectMany(a => a).ToList();
        return Own(MainViewModelHarness.BuildParts(new Library(tracks), new MainPlaylist(tracks)));
    }

    [AvaloniaFact]
    public void A_song_picked_plays_the_shelf_from_it_down()
    {
        var parts = Build(Album("Blue", 5), Album("Red", 50));
        var home = parts.Main.Home;
        home.Rebuild();
        var shelf = home.RecentlyPlayed.Select(r => r.Track).ToList();

        home.PlayRecentlyPlayed(home.RecentlyPlayed[1]);

        Assert.Same(shelf[1], parts.PlaylistControl.CurrentlyPlayingTrack);
        Assert.Equal(shelf, parts.PlaylistControl.CurrentPlaylist.Tracks);
    }

    // The rest of its album stays, and so does it once it is played again.
    [AvaloniaFact]
    public void A_song_taken_off_stays_off_until_it_is_played_again_and_is_saved()
    {
        var parts = Build(Album("Blue", 5));
        var home = parts.Main.Home;
        home.Rebuild();
        var song = home.RecentlyPlayed[0].Track;

        home.HideFromRecentlyPlayed(song);
        home.Rebuild();

        Assert.Equal(new[] { "Blue 2" }, home.RecentlyPlayed.Select(r => r.Track.Title));
        Assert.Contains(song.Id.ToKey(), parts.AppSettings.HomeRecentlyPlayedHidden.Keys);

        song.LastPlayedAt = Now;
        home.Rebuild();

        Assert.Equal(new[] { "Blue 1", "Blue 2" }, home.RecentlyPlayed.Select(r => r.Track.Title));
    }

    // A song still on the shelf keeps its row across a rebuild, and with it
    // the cover the row has already loaded.
    [AvaloniaFact]
    public void A_song_still_on_the_shelf_keeps_its_row()
    {
        var parts = Build(Album("Blue", 5), Album("Red", 50));
        var home = parts.Main.Home;
        home.Rebuild();
        var red = home.RecentlyPlayed.Single(r => r.Track.Title == "Red 1");

        home.RecentlyPlayed[0].Track.LastPlayedAt = Now.AddMinutes(-500);
        home.Rebuild();

        Assert.Same(red, home.RecentlyPlayed.Single(r => r.Track.Title == "Red 1"));
    }

    [AvaloniaFact]
    public void The_songs_go_so_many_to_a_row_under_a_heading_that_can_lead_to_history()
    {
        var parts = Build(Album("Blue", 5), Album("Red", 50));
        var home = parts.Main.Home;
        home.RecentlyAddedMonths = 0;
        home.Rebuild();

        var desktop = HomeShelfRows.Build(home, columns: 5, songColumns: 2, historyLink: true);
        var header = Assert.IsType<HomeShelfHeaderRow>(desktop[0]);
        Assert.True(header.ShowsHistoryLink);
        Assert.Equal(new[] { 2, 2 }, desktop.Skip(1).Cast<HomeSongRow>().Select(r => r.Songs.Count));

        var phone = HomeShelfRows.Build(home, columns: 2);
        Assert.False(Assert.IsType<HomeShelfHeaderRow>(phone[0]).ShowsHistoryLink);
        Assert.Equal(4, phone.OfType<HomeSongRow>().Count());
    }
}
