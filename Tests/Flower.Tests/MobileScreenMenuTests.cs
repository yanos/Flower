using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels.Mobile;

using Xunit;

namespace Flower.Tests;

// The hamburger in every phone screen's header (ScreenSlot), which replaced
// the gear. Settings is always its last entry; what sits above it is the
// screen's to say - a sort only where the order is not the point of the
// screen, Download All and Add to Playlist only over a bounded set of songs.
[Collection("PlatformDataDirectory")]
public class MobileScreenMenuTests : PinnedDataDirectory
{
    public MobileScreenMenuTests() => TestIoc.EnsureConfigured();

    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // Alphabetical, by year and by date added are three different orders.
    private static readonly Track[] Songs =
    [
        Make("Zero Hour", "Bee Thousand", "1994", addedDay: 1),
        Make("Apple Tree", "Bee Thousand", "1994", addedDay: 1),
        Make("Maps", "Alien Lanes", "1995", addedDay: 3),
        Make("Quality of Armor", "Propeller", "1992", addedDay: 2),
    ];

    private static Track Make(string title, string album, string year, int addedDay) => new()
    {
        Title = title,
        Album = album,
        Artists = "Guided by Voices",
        Year = year,
        Path = "/music/" + title + ".flac",
        DiscNumber = 1,
        TrackNumber = 1,
        Duration = TimeSpan.FromMinutes(3),
        DateAdded = Epoch.AddDays(addedDay),
    };

    private MobileMainViewModel Build()
    {
        var library = new Library(Songs.ToList());
        return Own(MainViewModelHarness.BuildMobile(library, new MainPlaylist(new List<Track>()))).Mobile;
    }

    private static IReadOnlyList<string> Labels(MobileMainViewModel vm) =>
        vm.BuildScreenMenu(vm.CurrentFrame).SelectMany(s => s.Entries).Select(e => e.Label).ToList();

    private static ScreenMenuEntry Entry(MobileMainViewModel vm, string label) =>
        vm.BuildScreenMenu(vm.CurrentFrame).SelectMany(s => s.Entries).Single(e => e.Label == label);

    [AvaloniaFact]
    public async Task Songs_can_be_sorted_five_ways_and_shuffled()
    {
        var vm = Build();
        vm.SelectTabCommand.Execute(nameof(MobileTab.Songs));
        await WaitForRows(vm, 4);

        Assert.Equal(["Name", "Artist", "Album", "Year", "Date Added", "Shuffle", "Delete Local Files", "Settings"], Labels(vm));
        Assert.Equal(ListSortDirection.Ascending, Entry(vm, "Name").Direction);
        Assert.Null(Entry(vm, "Year").Direction);
    }

    [AvaloniaFact]
    public async Task Picking_date_added_reorders_the_songs_newest_first_and_is_remembered()
    {
        var vm = Build();
        vm.SelectTabCommand.Execute(nameof(MobileTab.Songs));
        await WaitForRows(vm, 4);

        Entry(vm, "Date Added").Invoke();
        string[] newestFirst = ["Maps", "Quality of Armor"];
        await WaitFor(() => vm.Main.Rows.Take(2).Select(r => r.Track.Title).SequenceEqual(newestFirst));

        Assert.Equal(newestFirst, vm.Main.Rows.Take(2).Select(r => r.Track.Title));
        Assert.Equal(ListSortDirection.Descending, Entry(vm, "Date Added").Direction);
        Assert.Equal("DateAdded:desc", vm.Main.MobileSortFor(nameof(MobileSortScreen.Songs)));
    }

    [AvaloniaFact]
    public async Task The_album_grid_sorts_by_year()
    {
        var vm = Build();
        vm.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        await WaitFor(() => vm.AlbumGridRows.Count > 0);

        Assert.Equal(["Name", "Artist", "Year", "Date Added", "Shuffle", "Delete Local Files", "Settings"], Labels(vm));
        Entry(vm, "Year").Invoke();

        Assert.Equal(["Propeller", "Bee Thousand", "Alien Lanes"],
            vm.AlbumGridRows.SelectMany(r => r.Tiles).Select(t => t.Name));
    }

    // Picking the sort in use again turns it round, arrow and all - and
    // turns it back the next time.
    [AvaloniaFact]
    public async Task Picking_the_same_sort_again_reverses_it()
    {
        var vm = Build();
        vm.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        await WaitFor(() => vm.AlbumGridRows.Count > 0);
        Entry(vm, "Year").Invoke();

        Entry(vm, "Year").Invoke();
        Assert.Equal(ListSortDirection.Descending, Entry(vm, "Year").Direction);
        Assert.Equal(["Alien Lanes", "Bee Thousand", "Propeller"],
            vm.AlbumGridRows.SelectMany(r => r.Tiles).Select(t => t.Name));

        Entry(vm, "Year").Invoke();
        Assert.Equal(ListSortDirection.Ascending, Entry(vm, "Year").Direction);
        Assert.Equal(["Propeller", "Bee Thousand", "Alien Lanes"],
            vm.AlbumGridRows.SelectMany(r => r.Tiles).Select(t => t.Name));
    }

    // Name turned round is Z to A.
    [AvaloniaFact]
    public async Task Picking_name_again_on_songs_runs_them_z_to_a()
    {
        var vm = Build();
        vm.SelectTabCommand.Execute(nameof(MobileTab.Songs));
        await WaitForRows(vm, 4);

        Entry(vm, "Name").Invoke();
        string[] zToA = ["Zero Hour", "Quality of Armor", "Maps", "Apple Tree"];
        await WaitFor(() => vm.Main.Rows.Select(r => r.Track.Title).SequenceEqual(zToA));

        Assert.Equal(zToA, vm.Main.Rows.Select(r => r.Track.Title));
        Assert.Equal(ListSortDirection.Descending, Entry(vm, "Name").Direction);
    }

    // An album is its own order, so no sort - but its songs can go into a
    // playlist from here.
    [AvaloniaFact]
    public async Task An_album_has_no_sort_but_can_be_added_to_a_playlist()
    {
        var vm = Build();
        vm.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        vm.SelectAlbumOrArtistCommand.Execute("Bee Thousand");
        await WaitForRows(vm, 2);

        Assert.Equal(["Shuffle", "Play Next", "Add to Queue", "Add to Playlist", "Delete Local Files", "Settings"], Labels(vm));
        Entry(vm, "Add to Playlist").Invoke();
        Assert.True(vm.IsShowingAddToPlaylist);
    }

    // Every screen can shuffle what it shows, and delete the files of it this
    // phone has. Search has nothing to show until something is typed, so
    // until then it has only Settings.
    [AvaloniaFact]
    public void Recently_added_and_search_have_only_shuffle_delete_and_settings()
    {
        var vm = Build();
        vm.SelectTabCommand.Execute(nameof(MobileTab.RecentlyAdded));
        Assert.Equal(["Shuffle", "Delete Local Files", "Settings"], Labels(vm));

        vm.SelectTabCommand.Execute(nameof(MobileTab.Search));
        Assert.Equal(["Settings"], Labels(vm));
        vm.SearchQuery = "Maps";
        Assert.Equal(["Shuffle", "Delete Local Files", "Settings"], Labels(vm));

        Entry(vm, "Settings").Invoke();
        Assert.True(vm.IsShowingSettings);
    }

    // What is shuffled is what the screen shows: here every song on every
    // album in the grid, with shuffle left on.
    [AvaloniaFact]
    public async Task Shuffle_on_the_album_grid_plays_everything_on_it_shuffled()
    {
        var vm = Build();
        vm.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        await WaitFor(() => vm.AlbumGridRows.Count > 0);

        Entry(vm, "Shuffle").Invoke();
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.PlaylistControl.IsShuffleEnabled);
        Assert.Contains(vm.PlaylistControl.CurrentlyPlayingTrack, Songs);
        Assert.Equal(Songs.Select(t => t.Title).Order(), vm.PlaylistControl.CurrentPlaylist.Tracks.Select(t => t.Title).Order());
    }

    // With the pull-down filter open, only what it left.
    [AvaloniaFact]
    public async Task Shuffle_on_a_filtered_grid_plays_only_what_the_filter_left()
    {
        var vm = Build();
        vm.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        await WaitFor(() => vm.AlbumGridRows.Count > 0);
        vm.OpenScreenFilter();
        vm.ScreenFilter = "Propeller";
        await WaitFor(() => vm.AlbumGridRows.SelectMany(r => r.Tiles).Count() == 1);

        Entry(vm, "Shuffle").Invoke();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["Quality of Armor"], vm.PlaylistControl.CurrentPlaylist.Tracks.Select(t => t.Title));
    }

    // The artist list is sortable too: by name, by how often each artist's
    // songs have been played, and by their newest song - the last two from the
    // top, and the index bar goes to dots for either. Name turned round keeps
    // its letters, Z first.
    [AvaloniaFact]
    public async Task Artists_sort_by_name_most_played_and_date_added()
    {
        Track Song(string artist, int plays, int addedDay) => new()
        {
            Title = artist + " song",
            Album = artist + " album",
            Artists = artist,
            Path = "/music/" + artist + ".flac",
            PlayCount = plays,
            DateAdded = Epoch.AddDays(addedDay),
        };
        var library = new Library([Song("Built to Spill", 3, 2), Song("Autechre", 1, 3), Song("Cocteau Twins", 7, 1)]);
        var vm = Own(MainViewModelHarness.BuildMobile(library, new MainPlaylist(new List<Track>()))).Mobile;
        vm.SelectTabCommand.Execute(nameof(MobileTab.Artists));
        await WaitFor(() => vm.ArtistPickerItems.Count == 3);
        IEnumerable<string> Names() => vm.ArtistPickerItems.Select(r => r.Name);

        Assert.Equal(["Name", "Most Played", "Date Added", "Shuffle", "Delete Local Files", "Settings"], Labels(vm));
        Assert.Equal(["Autechre", "Built to Spill", "Cocteau Twins"], Names());
        Assert.True(vm.ArtistPickerIsAlphabetical);

        Entry(vm, "Most Played").Invoke();
        Assert.Equal(["Cocteau Twins", "Built to Spill", "Autechre"], Names());
        Assert.Equal(ListSortDirection.Descending, Entry(vm, "Most Played").Direction);
        Assert.False(vm.ArtistPickerIsAlphabetical);

        Entry(vm, "Date Added").Invoke();
        Assert.Equal(["Autechre", "Built to Spill", "Cocteau Twins"], Names());

        Entry(vm, "Name").Invoke();
        Assert.True(vm.ArtistPickerIsAlphabetical);
        Assert.False(vm.ArtistPickerRunsZToA);
        Entry(vm, "Name").Invoke();
        Assert.Equal(["Cocteau Twins", "Built to Spill", "Autechre"], Names());
        Assert.True(vm.ArtistPickerIsAlphabetical);
        Assert.True(vm.ArtistPickerRunsZToA);
    }

    // The album grid's bar is letters while it runs A to Z on a name - the
    // album's or the artist's - and dots otherwise.
    [AvaloniaFact]
    public async Task The_album_grid_is_alphabetical_only_by_name_or_artist()
    {
        var vm = Build();
        vm.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        await WaitFor(() => vm.AlbumGridRows.Count > 0);
        Assert.True(vm.AlbumGridIsAlphabetical);

        Entry(vm, "Artist").Invoke();
        Assert.True(vm.AlbumGridIsAlphabetical);
        Entry(vm, "Year").Invoke();
        Assert.False(vm.AlbumGridIsAlphabetical);
        Entry(vm, "Date Added").Invoke();
        Assert.False(vm.AlbumGridIsAlphabetical);
    }

    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 3000)
    {
        for (var waited = 0; waited < timeoutMs && !condition(); waited += 20)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task WaitForRows(MobileMainViewModel vm, int count)
    {
        await WaitFor(() => vm.Main.Rows.Count == count);
        Assert.Equal(count, vm.Main.Rows.Count);
    }
}
