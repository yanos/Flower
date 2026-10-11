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

    [AvaloniaFact]
    public void A_row_says_how_many_songs_and_how_long_they_run()
    {
        var vm = Build(out _, T("A", 3), T("B", 4), T("C", 5));
        Assert.Equal("3 songs  ·  12:00", TheRow(vm).PlaylistSummary);
    }

    // Written the way a song's own length is, widening to hours the same way
    // (TrackRowViewModel.DurationDisplay, and DurationText under both).
    [AvaloniaFact]
    public void A_long_playlist_counts_in_hours()
    {
        var vm = Build(out _, T("A", 40), T("B", 33));
        Assert.Equal("2 songs  ·  1:13:00", TheRow(vm).PlaylistSummary);
    }

    // The playlist's own screen has the room the row does not, and nothing
    // beside it to be read against, so it says the same total in words.
    [AvaloniaFact]
    public async Task The_playlist_screen_says_how_long_it_runs_in_words()
    {
        var vm = Build(out _, T("A", 40), T("B", 33));
        vm.SelectTabCommand.Execute(nameof(MobileTab.Playlists));
        vm.SelectPlaylistCommand.Execute(TheRow(vm));

        // The playlist lands once its rows have been rebuilt off the UI
        // thread, which one pass of the dispatcher does not wait for - a slow
        // CI runner read the header before there was one. This used to sleep
        // on the dispatcher thread between pumps, which is the one thread the
        // rebuild's continuation needs; see UiWait.
        await UiWait.Until(() => vm.CurrentPlaylistHeader != null, "the playlist screen never got a header");
        Assert.Equal("2 songs  ·  1 hour 13 minutes", vm.CurrentPlaylistHeader?.Artist);
    }

    // A playlist of tracks not downloaded yet has no durations to add up, and
    // says nothing rather than claiming it is empty of time.
    [AvaloniaFact]
    public void A_playlist_with_no_durations_says_only_how_many_songs()
    {
        var vm = Build(out _, T("A", 0));
        Assert.Equal("1 song", TheRow(vm).PlaylistSummary);
    }

    // The row is not rebuilt when a song is added to the playlist behind it,
    // so the count has to be re-read rather than waited for.
    [AvaloniaFact]
    public async Task Adding_a_song_re_counts_the_row()
    {
        var vm = Build(out var library, T("A", 3));
        var row = TheRow(vm);
        var changed = 0;
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SidebarItem.PlaylistSummary))
                changed++;
        };

        var extra = T("B", 7);
        library.UpdateTracks(library.Tracks.Append(extra).ToList());
        await vm.Main.AddTrackToPlaylist(extra, row.Playlist!);
        Dispatcher.UIThread.RunJobs();

        Assert.True(changed > 0, "the row was never told to re-count");
        Assert.Equal("2 songs  ·  10:00", row.PlaylistSummary);
    }

    [AvaloniaFact]
    public void The_menu_is_the_album_menu_over_this_playlist()
    {
        var vm = Build(out _, T("A", 3), T("B", 4));
        vm.OpenPlaylistActionsCommand.Execute(TheRow(vm));

        Assert.Equal(MobileSheet.AlbumActions, vm.ActiveSheet);
        Assert.True(vm.IsActingOnAPlaylist);
        Assert.Equal("Road trip", vm.AlbumActionTarget?.Name);
        Assert.Equal("2 songs  ·  7 minutes", vm.AlbumActionTarget?.Artist);
        // The playlist's songs, so Play/Shuffle/Add to Playlist act on them.
        Assert.Equal(new[] { "A", "B" }, vm.AlbumActionTarget!.Tracks.Select(t => t.Title));
    }

    // The same sheet opened over an album must not offer to rename or delete
    // a playlist - that is what gates those two entries.
    [AvaloniaFact]
    public void An_albums_menu_is_not_a_playlists_menu()
    {
        var vm = Build(out _, T("A", 3));
        vm.OpenPlaylistActionsCommand.Execute(TheRow(vm));
        Assert.True(vm.IsActingOnAPlaylist);

        vm.CloseSheetCommand.Execute(null);
        vm.OpenArtistActionsCommand.Execute("Guided by Voices");

        Assert.Equal(MobileSheet.AlbumActions, vm.ActiveSheet);
        Assert.False(vm.IsActingOnAPlaylist);
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

        Assert.Equal("Delete \"Road trip\"?", vm.ConfirmDeletePlaylistTitle);

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

    // Dismissing the confirmation any other way is still an answer, and the
    // delete waiting on it would otherwise never come back at all.
    [AvaloniaFact]
    public async Task Dismissing_the_confirmation_is_a_no()
    {
        var vm = Build(out var library, T("A", 3));
        vm.OpenPlaylistActionsCommand.Execute(TheRow(vm));
        vm.DeletePlaylistActionTargetCommand.Execute(null);
        await WaitFor(() => vm.ActiveSheet == MobileSheet.ConfirmDeletePlaylist);

        vm.CloseSheetCommand.Execute(null);
        await WaitFor(() => vm.ActiveSheet == MobileSheet.None);

        Assert.Single(library.Playlists);
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
