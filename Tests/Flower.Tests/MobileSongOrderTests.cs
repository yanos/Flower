using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels.Mobile;

using Xunit;

namespace Flower.Tests;

// The phone's Songs tab is the whole library in one list, and it used to come
// in whatever order the persisted desktop sort column said - track number, by
// default, which across a library means "every track 1, then every track 2".
// There are no column headers on a phone to fix that with, so each screen
// states the order it wants: alphabetical here, album order inside an album.
[Collection("PlatformDataDirectory")]
public class MobileSongOrderTests : PinnedDataDirectory
{
    public MobileSongOrderTests() => TestIoc.EnsureConfigured();

    // Deliberately in neither alphabetical nor track order as written, and
    // with the alphabetical order cutting across the album order.
    private static readonly Track[] Songs =
    [
        Make("Zero Hour", "Bee Thousand", disc: 1, track: 1),
        Make("Apple Tree", "Bee Thousand", disc: 1, track: 2),
        Make("Maps", "Alien Lanes", disc: 1, track: 1),
    ];

    private static Track Make(string title, string album, uint disc, uint track) => new()
    {
        Title = title,
        Album = album,
        Artists = "Guided by Voices",
        Path = "/music/" + title + ".flac",
        DiscNumber = disc,
        TrackNumber = track,
        Duration = TimeSpan.FromMinutes(3),
        DateAdded = DateTimeOffset.UtcNow,
    };

    private MobileMainViewModel Build()
    {
        var library = new Library(Songs.ToList());
        return Own(MainViewModelHarness.BuildMobile(library, new MainPlaylist(new List<Track>()))).Mobile;
    }

    [AvaloniaFact]
    public async Task The_songs_tab_is_in_alphabetical_order()
    {
        var vm = Build();
        vm.SelectTabCommand.Execute(nameof(MobileTab.Songs));
        await WaitForRows(vm, 3);
        await WaitForOrder(vm, Alphabetical);

        Assert.Equal(Alphabetical, vm.Main.Rows.Select(r => r.Track.Title));
    }

    // An album is still its own order - the alphabetical rule is about the
    // library as a list of songs, not about every list of songs.
    [AvaloniaFact]
    public async Task An_album_is_still_in_album_order()
    {
        var vm = Build();
        vm.SelectTabCommand.Execute(nameof(MobileTab.Songs));
        await WaitForRows(vm, 3);

        vm.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        vm.SelectAlbumOrArtistCommand.Execute("Bee Thousand");
        await WaitForRows(vm, 2);

        Assert.Equal(new[] { "Zero Hour", "Apple Tree" }, vm.Main.Rows.Select(r => r.Track.Title));
    }

    // And going back out of one puts the library back the way it was, rather
    // than leaving it in the album's order.
    [AvaloniaFact]
    public async Task Coming_back_from_an_album_is_alphabetical_again()
    {
        var vm = Build();
        vm.SelectTabCommand.Execute(nameof(MobileTab.Songs));
        await WaitForRows(vm, 3);
        vm.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        vm.SelectAlbumOrArtistCommand.Execute("Bee Thousand");
        await WaitForRows(vm, 2);

        // Back out of the album, then back again to the Songs tab.
        vm.BackCommand.Execute(null);
        await WaitFor(() => !vm.IsShowingAlbumTrackList);
        vm.BackCommand.Execute(null);
        await WaitForRows(vm, 3);
        await WaitForOrder(vm, Alphabetical);

        Assert.Equal(Alphabetical, vm.Main.Rows.Select(r => r.Track.Title));
    }

    private static readonly string[] Alphabetical = ["Apple Tree", "Maps", "Zero Hour"];

    // Three rows is not the same as three rows in the new order. The count can
    // already be right from a build in the previous order - album order, here -
    // with the re-sort the tab asked for still to land, and a macOS CI runner
    // asserted in between: "Maps" first. So wait for the order itself; the
    // assertion after it is what reports a wrong one.
    private static Task WaitForOrder(MobileMainViewModel vm, string[] titles) =>
        WaitFor(() => vm.Main.Rows.Select(r => r.Track.Title).SequenceEqual(titles));

    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 3000)
    {
        for (var waited = 0; waited < timeoutMs && !condition(); waited += 20)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task WaitForRows(MobileMainViewModel vm, int count, int timeoutMs = 3000)
    {
        for (var waited = 0; waited < timeoutMs && vm.Main.Rows.Count != count; waited += 20)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(count, vm.Main.Rows.Count);
    }
}
