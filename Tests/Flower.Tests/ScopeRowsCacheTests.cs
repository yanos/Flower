using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

namespace Flower.Tests;

// LibraryBrowserViewModel keeps each scope's rows (Songs, an album, an artist)
// rather than rebuilding Main.Rows from scratch every time the scope changes.
// Going back to Songs from an album used to re-sort the whole library and
// allocate a row per track before mobile's screen could even start to slide.
[Collection("PlatformDataDirectory")]
public class ScopeRowsCacheTests : PinnedDataDirectory
{
    private static Track T(string title, string album) =>
        new() { Title = title, Album = album, Path = $"/music/{title}.mp3", Duration = TimeSpan.FromMinutes(3) };

    private MainViewModel MakeViewModel(Library library) =>
        Own(MainViewModelHarness.Build(library, new MainPlaylist(library.Tracks))).Main;

    private static SidebarItem Item(MainViewModel vm, SidebarItemKind kind) =>
        vm.SidebarItems.Single(i => i.Kind == kind);

    [AvaloniaFact]
    public async Task Returning_to_Songs_hands_back_its_own_rows_before_anything_is_awaited()
    {
        var library = new Library(new List<Track> { T("A", "X"), T("B", "X"), T("C", "Y") });
        var vm = MakeViewModel(library);

        vm.SelectedSidebarItem = Item(vm, SidebarItemKind.Songs);
        await vm.RebuildRowsImmediatelyAsync();
        var songsRows = vm.Rows.ToList();
        Assert.Equal(3, songsRows.Count);

        vm.SelectedSidebarItem = Item(vm, SidebarItemKind.Albums);
        vm.SelectedSubItem = "X";
        await vm.RebuildRowsImmediatelyAsync();
        Assert.Equal(2, vm.Rows.Count);

        // No await: the sidebar change alone puts the same rows back.
        vm.SelectedSidebarItem = Item(vm, SidebarItemKind.Songs);

        Assert.Equal(songsRows.Count, vm.Rows.Count);
        Assert.All(songsRows.Zip(vm.Rows), pair => Assert.Same(pair.First, pair.Second));
        Assert.False(vm.Browser.IsRowsRebuildPending);
    }

    // Reusing a row re-points its album grouping at the new plan, so a row
    // shared between Songs and an album would come back to Songs grouped the
    // album's way.
    [AvaloniaFact]
    public async Task An_album_never_borrows_the_rows_Songs_is_keeping()
    {
        var library = new Library(new List<Track> { T("A", "X"), T("B", "X"), T("C", "Y") });
        var vm = MakeViewModel(library);

        vm.SelectedSidebarItem = Item(vm, SidebarItemKind.Songs);
        await vm.RebuildRowsImmediatelyAsync();
        var songsRows = vm.Rows.ToList();

        vm.SelectedSidebarItem = Item(vm, SidebarItemKind.Albums);
        vm.SelectedSubItem = "X";
        await vm.RebuildRowsImmediatelyAsync();

        Assert.DoesNotContain(vm.Rows, row => songsRows.Any(songsRow => ReferenceEquals(songsRow, row)));
    }

    // A play changes a track in place and raises TrackStatsChanged, not
    // TracksUpdated, so the track list the cache compares by reference never
    // moves. Library.ChangeToken does.
    [AvaloniaFact]
    public async Task A_play_while_away_reorders_a_scope_sorted_by_play_count()
    {
        var a = T("A", "X");
        var b = T("B", "X");
        var c = T("C", "Y");
        var library = new Library(new List<Track> { a, b, c });
        var vm = MakeViewModel(library);

        vm.SelectedSidebarItem = Item(vm, SidebarItemKind.Songs);
        vm.Browser.SortByColumn("PlayCount");
        vm.Browser.SortByColumn("PlayCount");
        await vm.RebuildRowsImmediatelyAsync();
        Assert.False(vm.Browser.SortAscending);

        vm.SelectedSidebarItem = Item(vm, SidebarItemKind.Albums);
        vm.SelectedSubItem = "X";
        await vm.RebuildRowsImmediatelyAsync();

        library.IncrementPlayCount(c);

        vm.SelectedSidebarItem = Item(vm, SidebarItemKind.Songs);
        await vm.RebuildRowsImmediatelyAsync();

        Assert.Same(c, vm.Rows[0].Track);
    }

    [AvaloniaFact]
    public async Task A_scope_left_before_the_library_changed_is_rebuilt_on_return()
    {
        var library = new Library(new List<Track> { T("A", "X"), T("B", "X"), T("C", "Y") });
        var vm = MakeViewModel(library);

        vm.SelectedSidebarItem = Item(vm, SidebarItemKind.Songs);
        await vm.RebuildRowsImmediatelyAsync();

        vm.SelectedSidebarItem = Item(vm, SidebarItemKind.Albums);
        vm.SelectedSubItem = "X";
        await vm.RebuildRowsImmediatelyAsync();

        library.UpdateTracks(library.Tracks.Append(T("D", "Y")).ToList());
        Dispatcher.UIThread.RunJobs();

        vm.SelectedSidebarItem = Item(vm, SidebarItemKind.Songs);
        await vm.RebuildRowsImmediatelyAsync();

        Assert.Equal(4, vm.Rows.Count);
    }
}
