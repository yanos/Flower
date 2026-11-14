using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests.ViewModels;

// The same through the real MainViewModel: what the sidebar shows is what is
// recorded, and Back and Forward show it again.
[Collection("PlatformDataDirectory")]
public class MainViewModelBackForwardTests : PinnedDataDirectory
{
    private static Track T(string title, string album) =>
        new() { Title = title, Artists = "Someone", Album = album, Path = $"/music/{title}.mp3", Duration = TimeSpan.FromMinutes(3) };

    private static SidebarItem Row(MainViewModel vm, SidebarItemKind kind) =>
        vm.SidebarItems.Single(i => i.Kind == kind);

    [AvaloniaFact]
    public async Task Back_and_forward_return_to_the_pages_and_the_album_left_open()
    {
        var library = new Library(new List<Track> { T("One", "First"), T("Two", "Second") });
        var vm = Own(MainViewModelHarness.Build(library, new MainPlaylist(library.Tracks))).Main;

        vm.SelectedSidebarItem = Row(vm, SidebarItemKind.Songs);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedSidebarItem = Row(vm, SidebarItemKind.Albums);
        await vm.RebuildRowsImmediatelyAsync();
        Dispatcher.UIThread.RunJobs();
        vm.ToggleAlbumExpandedCommand!.Execute(vm.AlbumGridTiles.Single(t => t.Name == "Second"));
        vm.SelectedSidebarItem = Row(vm, SidebarItemKind.Home);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.CanGoBack);
        Assert.False(vm.CanGoForward);

        await vm.GoBackAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(SidebarItemKind.Albums, vm.SelectedSidebarItem?.Kind);
        Assert.Equal("Second", vm.ExpandedAlbumName);

        await vm.GoBackAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(SidebarItemKind.Songs, vm.SelectedSidebarItem?.Kind);
        Assert.True(vm.CanGoForward);

        await vm.GoForwardAsync();
        Dispatcher.UIThread.RunJobs();
        await vm.GoForwardAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(SidebarItemKind.Home, vm.SelectedSidebarItem?.Kind);
        Assert.False(vm.CanGoForward);
    }
}
