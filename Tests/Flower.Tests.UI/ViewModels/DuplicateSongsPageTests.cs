using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;

using Flower.Models;
using Flower.Persistence;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests.ViewModels;

// The Duplicates page: there while the library holds a song twice, listing
// every copy, and only for someone able to do something about it.
[Collection("PlatformDataDirectory")]
public class DuplicateSongsPageTests : PinnedDataDirectory
{
    private static Track Song(string title, string path) => new()
    {
        Title = title, Artists = "Waxahatchee", Album = "Tigers Blood",
        Duration = TimeSpan.FromSeconds(200), Path = path,
    };

    private MainViewModel Build(List<Track> tracks, AppSettings? settings = null)
    {
        var library = new Library(tracks);
        return Own(MainViewModelHarness.Build(library, new MainPlaylist(library.Tracks), settings)).Main;
    }

    private static SidebarItem? Row(MainViewModel vm) =>
        vm.SidebarItems.FirstOrDefault(i => i.Kind == SidebarItemKind.Duplicates);

    [AvaloniaFact]
    public async Task Show_opens_a_page_of_every_copy_and_nothing_else()
    {
        var vm = Build([Song("3 Sisters", "/m/01. a.mp3"), Song("3 Sisters", "/m/01 a.mp3"), Song("Bored", "/m/06.mp3")]);
        Assert.True(vm.DuplicateSongsNotice.IsOpen);

        vm.DuplicateSongsNotice.ShowCommand.Execute(null);
        await vm.RebuildRowsImmediatelyAsync();

        Assert.Equal(SidebarItemKind.Duplicates, vm.SelectedSidebarItem?.Kind);
        Assert.Equal(2, vm.Rows.Count);
    }

    [AvaloniaFact]
    public async Task A_phone_gets_back_to_the_page_from_Homes_menu_once_the_banner_is_gone()
    {
        var library = new Library([Song("3 Sisters", "/m/01. a.mp3"), Song("3 Sisters", "/m/01 a.mp3")]);
        var vm = Own(MainViewModelHarness.BuildMobile(library, new MainPlaylist(new List<Track>()))).Mobile;
        vm.Main.DuplicateSongsNotice.DismissCommand.Execute(null);

        vm.BuildScreenMenu(vm.CurrentFrame).SelectMany(s => s.Entries).Single(e => e.Label == "Duplicates").Invoke();
        await vm.Main.RebuildRowsImmediatelyAsync();

        Assert.True(vm.Main.IsShowingDuplicateSongs);
        Assert.Equal(2, vm.Main.Rows.Count);
    }

    [AvaloniaFact]
    public void A_phone_with_no_duplicates_has_no_menu_entry()
    {
        var library = new Library([Song("3 Sisters", "/m/01.mp3"), Song("Bored", "/m/06.mp3")]);
        var vm = Own(MainViewModelHarness.BuildMobile(library, new MainPlaylist(new List<Track>()))).Mobile;

        Assert.DoesNotContain(vm.BuildScreenMenu(vm.CurrentFrame).SelectMany(s => s.Entries), e => e.Label == "Duplicates");
    }

    [Fact]
    public void A_copy_is_named_by_its_file_here_or_else_the_servers()
    {
        var local = new Track { Path = "/m/Tigers Blood/01. 3 Sisters.mp3", OriginRelativePath = "Waxahatchee/Tigers Blood/01. 3 Sisters.mp3" };
        var serverOnly = new Track { OriginRelativePath = "Waxahatchee/Tigers Blood/01 3 Sisters.mp3" };

        Assert.Equal("01. 3 Sisters.mp3", Flower.Services.DuplicateSongs.FileNameOf(local));
        Assert.Equal("01 3 Sisters.mp3", Flower.Services.DuplicateSongs.FileNameOf(serverOnly));
    }

    [AvaloniaFact]
    public void A_library_without_duplicates_has_no_page()
    {
        var vm = Build([Song("3 Sisters", "/m/01.mp3"), Song("Bored", "/m/06.mp3")]);

        Assert.Null(Row(vm));
        Assert.False(vm.DuplicateSongsNotice.IsOpen);
    }

    [AvaloniaFact]
    public void A_listener_is_not_told_about_the_servers_duplicates()
    {
        var settings = new AppSettings { PairedServerFingerprint = "server", PairedServerGrantsAdmin = false };

        var vm = Build([Song("3 Sisters", "/m/01. a.mp3"), Song("3 Sisters", "/m/01 a.mp3")], settings);

        Assert.Null(Row(vm));
        Assert.False(vm.DuplicateSongsNotice.IsOpen);
    }

    [AvaloniaFact]
    public void An_administrator_is()
    {
        var settings = new AppSettings { PairedServerFingerprint = "server", PairedServerGrantsAdmin = true };

        var vm = Build([Song("3 Sisters", "/m/01. a.mp3"), Song("3 Sisters", "/m/01 a.mp3")], settings);

        Assert.NotNull(Row(vm));
        Assert.True(vm.DuplicateSongsNotice.IsOpen);
    }
}
