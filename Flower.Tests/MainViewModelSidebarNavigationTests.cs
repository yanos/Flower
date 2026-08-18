using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;


using Flower.Models;
using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

namespace Flower.Tests;

// Regression coverage for "switching sidebar views flashes the previous
// view's tracks before the new ones appear" - a plain sidebar click
// (Songs/Albums/Artists/a playlist) used to go through the same 250ms
// ScheduleFilter debounce as typing into the search box, so the old view's
// Rows stayed on screen for that whole delay after the new view was already
// visible. MainViewModel.ApplySubItemSelection's immediate:true path (used
// only by OnSidebarSelectionChanged) bypasses that debounce the same way
// RebuildRowsImmediatelyAsync already does for mobile's drill-in navigation.
//
// Isolated under PlatformDataDirectory the same way PlaylistPlaybackIntegrationTests/
// LibraryDownloadServiceTests are - MainViewModel's constructor wires up
// LibraryStore/AppSettingsStore, which resolve their on-disk path from it.
[Collection("PlatformDataDirectory")]
public class MainViewModelSidebarNavigationTests : IDisposable
{
    private readonly string _tempHome;

    public MainViewModelSidebarNavigationTests()
    {
        _tempHome = Path.Combine(Path.GetTempPath(), "flower-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempHome);
        PlatformDataDirectory.Current = _tempHome;
    }

    public void Dispose()
    {
        PlatformDataDirectory.Current = null;
        try { Directory.Delete(_tempHome, recursive: true); } catch { /* best effort */ }
    }

    private static Track T(string title) =>
        new() { Title = title, Path = $"/music/{title}.mp3", Duration = TimeSpan.FromMinutes(3) };

    // The full-MainViewModel wiring lives in TestSupport/MainViewModelHarness
    // now that ScreenStackPanelSwipeTests needs the same thing.
    private static MainViewModel MakeViewModel(Library library, MainPlaylist mainPlaylist) =>
        MainViewModelHarness.Build(library, mainPlaylist);

    [AvaloniaFact]
    public async Task Switching_sidebar_view_updates_Rows_well_under_the_search_debounce()
    {
        var trackA = T("A");
        var trackB = T("B");
        var trackC = T("C");
        var library = new Library(new List<Track> { trackA, trackB, trackC });
        library.AddPlaylist(new Playlist("Just A", new List<Track> { trackA }));
        var mainPlaylist = new MainPlaylist(library.Tracks);

        var vm = MakeViewModel(library, mainPlaylist);

        // Land on the playlist first and let its own rebuild fully settle
        // before timing the switch under test.
        var playlistItem = vm.SidebarItems.Single(i => i.Kind == SidebarItemKind.Playlist);
        vm.SelectedSidebarItem = playlistItem;
        await vm.RebuildRowsImmediatelyAsync();
        Assert.Single(vm.Rows);

        // Switching to Songs used to go through ScheduleFilter's 250ms
        // debounce like a search-box keystroke, so Rows would still show the
        // playlist's one track this soon after switching. Awaiting well
        // under that (50ms) proves the sidebar-switch path no longer waits
        // for it.
        var songsItem = vm.SidebarItems.Single(i => i.Kind == SidebarItemKind.Songs);
        vm.SelectedSidebarItem = songsItem;
        await Task.Delay(50);

        Assert.Equal(3, vm.Rows.Count);
    }
}
