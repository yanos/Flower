using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;

using Flower.Models;
using Flower.Services;
using Flower.ViewModels;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Flower.Tests.ViewModels;

// One of the collaborators split out of MainViewModel (docs/ARCHITECTURE-
// REVIEW.md Tier 4.2), constructed directly rather than through that class's
// constructor and the whole service graph behind it.
public class PlaylistManagementViewModelTests
{
    private sealed class Host : IPlaylistManagementHost
    {
        public SidebarItem? SelectedSidebarItem { get; set; }
        public SidebarItem? DefaultSelection { get; set; }
        public IReadOnlyList<Playlist> SelectedPlaylists { get; set; } = new List<Playlist>();
        public int ContentChangedCount;
        public int ScheduledSyncs;
        public void PlaylistContentChanged() => ContentChangedCount++;
        public void ScheduleContentSync() => ScheduledSyncs++;
    }

    private static Track Song(string title) => new() { Title = title, Path = $"/music/{title}.mp3" };

    private static (PlaylistManagementViewModel Vm, Library Library, ObservableCollection<SidebarItem> Items, Host Host) Make()
    {
        var library = new Library(new List<Track>());
        var items = new ObservableCollection<SidebarItem>
        {
            new(SidebarItemKind.Header, "Library"),
            new(SidebarItemKind.Songs, "Songs"),
        };
        var host = new Host { DefaultSelection = items[1] };
        return (new PlaylistManagementViewModel(library, items, host), library, items, host);
    }

    [Fact]
    public async Task Creating_a_playlist_adds_a_header_a_row_and_schedules_a_sync()
    {
        var (vm, library, items, host) = Make();

        await vm.CreateWithTracks(new[] { Song("a"), Song("b") });

        Assert.Single(library.Playlists);
        Assert.Equal(2, library.Playlists[0].Tracks.Count);

        var header = items.Single(i => i.Kind == SidebarItemKind.Header && i.Name == "Playlists");
        var row = items.Single(i => i.Kind == SidebarItemKind.Playlist);
        Assert.True(items.IndexOf(header) < items.IndexOf(row));
        // A fresh playlist opens straight into its inline rename box.
        Assert.True(row.IsEditing);
        Assert.Same(row, host.SelectedSidebarItem);
        Assert.Equal(1, host.ScheduledSyncs);
    }

    // A song dropped on the sidebar's Playlists space with a server paired:
    // the new row went to the very end of the sidebar, under the server.
    [Fact]
    public async Task A_new_playlist_goes_in_the_playlists_section_not_under_the_server()
    {
        var (vm, _, items, _) = Make();
        items.Add(new SidebarItem(SidebarItemKind.Header, "Servers"));
        items.Add(new SidebarItem(SidebarItemKind.Device, "lambda"));

        await vm.CreateWithTracks(new[] { Song("a") });
        await vm.CreateWithTracks(new[] { Song("b") });

        Assert.Equal(
            new[] { "Library", "Songs", "Playlists", "New Playlist", "New Playlist", "Servers", "lambda" },
            items.Select(i => i.Name));
    }

    // The first playlist arriving by sync rather than by hand.
    [Fact]
    public void A_synced_first_playlist_goes_in_the_playlists_section_not_under_the_server()
    {
        var (vm, library, items, _) = Make();
        items.Add(new SidebarItem(SidebarItemKind.Header, "Servers"));
        items.Add(new SidebarItem(SidebarItemKind.Device, "lambda"));
        library.AddPlaylist(new Playlist("Road Trip", new List<Track>()));

        vm.RefreshSidebarItems();

        Assert.Equal(
            new[] { "Library", "Songs", "Playlists", "Road Trip", "Servers", "lambda" },
            items.Select(i => i.Name));
    }

    [Fact]
    public async Task Deleting_the_selected_playlist_falls_back_to_the_default_selection()
    {
        var (vm, library, items, host) = Make();
        await vm.CreateWithTracks(new[] { Song("a") });
        var playlist = library.Playlists[0];
        host.SelectedSidebarItem = items.Single(i => i.Kind == SidebarItemKind.Playlist);
        // The row is still mid-rename from creation; a deleted playlist's row
        // must go regardless.
        host.SelectedSidebarItem.IsEditing = false;

        await vm.DeleteAsync(playlist);

        Assert.Empty(library.Playlists);
        Assert.DoesNotContain(items, i => i.Kind == SidebarItemKind.Playlist);
        Assert.DoesNotContain(items, i => i.Kind == SidebarItemKind.Header && i.Name == "Playlists");
        Assert.Same(host.DefaultSelection, host.SelectedSidebarItem);
    }

    [Fact]
    public async Task Deleting_several_playlists_asks_once_and_removes_them_all()
    {
        var (vm, library, items, host) = Make();
        await vm.CreateWithTracks(new[] { Song("a") });
        await vm.CreateWithTracks(new[] { Song("b") });
        await vm.CreateWithTracks(new[] { Song("c") });
        foreach (var row in items)
            row.IsEditing = false;
        var keep = library.Playlists[2];
        var doomed = library.Playlists.Take(2).ToList();
        host.SelectedSidebarItem = items.Single(i => i.Playlist == doomed[0]);
        var confirmations = new List<IReadOnlyList<Playlist>>();
        vm.DeleteConfirmationRequested += (_, e) =>
        {
            confirmations.Add(e.Playlists);
            e.Confirmed.SetResult(true);
        };
        var syncsBefore = host.ScheduledSyncs;

        await vm.DeleteAsync(doomed);

        Assert.Equal(doomed, Assert.Single(confirmations));
        Assert.Same(keep, Assert.Single(library.Playlists));
        Assert.Same(keep, items.Single(i => i.Kind == SidebarItemKind.Playlist).Playlist);
        Assert.Same(host.DefaultSelection, host.SelectedSidebarItem);
        Assert.Equal(syncsBefore + 1, host.ScheduledSyncs);
    }

    // The Playlist menu's Delete, with several playlists selected in the
    // sidebar: all of them go, not only the one being shown.
    [Fact]
    public async Task Deleting_the_selection_deletes_every_selected_playlist()
    {
        var (vm, library, items, host) = Make();
        await vm.CreateWithTracks(new[] { Song("a") });
        await vm.CreateWithTracks(new[] { Song("b") });
        await vm.CreateWithTracks(new[] { Song("c") });
        foreach (var row in items)
            row.IsEditing = false;
        var keep = library.Playlists[1];
        host.SelectedSidebarItem = items.Single(i => i.Playlist == library.Playlists[0]);
        host.SelectedPlaylists = new[] { library.Playlists[0], library.Playlists[2] };

        await vm.DeleteSelectedAsync();

        Assert.Same(keep, Assert.Single(library.Playlists));
    }

    [Fact]
    public async Task Deletion_is_abandoned_when_the_confirmation_is_declined()
    {
        var (vm, library, _, host) = Make();
        await vm.CreateWithTracks(new[] { Song("a") });
        vm.DeleteConfirmationRequested += (_, e) => e.Confirmed.SetResult(false);
        var syncsBefore = host.ScheduledSyncs;

        await vm.DeleteAsync(library.Playlists[0]);

        Assert.Single(library.Playlists);
        Assert.Equal(syncsBefore, host.ScheduledSyncs);
    }

    [Fact]
    public async Task A_refresh_leaves_a_row_being_renamed_alone()
    {
        var (vm, library, items, host) = Make();
        await vm.CreateWithTracks(new[] { Song("a") });
        var editingRow = items.Single(i => i.Kind == SidebarItemKind.Playlist);
        Assert.True(editingRow.IsEditing);

        // What a background PlaylistsUpdated (a device sync landing mid-edit)
        // triggers - rebuilding this row would yank focus out of its TextBox
        // and look like the rename cancelled itself.
        vm.RefreshSidebarItems();

        Assert.Same(editingRow, items.Single(i => i.Kind == SidebarItemKind.Playlist));
    }

    [Fact]
    public async Task Reordering_a_playlist_that_is_not_on_screen_still_syncs_but_does_not_rebuild_rows()
    {
        var (vm, library, _, host) = Make();
        var a = Song("a");
        var b = Song("b");
        await vm.CreateWithTracks(new[] { a, b });
        var playlist = library.Playlists[0];
        host.SelectedSidebarItem = null;
        var syncsBefore = host.ScheduledSyncs;

        await vm.ReorderTrack(playlist, b, a);

        Assert.Equal("b", playlist.Tracks[0].Title);
        Assert.Equal(syncsBefore + 1, host.ScheduledSyncs);
        Assert.Equal(0, host.ContentChangedCount);
    }

    [Fact]
    public async Task Reordering_a_track_the_playlist_does_not_contain_neither_syncs_nor_rebuilds()
    {
        var (vm, library, _, host) = Make();
        await vm.CreateWithTracks(new[] { Song("a") });
        var playlist = library.Playlists[0];
        var syncsBefore = host.ScheduledSyncs;

        // MoveTrack returns false for a drag that reorders nothing - see
        // PlaylistTests for the full set of those - and this ViewModel must
        // then neither rebuild rows nor schedule a peer sync.
        await vm.ReorderTrack(playlist, Song("stranger"), null);

        Assert.Equal(syncsBefore, host.ScheduledSyncs);
        Assert.Equal(0, host.ContentChangedCount);
    }
}
