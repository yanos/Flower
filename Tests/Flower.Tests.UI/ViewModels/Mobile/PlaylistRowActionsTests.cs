using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Flower.Controls;
using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels;
using Flower.ViewModels.Mobile;
using Flower.Views.Mobile;

using Material.Icons;
using Material.Icons.Avalonia;

using Xunit;

namespace Flower.Tests.ViewModels.Mobile;

// A playlist row in the Playlists tab says what is in it and carries its own
// menu, the way a song row does. The menu is the album menu (AlbumActionsView)
// over a playlist rather than a second sheet saying the same things, plus the
// two entries only a playlist has: renaming it and deleting it.
[Collection("PlatformDataDirectory")]
public class PlaylistRowActionsTests : PinnedDataDirectory
{
    public PlaylistRowActionsTests() => TestIoc.EnsureConfigured();

    private static Track T(string title, int minutes) => new()
    {
        Title = title,
        Album = "Bee Thousand",
        Artists = "Guided by Voices",
        Path = "/music/" + title + ".flac",
        Duration = TimeSpan.FromMinutes(minutes),
        DateAdded = DateTimeOffset.UtcNow,
    };

    private MobileMainViewModel Build(out Library library, params Track[] tracks)
    {
        library = new Library(tracks.ToList());
        library.AddPlaylist(new Playlist("Road trip", tracks.ToList()));
        return Own(MainViewModelHarness.BuildMobile(library, new MainPlaylist(new List<Track>()))).Mobile;
    }

    private SidebarItem TheRow(MobileMainViewModel vm) =>
        vm.PlaylistPickerItems.Single(i => i.Playlist?.Name == "Road trip");

    // Like a song row's, the row's download icon is there while something in
    // the playlist is still only on the server - and a song added from there
    // brings it, though the row itself is never rebuilt.
    [AvaloniaFact]
    public async Task A_row_offers_a_download_once_it_holds_a_song_still_on_the_server()
    {
        var vm = Build(out var library, T("A", 3));
        // The one way a test has to make a server reachable: the tab's own.
        vm.Main.BrowserOriginFingerprint = "server-fp";
        var row = TheRow(vm);
        Assert.False(row.PlaylistDownload.IsDownloadable);

        var remote = new Track { Title = "B", Album = "Bee Thousand", OriginDeviceFingerprint = "server-fp" };
        library.UpdateTracks(library.Tracks.Append(remote).ToList());
        await vm.Main.AddTrackToPlaylist(remote, row.Playlist!);
        Dispatcher.UIThread.RunJobs();

        Assert.True(row.PlaylistDownload.IsDownloadable);
    }

    [AvaloniaFact]
    public async Task Rename_opens_the_row_s_own_box_and_commits_what_is_typed()
    {
        var vm = Build(out _, T("A", 3));
        var row = TheRow(vm);

        vm.OpenPlaylistActionsCommand.Execute(row);
        vm.RenamePlaylistActionTargetCommand.Execute(null);

        // The menu closes and the row turns into a box.
        Assert.Equal(MobileSheet.None, vm.ActiveSheet);
        Assert.True(row.IsEditing);

        row.Name = "Long way home";
        vm.CommitPlaylistRenameCommand.Execute(row);
        await WaitFor(() => !row.IsEditing);

        Assert.False(row.IsEditing);
        Assert.Equal("Long way home", row.Playlist!.Name);
    }

    [AvaloniaFact]
    public async Task Deleting_asks_first_and_keeps_the_playlist_if_the_answer_is_no()
    {
        var vm = Build(out var library, T("A", 3));
        vm.OpenPlaylistActionsCommand.Execute(TheRow(vm));
        vm.DeletePlaylistActionTargetCommand.Execute(null);
        await WaitFor(() => vm.ActiveSheet == MobileSheet.ConfirmDeletePlaylist);

        Assert.Equal(MobileSheet.ConfirmDeletePlaylist, vm.ActiveSheet);

        vm.CancelDeletePlaylistCommand.Execute(null);
        await WaitFor(() => vm.ActiveSheet == MobileSheet.None);

        Assert.Single(library.Playlists);
    }

    [AvaloniaFact]
    public async Task Deleting_removes_the_playlist_once_it_is_confirmed()
    {
        var vm = Build(out var library, T("A", 3));
        vm.OpenPlaylistActionsCommand.Execute(TheRow(vm));
        vm.DeletePlaylistActionTargetCommand.Execute(null);
        await WaitFor(() => vm.ActiveSheet == MobileSheet.ConfirmDeletePlaylist);

        vm.ConfirmDeletePlaylistCommand.Execute(null);
        await WaitFor(() => library.Playlists.Count == 0);

        Assert.Empty(library.Playlists);
        Assert.Empty(vm.PlaylistPickerItems);
        // The songs are the library's, not the playlist's, and stay put.
        Assert.Single(library.Tracks);
    }

    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 2000)
    {
        for (var waited = 0; waited < timeoutMs && !condition(); waited += 20)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Dispatcher.UIThread.RunJobs();
    }
}
