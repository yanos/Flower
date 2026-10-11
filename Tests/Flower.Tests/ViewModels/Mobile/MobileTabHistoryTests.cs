using System.Linq;

using Avalonia.Headless.XUnit;

using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels.Mobile;

using Xunit;

namespace Flower.Tests.ViewModels.Mobile;

// A tab remembers what it was showing. Albums, into one album, over to Artists
// and back by the tab bar lands on that album again, with the grid under it
// for Back - rather than at the top of the grid. Tapping the tab it is already
// on goes back to the tab's root instead.
[Collection("PlatformDataDirectory")]
public class MobileTabHistoryTests : PinnedDataDirectory
{
    private const string BigAlbum = "Album 0";

    private static MainViewModelHarness.MobileParts Build()
    {
        var tracks = Enumerable.Range(0, 200).Select(i => new Track
        {
            Title = $"Track {i:D3}", Path = $"/music/{i}.mp3",
            Album = i < 100 ? BigAlbum : $"Album {i}", Artists = $"Artist {i:D3}",
        }).ToList();
        return MainViewModelHarness.BuildMobile(new Library(tracks), new MainPlaylist(tracks));
    }

    private static void SelectTab(MobileMainViewModel vm, MobileTab tab)
    {
        vm.SelectTabCommand.Execute(tab.ToString());
        UiWait.Settle(() => vm.SelectedTab == tab, $"never switched to {tab}");
    }

    private static void Back(MobileMainViewModel vm, System.Func<bool> landed, string where)
    {
        vm.BackCommand.Execute(null);
        UiWait.Settle(landed, $"Back never landed on {where}");
    }

    [AvaloniaFact]
    public void A_tab_comes_back_on_the_album_it_was_showing_with_the_grid_under_it()
    {
        using var scope = Build();
        var vm = scope.Mobile;
        SelectTab(vm, MobileTab.Albums);
        vm.SelectAlbumOrArtistCommand.Execute(BigAlbum);
        MainViewModelHarness.WaitForTheDrillIn(vm, BigAlbum);

        SelectTab(vm, MobileTab.Artists);
        UiWait.Settle(() => vm.IsShowingArtistPicker, "Artists never showed its picker");

        SelectTab(vm, MobileTab.Albums);
        MainViewModelHarness.WaitForTheDrillIn(vm, BigAlbum);

        // Back walks down through the tab before leaving it.
        Back(vm, () => vm.IsShowingAlbumGrid, "the album grid");
        Back(vm, () => vm.IsShowingArtistPicker, "the artist picker");
    }

    [AvaloniaFact]
    public void A_tab_never_left_drilled_in_starts_at_its_root()
    {
        using var scope = Build();
        var vm = scope.Mobile;
        SelectTab(vm, MobileTab.Albums);
        SelectTab(vm, MobileTab.Artists);
        SelectTab(vm, MobileTab.Albums);

        UiWait.Settle(() => vm.IsShowingAlbumGrid, "Albums never came back on its grid");
    }

    [AvaloniaFact]
    public void Tapping_the_tab_inside_an_album_goes_back_to_the_grid()
    {
        using var scope = Build();
        var vm = scope.Mobile;
        SelectTab(vm, MobileTab.Artists);
        SelectTab(vm, MobileTab.Albums);
        vm.SelectAlbumOrArtistCommand.Execute(BigAlbum);
        MainViewModelHarness.WaitForTheDrillIn(vm, BigAlbum);

        vm.SelectTabCommand.Execute(nameof(MobileTab.Albums));
        UiWait.Settle(() => vm.IsShowingAlbumGrid, "the tab never went back to its grid");

        // The album is closed, not under the grid: Back leaves the tab.
        Back(vm, () => vm.IsShowingArtistPicker, "the artist picker");
    }
}
