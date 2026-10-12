using System;
using System.Collections.Generic;
using System.Threading;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels.Mobile;

using Xunit;

namespace Flower.Tests.ViewModels.Mobile;

// The album page names the artist the songs are by (Track.Artists), not the
// album artist tag, which is a sorting key and often not the name the artist
// goes by - and falls back to that tag only where the songs disagree. Its
// heading carries the album's year in brackets, when a song gives one.
[Collection("PlatformDataDirectory")]
public class MobileAlbumHeaderTests : PinnedDataDirectory
{
    private static string? HeaderArtistOf(List<Track> tracks) => HeaderOf(tracks)?.Artist;

    private static AlbumTileViewModel? HeaderOf(List<Track> tracks)
    {
        using var parts = MainViewModelHarness.BuildMobile(new Library(tracks), new MainPlaylist(tracks));
        parts.Mobile.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        Dispatcher.UIThread.RunJobs();
        parts.Mobile.SelectAlbumOrArtistCommand.Execute("Album");

        // The album's page is built off the UI thread (MainViewModel's row
        // rebuild), so it is waited for rather than assumed after one pass.
        var deadline = Environment.TickCount64 + 5_000;
        while (parts.Mobile.CurrentAlbumHeader == null && Environment.TickCount64 < deadline)
        {
            using var cts = new CancellationTokenSource(20);
            Dispatcher.UIThread.MainLoop(cts.Token);
        }
        return parts.Mobile.CurrentAlbumHeader;
    }

    private static Track Song(string title, string artists, string? albumArtists = null, string? year = null) => new()
    {
        Title = title, Path = $"/music/{title}.mp3", Album = "Album", Artists = artists, AlbumArtists = albumArtists,
        Year = year,
    };

    // A reissue's bonus track carries the reissue's year; the album is the
    // original's.
    [AvaloniaFact]
    public void The_heading_carries_the_albums_earliest_year()
    {
        Assert.Equal("Album (1975)", HeaderOf(
        [
            Song("One", "Mulatu Astatke", year: "1975"),
            Song("Bonus", "Mulatu Astatke", year: "1998"),
        ])?.Heading);
    }

    // An untagged album is not followed by an empty "()".
    [AvaloniaFact]
    public void An_album_with_no_year_is_headed_by_its_name_alone()
    {
        Assert.Equal("Album", HeaderOf([Song("One", "Mulatu Astatke")])?.Heading);
    }

    [AvaloniaFact]
    public void The_header_names_the_songs_artist_rather_than_the_album_artist()
    {
        Assert.Equal("Explosions in the Sky", HeaderArtistOf(
        [
            Song("One", "Explosions in the Sky", albumArtists: "Explosions In The Sky"),
            Song("Two", "Explosions in the Sky", albumArtists: "Explosions In The Sky"),
        ]));
    }

    // One artist in two cases is one artist, named the way most songs do.
    [AvaloniaFact]
    public void An_artist_spelled_in_two_cases_is_still_the_one_artist()
    {
        Assert.Equal("Add N to (X)", HeaderArtistOf(
        [
            Song("One", "Add N to (X)"),
            Song("Two", "Add N to (X)"),
            Song("Three", "Add N To (X)"),
        ]));
    }

    // A guest on one song: the album artist says whose album it is.
    [AvaloniaFact]
    public void Songs_by_several_fall_back_to_the_album_artist()
    {
        Assert.Equal("Death From Above 1979", HeaderArtistOf(
        [
            Song("One", "Death From Above 1979", albumArtists: "Death From Above 1979"),
            Song("Two", "Death From Above 1979 feat. Someone", albumArtists: "Death From Above 1979"),
        ]));
    }

    [AvaloniaFact]
    public void Songs_and_album_artists_that_all_differ_are_various_artists()
    {
        Assert.Equal("Various Artists", HeaderArtistOf(
        [
            Song("One", "Artist A"),
            Song("Two", "Artist B"),
        ]));
    }
}
