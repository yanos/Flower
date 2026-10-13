using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia.Headless.XUnit;

using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels;
using Flower.Views;

using Xunit;

namespace Flower.Tests.ViewModels;

// The desktop Artists page: the picked artist, and an album card each with
// its songs under it (MainViewModel.ArtistAlbums). The page header's filter
// narrows the column of names, by name alone, and leaves the cards whole.
[Collection("PlatformDataDirectory")]
public class ArtistPageTests : PinnedDataDirectory
{
    private static Track T(string title, string artist, string album, uint number, string year) => new()
    {
        Title = title, Artists = artist, Album = album, TrackNumber = number, Year = year,
        Path = $"/music/{artist}/{album}/{title}.mp3", Duration = TimeSpan.FromMinutes(3),
    };

    private MainViewModel OnArtist(string artist)
    {
        var library = new Library(new List<Track>
        {
            T("Hit Vibes", "SAINT PEPSI", "Hit Vibes", 1, "2013"),
            T("Have Faith", "SAINT PEPSI", "Hit Vibes", 2, "2013"),
            T("Carpark", "SAINT PEPSI", "Winner's Circle", 3, "2015"),
            T("Private Caller", "SAINT PEPSI", "Winner's Circle", 2, "2015"),
            T("Blackened", "Metallica", "...And Justice for All", 1, "1988"),
        });
        var vm = Own(MainViewModelHarness.Build(library, new MainPlaylist(library.Tracks))).Main;
        vm.Browser.Repopulate();
        vm.SelectedSidebarItem = vm.SidebarItems.Single(i => i.Kind == SidebarItemKind.Artists);
        vm.SetSelectedSubItems([artist]);
        return vm;
    }

    private static string[] Cards(MainViewModel vm) =>
        vm.ArtistAlbums.Select(a => a.Tile.Name).ToArray();

    private static string[] Songs(MainViewModel vm, string album) =>
        vm.ArtistAlbums.Single(a => a.Tile.Name == album).ExpandedTracks.Select(t => t.Title!).ToArray();

    [AvaloniaFact]
    public void Each_album_is_a_card_oldest_first_with_its_songs_in_track_order()
    {
        var vm = OnArtist("SAINT PEPSI");

        Assert.Equal(new[] { "Hit Vibes", "Winner's Circle" }, Cards(vm));
        Assert.Equal(new[] { "Private Caller", "Carpark" }, Songs(vm, "Winner's Circle"));
        Assert.Equal("SAINT PEPSI", vm.ArtistHeader?.Name);
    }

    // A song's title, an album's name: neither is an artist's, so neither
    // finds anyone - and the artist still picked keeps every card.
    [AvaloniaFact]
    public void The_filter_finds_artists_by_name_alone_and_leaves_the_cards_whole()
    {
        var vm = OnArtist("SAINT PEPSI");

        vm.FilterText = "pepsi";
        Assert.Equal(new[] { "SAINT PEPSI" }, vm.VisibleSubListItems);
        Assert.Equal(new[] { "Hit Vibes", "Winner's Circle" }, Cards(vm));
        Assert.Equal(2, Songs(vm, "Hit Vibes").Length);

        vm.FilterText = "carpark";
        Assert.Empty(vm.VisibleSubListItems);

        vm.FilterText = "winner";
        Assert.Empty(vm.VisibleSubListItems);
    }

    [AvaloniaFact]
    public void The_filter_narrows_the_column_of_names_and_clearing_it_brings_every_card_back()
    {
        var vm = OnArtist("SAINT PEPSI");

        vm.FilterText = "metal";
        Assert.Equal(new[] { "Metallica" }, vm.VisibleSubListItems);

        vm.FilterText = null;
        Assert.Equal(2, vm.VisibleSubListItems.Count);
        Assert.Equal(new[] { "Hit Vibes", "Winner's Circle" }, Cards(vm));
    }

    // The page shows whoever the column has picked, so a filter that hides
    // them moves the page to the first artist it still lists - and one that
    // finds nobody shows nobody. That rule is the desktop view's (MainView
    // .KeepAListedArtistPicked), so it is tested through one.
    [AvaloniaFact]
    public void Filtering_the_picked_artist_away_moves_the_page_to_the_first_one_still_listed()
    {
        TestIoc.EnsureConfigured();
        var vm = OnArtist("SAINT PEPSI");
        _ = new MainView { DataContext = vm };

        vm.FilterText = "metal";

        Assert.Equal(new[] { "Metallica" }, vm.SelectedSubItems);
        Assert.Equal(new[] { "...And Justice for All" }, Cards(vm));

        vm.FilterText = "nothing by anybody";

        Assert.Empty(vm.SelectedSubItems);
        Assert.Empty(vm.ArtistAlbums);
    }
}
