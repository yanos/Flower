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

// What MainViewModel makes of Library.TrackChanged: a rebuild for what can
// move a row, a refresh of the rows showing the tracks for everything else.
[Collection("PlatformDataDirectory")]
public class TrackChangedViewTests : PinnedDataDirectory
{
    private static Track T(string title, string album) =>
        new() { Title = title, Album = album, Path = $"/music/{title}.mp3", Duration = TimeSpan.FromMinutes(3) };

    private MainViewModel MakeViewModel(Library library) =>
        Own(MainViewModelHarness.Build(library, new MainPlaylist(library.Tracks))).Main;

    private static async Task<MainViewModel> OnSongs(MainViewModel vm)
    {
        vm.SelectedSidebarItem = vm.SidebarItems.Single(i => i.Kind == SidebarItemKind.Songs);
        await vm.RebuildRowsImmediatelyAsync();
        return vm;
    }

    // Stars used to have an event of their own that only smart playlists
    // listened to, so starring a song left its row on screen unstarred.
    [AvaloniaFact]
    public async Task A_star_reaches_the_row_showing_the_track_without_a_rebuild()
    {
        var a = T("A", "X");
        var library = new Library(new List<Track> { a, T("B", "X") });
        var vm = await OnSongs(MakeViewModel(library));
        var rows = vm.Rows.ToList();
        var raised = new List<string?>();
        rows.Single(r => r.Track == a).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        library.SetStarred(StarTarget.Song, a.Id.ToString(), starred: true);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(nameof(TrackRowViewModel.StarredDisplay), raised);
        Assert.Equal(rows, vm.Rows);
        Assert.False(vm.Browser.IsRowsRebuildPending);
    }

    // A tag edit can move a track to another album group or sort position, so
    // the list is rebuilt from the library rather than patched.
    [AvaloniaFact]
    public async Task A_tag_edit_rebuilds_the_list()
    {
        var a = T("A", "X");
        var library = new Library(new List<Track> { a, T("B", "Y") });
        var vm = await OnSongs(MakeViewModel(library));

        a.Album = "Z";
        library.NotifyTrackChanged(a, TrackChange.Tags);
        Dispatcher.UIThread.RunJobs();

        // Asked for by the event itself, where a star above asks for none.
        Assert.True(vm.Browser.IsRowsRebuildPending);

        await vm.RebuildRowsImmediatelyAsync();
        Assert.Equal(new[] { "B", "A" }, vm.Rows.Select(r => r.Track.Title));
    }
}
