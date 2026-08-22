using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;

using Flower.Models;
using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests;

// Turning the iTunes/Music.app integration off drops Music.app's media folder
// from the library paths (SettingsViewModel.ApplyAppleMusicFolder) and rescans,
// which for a library that was *only* that folder means a scan that finds
// nothing - so every one of these has to follow the library down to empty
// rather than keeping whatever it last showed. Written while chasing "I
// unselected the iTunes option and my library was still on screen": the
// ViewModel layer does clear, which is what this pins.
[Collection("PlatformDataDirectory")]
public class LibraryEmptiedUiTests : PinnedDataDirectory
{
    private static Track T(string title) =>
        new() { Title = title, Path = $"/music/{title}.mp3", Album = "Alb", Artists = "Art", Duration = TimeSpan.FromMinutes(3) };

    [AvaloniaFact]
    public async Task Emptying_the_library_clears_the_rows()
    {
        var library = new Library(new List<Track> { T("A"), T("B"), T("C") });
        var mainPlaylist = new MainPlaylist(library.Tracks);
        var vm = Own(MainViewModelHarness.Build(library, mainPlaylist)).Main;

        vm.SelectedSidebarItem = vm.SidebarItems.Single(i => i.Kind == SidebarItemKind.Songs);
        await vm.RebuildRowsImmediatelyAsync();
        Assert.Equal(3, vm.Rows.Count);

        library.UpdateTracks(new List<Track>());
        for (var i = 0; i < 20 && vm.Rows.Count > 0; i++)
            await Task.Delay(50);

        Assert.Empty(vm.Rows);
        Assert.Empty(vm.DisplayedTracks);
    }

    [AvaloniaFact]
    public async Task Emptying_the_library_clears_the_recently_added_grid()
    {
        var library = new Library(new List<Track> { T("A"), T("B"), T("C") });
        var mainPlaylist = new MainPlaylist(library.Tracks);
        var vm = Own(MainViewModelHarness.Build(library, mainPlaylist)).Main;

        vm.SelectedSidebarItem = vm.SidebarItems.Single(i => i.Kind == SidebarItemKind.RecentlyAdded);
        await vm.RebuildRowsImmediatelyAsync();
        Assert.NotEmpty(vm.RecentlyAddedGridTiles);

        library.UpdateTracks(new List<Track>());
        for (var i = 0; i < 20 && vm.RecentlyAddedGridTiles.Count > 0; i++)
            await Task.Delay(50);

        Assert.Empty(vm.RecentlyAddedGridTiles);
        Assert.Empty(vm.AlbumGridTiles);
    }

    // The same thing again, but driven the way the app drives it: through the
    // real settings screen and LocalSettingsBackend, so the save, the path
    // write and the unawaited rescan all happen the way Remove Folder + OK
    // makes them happen - not by calling UpdateTracks directly.
    [AvaloniaFact]
    public async Task Removing_the_last_folder_through_settings_empties_the_view()
    {
        var settings = new AppSettings { LibraryPaths = { "/music" } };
        var library = new Library(new List<Track> { T("A"), T("B"), T("C") });
        var mainPlaylist = new MainPlaylist(library.Tracks);
        var vm = Own(MainViewModelHarness.Build(library, mainPlaylist, settings)).Main;

        vm.SelectedSidebarItem = vm.SidebarItems.Single(i => i.Kind == SidebarItemKind.Songs);
        await vm.RebuildRowsImmediatelyAsync();
        Assert.Equal(3, vm.Rows.Count);

        var panel = new SettingsViewModel(new LocalSettingsBackend(vm));
        await panel.LoadAsync();
        Assert.Single(panel.LibraryPaths);
        panel.RemoveLibraryPathCommand.Execute(panel.LibraryPaths[0]);
        await panel.SaveAsync();

        for (var i = 0; i < 20 && vm.Rows.Count > 0; i++)
            await Task.Delay(50);

        Assert.Empty(library.Tracks);
        Assert.Empty(vm.DisplayedTracks);
        Assert.Empty(vm.Rows);
    }
}
