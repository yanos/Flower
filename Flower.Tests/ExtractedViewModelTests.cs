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

namespace Flower.Tests;

// docs/ARCHITECTURE-REVIEW.md Tier 4.2: the collaborators split out of
// MainViewModel, each constructed directly here rather than through that
// class's 20-parameter constructor and the whole service graph behind it.
// Being able to write these at all is the point of the decomposition - the
// pre-split equivalents either had to stand up MainViewModelHarness or could
// not be written.
public class BusyStateTests
{
    [AvaloniaFact]
    public void Nested_scopes_keep_the_indicator_up_until_the_outermost_closes()
    {
        var busy = new BusyState();
        var changes = 0;
        busy.Changed += (_, _) => changes++;

        Assert.False(busy.IsBusy);

        var outer = busy.BeginScope("outer");
        Assert.True(busy.IsBusy);
        Assert.Equal("outer", busy.Message);

        var inner = busy.BeginScope("inner");
        // The innermost scope wins the message while both are open.
        Assert.Equal("inner", busy.Message);

        inner.Dispose();
        // Still busy - and deliberately still showing the inner message, since
        // nothing re-states the outer one on the way out.
        Assert.True(busy.IsBusy);

        outer.Dispose();
        Assert.False(busy.IsBusy);
        Assert.Null(busy.Message);
        // Two opens and the final close - closing the inner scope changes
        // neither IsBusy nor Message, so it raises nothing.
        Assert.Equal(3, changes);
    }

    [AvaloniaFact]
    public void A_scope_opened_off_the_ui_thread_still_reports_busy_immediately()
    {
        var busy = new BusyState();

        // The count is bumped synchronously regardless of thread - only the
        // notification is marshalled. This is the invariant that made
        // overlapping scopes from App.axaml.cs's background rescan work.
        Task.Run(() => busy.BeginScope("background")).Wait();

        Assert.True(busy.IsBusy);
    }
}

public class PlaylistManagementViewModelTests
{
    private sealed class Host : IPlaylistManagementHost
    {
        public SidebarItem? SelectedSidebarItem { get; set; }
        public SidebarItem? DefaultSelection { get; set; }
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

public class LibraryBrowserViewModelTests
{
    private sealed class Host : ILibraryBrowseHost
    {
        public SidebarItemKind? CurrentKind { get; set; } = SidebarItemKind.Songs;
        public Playlist? CurrentPlaylist { get; set; }
        public Track? CurrentlyPlayingTrack { get; set; }
        public string? PairedServerFingerprint { get; set; }
        public bool IsPairedServerReachable { get; set; }
        public (string Column, bool Ascending)? PersistedSort;
        public bool? PersistedArtistAlbumsByYear;
        public void PersistSort(string column, bool ascending) => PersistedSort = (column, ascending);
        public void PersistSortArtistAlbumsByYear(bool value) => PersistedArtistAlbumsByYear = value;
    }

    private static Track Song(string title, string album, string artist, DateTimeOffset? lastPlayed = null) =>
        new()
        {
            Title = title, Album = album, Artists = artist,
            Path = $"/music/{artist}/{album}/{title}.mp3",
            LastPlayedAt = lastPlayed,
        };

    private static (LibraryBrowserViewModel Vm, Host Host) Make(params Track[] tracks)
    {
        var host = new Host();
        return (new LibraryBrowserViewModel(new Library(tracks.ToList()), host, NullLogger<LibraryBrowserViewModel>.Instance), host);
    }

    [AvaloniaFact]
    public async Task Songs_shows_every_track_and_summarises_them_in_the_status_bar()
    {
        var (vm, _) = Make(Song("a", "One", "X"), Song("b", "One", "X"), Song("c", "Two", "Y"));

        vm.Repopulate();
        await vm.RebuildRowsImmediatelyAsync();

        Assert.Equal(3, vm.Rows.Count);
        Assert.Contains("3 songs", vm.StatusBarText);
        Assert.Contains("2 albums", vm.StatusBarText);
    }

    [AvaloniaFact]
    public async Task Artists_shows_nothing_until_an_artist_is_picked_then_only_that_artists_tracks()
    {
        var (vm, host) = Make(Song("a", "One", "X"), Song("b", "Two", "Y"));
        host.CurrentKind = SidebarItemKind.Artists;
        vm.Repopulate();

        await vm.RebuildRowsImmediatelyAsync();
        // An unscoped Artists view is deliberately empty rather than the whole
        // library - see GetBaseTracksForFilter.
        Assert.Empty(vm.Rows);

        vm.SetSelectedSubItems(new[] { "X" });
        await vm.RebuildRowsImmediatelyAsync();

        Assert.Equal("a", Assert.Single(vm.Rows).Track.Title);
    }

    [AvaloniaFact]
    public async Task History_excludes_tracks_that_have_never_been_played()
    {
        var (vm, host) = Make(
            Song("played", "One", "X", DateTimeOffset.UtcNow),
            Song("never", "One", "X"));
        host.CurrentKind = SidebarItemKind.History;
        vm.Repopulate();

        await vm.RebuildRowsImmediatelyAsync();

        Assert.Equal("played", Assert.Single(vm.Rows).Track.Title);
    }

    [AvaloniaFact]
    public async Task The_search_filter_narrows_the_rows()
    {
        var (vm, _) = Make(Song("hello", "One", "X"), Song("goodbye", "One", "X"));
        vm.Repopulate();

        vm.FilterText = "hello";
        await vm.RebuildRowsImmediatelyAsync();

        Assert.Equal("hello", Assert.Single(vm.Rows).Track.Title);
    }

    [AvaloniaFact]
    public void Recently_added_and_history_keep_sort_state_independent_of_songs()
    {
        var (vm, host) = Make(Song("a", "One", "X"));

        Assert.Equal("TrackNumber", vm.SortColumn);

        host.CurrentKind = SidebarItemKind.RecentlyAdded;
        Assert.Equal("DateAdded", vm.SortColumn);
        Assert.False(vm.SortAscending);

        vm.SortByColumn("Title");
        Assert.Equal("Title", vm.SortColumn);
        // Only Songs' sort is persisted - Recently Added's is per-session.
        Assert.Null(host.PersistedSort);

        host.CurrentKind = SidebarItemKind.Songs;
        Assert.Equal("TrackNumber", vm.SortColumn);

        vm.SortByColumn("Album");
        Assert.Equal(("Album", true), host.PersistedSort);

        host.CurrentKind = SidebarItemKind.History;
        Assert.Equal("LastPlayed", vm.SortColumn);
    }

    [AvaloniaFact]
    public void Clicking_the_same_column_twice_reverses_it()
    {
        var (vm, _) = Make(Song("a", "One", "X"));

        vm.SortByColumn("Title");
        Assert.True(vm.SortAscending);

        vm.SortByColumn("Title");
        Assert.False(vm.SortAscending);

        // A different column always starts ascending again.
        vm.SortByColumn("Album");
        Assert.True(vm.SortAscending);
    }

    [AvaloniaFact]
    public void Expanding_an_album_is_an_accordion_and_orders_by_disc_then_track()
    {
        var (vm, _) = Make(
            new Track { Title = "second", Album = "One", DiscNumber = 1, TrackNumber = 2, Path = "/2.mp3" },
            new Track { Title = "first",  Album = "One", DiscNumber = 1, TrackNumber = 1, Path = "/1.mp3" },
            new Track { Title = "disc2",  Album = "One", DiscNumber = 2, TrackNumber = 1, Path = "/3.mp3" },
            new Track { Title = "other",  Album = "Two", DiscNumber = 1, TrackNumber = 1, Path = "/4.mp3" });
        vm.Repopulate();

        var one = vm.AlbumGridTiles.Single(t => t.Name == "One");
        var two = vm.AlbumGridTiles.Single(t => t.Name == "Two");

        vm.ToggleAlbumExpanded(one);
        Assert.Equal("One", vm.ExpandedAlbumName);
        Assert.Equal(new[] { "first", "second", "disc2" }, vm.ExpandedAlbumTracks.Select(t => t.Title));

        // Clicking the already-expanded album collapses it.
        vm.ToggleAlbumExpanded(one);
        Assert.Null(vm.ExpandedAlbumName);
        Assert.Empty(vm.ExpandedAlbumTracks);

        // A different one switches straight to it.
        vm.ToggleAlbumExpanded(one);
        vm.ToggleAlbumExpanded(two);
        Assert.Equal("Two", vm.ExpandedAlbumName);
    }

    // Recently Added groups by (Album, Artist), so a various-artists compilation
    // arrives as one tile per contributor - all reading the same album name.
    // Expansion used to be keyed by that name, so clicking any one of them
    // expanded every one of them at once, and each showed the whole
    // compilation instead of that contributor's share of it.
    [AvaloniaFact]
    public void Expanding_one_tile_of_a_compilation_leaves_its_namesakes_alone()
    {
        var (vm, _) = Make(
            Song("Blown Fruit", "Virtual Dreams II", "Palomatic"),
            Song("Flutter", "Virtual Dreams II", "Palomatic"),
            Song("Pause", "Virtual Dreams II", "Virgo"));
        vm.Repopulate();

        var palomatic = vm.RecentlyAddedGridTiles.Single(t => t.Artist == "Palomatic");
        var virgo = vm.RecentlyAddedGridTiles.Single(t => t.Artist == "Virgo");
        Assert.Equal(palomatic.Name, virgo.Name);

        vm.ToggleAlbumExpanded(palomatic);

        // The tile, not the album name - the two tiles differ only by artist.
        Assert.Equal(palomatic.Key, vm.ExpandedAlbumKey);
        Assert.NotEqual(virgo.Key, vm.ExpandedAlbumKey);

        // And it shows that tile's own tracks, not every namesake's.
        Assert.Equal(
            new[] { "Blown Fruit", "Flutter" },
            vm.ExpandedAlbumTracks.Select(t => t.Title).OrderBy(t => t));
    }

    // Clicking a second namesake is a switch, not a no-op: keyed by name, the
    // accordion could not tell "the same album again" from "the tile next door".
    [AvaloniaFact]
    public void Switching_between_two_tiles_of_a_compilation_swaps_the_expansion()
    {
        var (vm, _) = Make(
            Song("Blown Fruit", "Virtual Dreams II", "Palomatic"),
            Song("Pause", "Virtual Dreams II", "Virgo"));
        vm.Repopulate();

        var palomatic = vm.RecentlyAddedGridTiles.Single(t => t.Artist == "Palomatic");
        var virgo = vm.RecentlyAddedGridTiles.Single(t => t.Artist == "Virgo");

        vm.ToggleAlbumExpanded(palomatic);
        vm.ToggleAlbumExpanded(virgo);

        Assert.Equal(virgo.Key, vm.ExpandedAlbumKey);
        Assert.Equal(new[] { "Pause" }, vm.ExpandedAlbumTracks.Select(t => t.Title));
    }

    // A rescan replaces every tile instance, so the expansion has to be
    // re-found by key rather than by holding the old object - and re-found as
    // the *same* tile, not merely one with the same album name.
    [AvaloniaFact]
    public void A_rebuild_keeps_the_expansion_on_the_tile_it_was_opened_on()
    {
        var (vm, _) = Make(
            Song("Blown Fruit", "Virtual Dreams II", "Palomatic"),
            Song("Pause", "Virtual Dreams II", "Virgo"));
        vm.Repopulate();

        var palomatic = vm.RecentlyAddedGridTiles.Single(t => t.Artist == "Palomatic");
        vm.ToggleAlbumExpanded(palomatic);

        vm.Repopulate();

        Assert.Equal(palomatic.Key, vm.ExpandedAlbumKey);
        Assert.Equal(new[] { "Blown Fruit" }, vm.ExpandedAlbumTracks.Select(t => t.Title));
    }

    [AvaloniaFact]
    public void The_view_key_distinguishes_multi_selections_that_share_a_first_item()
    {
        var (vm, host) = Make(Song("a", "One", "X"), Song("b", "Two", "Y"));
        host.CurrentKind = SidebarItemKind.Albums;
        vm.Repopulate();

        vm.SetSelectedSubItems(new[] { "One" });
        var single = vm.CurrentViewKey;

        vm.SetSelectedSubItems(new[] { "One", "Two" });
        var pair = vm.CurrentViewKey;

        // Keying on the primary item alone would collide here and make the two
        // selections share saved scroll/selection state.
        Assert.NotEqual(single, pair);

        // Order within the selection must not matter.
        vm.SetSelectedSubItems(new[] { "Two", "One" });
        Assert.Equal(pair, vm.CurrentViewKey);
    }

    [AvaloniaFact]
    public void The_sub_list_offers_albums_or_artists_depending_on_the_view()
    {
        var (vm, host) = Make(Song("a", "One", "X"), Song("b", "Two", "Y"));

        host.CurrentKind = SidebarItemKind.Albums;
        vm.Repopulate();
        Assert.Equal(new[] { "One", "Two" }, vm.SubListItems);

        host.CurrentKind = SidebarItemKind.Artists;
        vm.RebuildSubListItems();
        Assert.Equal(new[] { "X", "Y" }, vm.SubListItems);

        host.CurrentKind = SidebarItemKind.Songs;
        vm.RebuildSubListItems();
        Assert.Empty(vm.SubListItems);
    }

    [AvaloniaFact]
    public void Artists_remembers_the_last_picked_artist_but_albums_starts_at_the_grid()
    {
        var (vm, host) = Make(Song("a", "One", "X"), Song("b", "Two", "Y"));
        host.CurrentKind = SidebarItemKind.Artists;
        vm.Repopulate();

        // Nothing picked yet - falls back to the first artist.
        Assert.Equal("X", vm.InitialSubItemForCurrentView());

        vm.SetSelectedSubItems(new[] { "Y" });
        Assert.Equal("Y", vm.InitialSubItemForCurrentView());

        host.CurrentKind = SidebarItemKind.Albums;
        vm.RebuildSubListItems();
        // Albums starts at the tile grid rather than auto-selecting an album.
        Assert.Null(vm.InitialSubItemForCurrentView());
    }

    [AvaloniaFact]
    public void Dragging_off_the_recently_added_grid_resolves_tracks_by_album_too()
    {
        var (vm, host) = Make(Song("a", "One", "X"), Song("b", "Two", "Y"));
        vm.Repopulate();

        // Not just Albums: a multi-selection dragged straight off the Recently
        // Added grid never switches the sidebar to Albums first.
        host.CurrentKind = SidebarItemKind.RecentlyAdded;
        Assert.Equal(new[] { "a" }, vm.GetTracksForSubListItems(new[] { "One" }).Select(t => t.Title));

        host.CurrentKind = SidebarItemKind.Artists;
        Assert.Equal(new[] { "b" }, vm.GetTracksForSubListItems(new[] { "Y" }).Select(t => t.Title));

        host.CurrentKind = SidebarItemKind.Songs;
        Assert.Empty(vm.GetTracksForSubListItems(new[] { "One" }));
    }

    [AvaloniaFact]
    public async Task Tile_grids_are_built_only_for_the_views_that_paint_them()
    {
        var (vm, host) = Make(Song("a", "One", "X"));
        host.CurrentKind = SidebarItemKind.Songs;
        vm.Repopulate();

        vm.FilterText = "nothing matches";
        await vm.RebuildRowsImmediatelyAsync();
        // Repopulate seeded the grids from the unfiltered library; a Songs-view
        // rebuild must not spend two full passes re-deriving them. See Tier 1.5.
        Assert.Single(vm.AlbumGridTiles);

        host.CurrentKind = SidebarItemKind.Albums;
        await vm.RebuildRowsImmediatelyAsync();
        // On Albums the filter does reach the grid.
        Assert.Empty(vm.AlbumGridTiles);
    }

    [AvaloniaFact]
    public async Task A_playlist_view_shows_the_playlists_own_order()
    {
        var b = Song("b", "One", "X");
        var a = Song("a", "One", "X");
        var (vm, host) = Make(a, b);
        host.CurrentKind = SidebarItemKind.Playlist;
        host.CurrentPlaylist = new Playlist("mine", new List<Track> { b, a });
        vm.Repopulate();

        await vm.RebuildRowsImmediatelyAsync();

        // Playlists have a user-defined, drag-reorderable order, so the column
        // sort is ignored while viewing one.
        Assert.Equal(new[] { "b", "a" }, vm.Rows.Select(r => r.Track.Title));
    }
}

// The device-row state machine, driven without a MainViewModel at all - the
// pre-split version of these had to go through the full ViewModel.
public class DeviceSidebarSectionTests
{
    private sealed class Host : IDeviceSidebarHost
    {
        public string? PairedServerFingerprint { get; set; }
        public bool IsSyncing { get; set; }
        public SidebarItem? SelectedSidebarItem { get; set; }
        public SidebarItem? DefaultSelection { get; set; }
        public readonly List<string> Forgotten = new();
        public int RowsChangedCount;
        public void ForgetSyncedDevice(string fingerprint) => Forgotten.Add(fingerprint);
        public void DeviceRowsChanged() => RowsChangedCount++;
    }

    private static int _port = 7000;

    private static DiscoveredDevice Device(string instanceName, string fingerprint = "", string? alias = null) =>
        new()
        {
            InstanceName = instanceName,
            Fingerprint = fingerprint,
            Alias = alias ?? instanceName,
            BaseUri = NetworkDiscoveryService.HttpOrigin(new IPEndPoint(IPAddress.Loopback, System.Threading.Interlocked.Increment(ref _port))),
        };

    private static (DeviceSidebarSection Section, ObservableCollection<SidebarItem> Items, Host Host) Make()
    {
        var items = new ObservableCollection<SidebarItem>
        {
            new(SidebarItemKind.Header, "Library"),
            new(SidebarItemKind.Songs, "Songs"),
        };
        var host = new Host { DefaultSelection = items[1] };
        return (new DeviceSidebarSection(items, host, nicknames: null, reachability: null), items, host);
    }

    [Fact]
    public void A_peer_with_no_resolved_fingerprint_yet_gets_no_row()
    {
        var (section, items, _) = Make();

        // Showing it now would display the raw mDNS instance name; the
        // discovery re-fires once /info answers with a real alias.
        section.AddOrUpdate(Device("laptop.local"));

        Assert.DoesNotContain(items, i => i.Kind == SidebarItemKind.Device);
    }

    // One section, because there is one kind of thing to find. This used to
    // fork on the peer's advertised role, back when a Flower app could be
    // flipped into being a server too.
    [Fact]
    public void Every_discovered_device_lands_under_one_Servers_section()
    {
        var (section, items, _) = Make();

        section.AddOrUpdate(Device("laptop", "fp-laptop", "Laptop"));
        section.AddOrUpdate(Device("nas", "fp-nas", "NAS"));

        Assert.DoesNotContain(items, i => i.Kind == SidebarItemKind.Header && i.Name == "Devices");
        var header = Assert.Single(items, i => i.Kind == SidebarItemKind.Header && i.Name == "Servers");

        // The section's members sit contiguously right after its own header,
        // which is what lets a row's section be found by walking backward.
        var headerIndex = items.IndexOf(header);
        Assert.Equal("Laptop", items[headerIndex + 1].Name);
        Assert.Equal("NAS", items[headerIndex + 2].Name);
    }

    // A second sighting of a peer already listed updates the row in place
    // rather than adding another, and does not disturb the selection sitting
    // on it.
    [Fact]
    public void Re_announcing_a_listed_peer_keeps_its_row_and_its_selection()
    {
        var (section, items, host) = Make();
        section.AddOrUpdate(Device("nas", "fp-nas", "NAS"));
        var row = items.Single(i => i.Kind == SidebarItemKind.Device);
        host.SelectedSidebarItem = row;

        section.AddOrUpdate(Device("nas", "fp-nas", "NAS"));

        Assert.Same(row, items.Single(i => i.Kind == SidebarItemKind.Device));
        Assert.Same(row, host.SelectedSidebarItem);
    }

    [Fact]
    public void Two_rows_for_one_peer_collapse_once_a_fingerprint_resolves()
    {
        var (section, items, host) = Make();
        host.PairedServerFingerprint = null;

        // Bonjour's collision-avoidance can republish the same physical device
        // under an auto-renamed instance name before the old advertisement is
        // withdrawn.
        section.AddOrUpdate(Device("nas", "fp-nas", "NAS"));
        section.AddOrUpdate(Device("nas-2", "fp-nas", "NAS"));

        var row = Assert.Single(items.Where(i => i.Kind == SidebarItemKind.Device));
        Assert.Equal("nas-2", row.Device!.InstanceName);
        // The surviving row is the same still-present device, so its once-per-
        // session sync dedup must NOT be cleared.
        Assert.Empty(host.Forgotten);
    }

    [Fact]
    public void Two_distinct_devices_sharing_a_computer_name_stay_separate()
    {
        var (section, items, _) = Make();

        section.AddOrUpdate(Device("macbook", "fp-one", "MacBook"));
        section.AddOrUpdate(Device("macbook", "fp-two", "MacBook"));

        // Matching on InstanceName regardless of a resolved fingerprint would
        // conflate them and pin one row at the wrong endpoint.
        Assert.Equal(2, items.Count(i => i.Kind == SidebarItemKind.Device));
        // Colliding display names each get their IP as a subtitle to tell them
        // apart - sync and trust key off Fingerprint, so this is purely visual.
        Assert.All(items.Where(i => i.Kind == SidebarItemKind.Device), i => Assert.NotNull(i.Subtitle));
    }

    [Fact]
    public void A_goodbye_for_an_ambiguous_instance_name_removes_nothing()
    {
        var (section, items, host) = Make();
        section.AddOrUpdate(Device("macbook", "fp-one", "MacBook"));
        section.AddOrUpdate(Device("macbook", "fp-two", "MacBook"));

        // mDNS's goodbye carries only the instance name, so there is no way to
        // tell which of the two actually left - guessing wrong is worse.
        section.Remove("macbook");

        Assert.Equal(2, items.Count(i => i.Kind == SidebarItemKind.Device));
        Assert.Empty(host.Forgotten);
    }

    [Fact]
    public void A_departing_peer_is_removed_reselected_away_from_and_forgotten()
    {
        var (section, items, host) = Make();
        section.AddOrUpdate(Device("laptop", "fp-laptop", "Laptop"));
        host.SelectedSidebarItem = items.Single(i => i.Kind == SidebarItemKind.Device);

        section.Remove("laptop");

        Assert.DoesNotContain(items, i => i.Kind == SidebarItemKind.Device);
        Assert.DoesNotContain(items, i => i.Kind == SidebarItemKind.Header && i.Name == "Devices");
        Assert.Same(host.DefaultSelection, host.SelectedSidebarItem);
        // Cleared so a fresh sync fires if it comes back later this session.
        Assert.Equal(new[] { "fp-laptop" }, host.Forgotten);
    }

    [Fact]
    public void The_paired_server_row_survives_going_offline()
    {
        var (section, items, host) = Make();
        host.PairedServerFingerprint = "fp-nas";
        section.AddOrUpdate(Device("nas", "fp-nas", "NAS"));
        Assert.True(items.Single(i => i.Kind == SidebarItemKind.Device).IsPairedServer);

        section.Remove("nas");

        // Pinned in place rather than disappearing - it just goes unreachable.
        var row = Assert.Single(items.Where(i => i.Kind == SidebarItemKind.Device));
        Assert.True(row.IsPairedServer);
        Assert.False(row.IsReachable);
    }

    [Fact]
    public void Unpinning_removes_a_row_that_has_no_live_device_behind_it()
    {
        var (section, items, host) = Make();
        // What MainViewModel's BuildSidebarItems seeds at launch: the pairing is
        // known from settings, but this session has discovered nothing yet.
        items.Add(new SidebarItem(SidebarItemKind.Header, "Servers"));
        items.Add(new SidebarItem(SidebarItemKind.Device, "NAS") { IsPairedServer = true });

        section.UnpinPairedServerRow();

        Assert.DoesNotContain(items, i => i.Kind == SidebarItemKind.Device);
        Assert.DoesNotContain(items, i => i.Kind == SidebarItemKind.Header && i.Name == "Servers");
    }

    [Fact]
    public void Unpinning_keeps_a_row_that_is_still_discovered()
    {
        var (section, items, host) = Make();
        host.PairedServerFingerprint = "fp-nas";
        section.AddOrUpdate(Device("nas", "fp-nas", "NAS"));

        host.PairedServerFingerprint = null;
        section.UnpinPairedServerRow();

        var row = Assert.Single(items.Where(i => i.Kind == SidebarItemKind.Device));
        Assert.False(row.IsPairedServer);
    }

    [Fact]
    public void A_rediscovered_peer_claims_the_pinned_placeholder_instead_of_adding_a_second_row()
    {
        var (section, items, host) = Make();
        host.PairedServerFingerprint = "fp-nas";
        items.Add(new SidebarItem(SidebarItemKind.Header, "Servers"));
        items.Add(new SidebarItem(SidebarItemKind.Device, "NAS") { IsPairedServer = true });

        section.AddOrUpdate(Device("nas", "fp-nas", "NAS"));

        var row = Assert.Single(items.Where(i => i.Kind == SidebarItemKind.Device));
        Assert.NotNull(row.Device);
        Assert.True(row.IsPairedServer);
    }

    [Fact]
    public void A_row_updated_mid_sync_keeps_its_spinner()
    {
        var (section, items, host) = Make();
        host.PairedServerFingerprint = "fp-nas";
        host.IsSyncing = true;

        section.AddOrUpdate(Device("nas", "fp-nas", "NAS"));

        // The spinner is only pushed on IsSyncing's own edges, so a row created
        // or re-created mid-sync has to carry the current state forward.
        Assert.True(items.Single(i => i.Kind == SidebarItemKind.Device).IsSyncing);
    }
}
