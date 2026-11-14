using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia.Headless.XUnit;

using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests.ViewModels.Home;

// Recently Played's playback and persistence wiring through the app shell.
// Row reuse and layout data are covered by RecentlyPlayedModelTests.
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
}
