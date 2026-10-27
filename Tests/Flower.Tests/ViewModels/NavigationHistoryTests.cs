using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels;
using Flower.ViewModels.Mobile;

using Xunit;

namespace Flower.Tests.ViewModels;

public class NavigationHistoryTests
{
    private static NavigationPlace Page(SidebarItemKind kind, string? artist = null, AlbumTileKey? album = null) =>
        new(kind, null, null, artist, album);

    private static bool Everywhere(NavigationPlace _) => true;

    [Fact]
    public void Back_and_forward_walk_the_pages_visited()
    {
        var history = new NavigationHistory();
        history.Visit(Page(SidebarItemKind.Home));
        history.Visit(Page(SidebarItemKind.Songs));
        history.Visit(Page(SidebarItemKind.Albums));

        Assert.Equal(SidebarItemKind.Songs, history.Back(Everywhere)?.Kind);
        Assert.Equal(SidebarItemKind.Home, history.Back(Everywhere)?.Kind);
        Assert.False(history.CanGoBack);
        Assert.Null(history.Back(Everywhere));
        Assert.Equal(SidebarItemKind.Songs, history.Forward(Everywhere)?.Kind);
        Assert.Equal(SidebarItemKind.Albums, history.Forward(Everywhere)?.Kind);
        Assert.False(history.CanGoForward);
    }

    [Fact]
    public void A_new_page_drops_the_ones_ahead()
    {
        var history = new NavigationHistory();
        history.Visit(Page(SidebarItemKind.Home));
        history.Visit(Page(SidebarItemKind.Songs));
        history.Back(Everywhere);

        history.Visit(Page(SidebarItemKind.Queue));

        Assert.False(history.CanGoForward);
        Assert.Equal(SidebarItemKind.Home, history.Back(Everywhere)?.Kind);
    }

    [Fact]
    public void Another_artist_is_another_page_but_another_album_is_not()
    {
        var history = new NavigationHistory();
        history.Visit(Page(SidebarItemKind.Artists, "A"));
        history.Visit(Page(SidebarItemKind.Artists, "B"));
        history.Visit(Page(SidebarItemKind.Albums));
        history.Visit(Page(SidebarItemKind.Albums, album: new AlbumTileKey("X", null)));

        Assert.Equal(new AlbumTileKey("X", null), history.Current?.Album);
        Assert.Equal("B", history.Back(Everywhere)?.Artist);
        Assert.Equal("A", history.Back(Everywhere)?.Artist);
    }

    [Fact]
    public void A_page_no_longer_there_is_skipped_and_forgotten()
    {
        var history = new NavigationHistory();
        history.Visit(Page(SidebarItemKind.Home));
        history.Visit(Page(SidebarItemKind.Playlist));
        history.Visit(Page(SidebarItemKind.Songs));

        Assert.Equal(SidebarItemKind.Home, history.Back(p => p.Kind != SidebarItemKind.Playlist)?.Kind);
        Assert.Equal(SidebarItemKind.Songs, history.Forward(Everywhere)?.Kind);
        Assert.Equal(SidebarItemKind.Home, history.Back(Everywhere)?.Kind);
    }

    [Fact]
    public void It_keeps_the_last_hundred()
    {
        var history = new NavigationHistory();
        for (var i = 0; i <= NavigationHistory.Capacity; i++)
            history.Visit(Page(SidebarItemKind.Artists, $"{i}"));

        var steps = 0;
        while (history.Back(Everywhere) != null)
            steps++;
        Assert.Equal(NavigationHistory.Capacity - 1, steps);
        Assert.Equal("1", history.Current?.Artist);
    }
}

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
