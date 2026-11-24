using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Models;
using Flower.Persistence;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests.Playback;

// UF-17: a queue row is one particular entry of the playlist it follows, so an
// edit to that entry reaches that row even when the song is there more than once.
[Collection("PlatformDataDirectory")]
public class QueueKeepsItsCopiesTests : PinnedDataDirectory
{
    private readonly Track _a = new() { Title = "A", Album = "A", Path = "/music/A.mp3" };
    private readonly Track _b = new() { Title = "B", Album = "B", Path = "/music/B.mp3" };
    private readonly Track _c = new() { Title = "C", Album = "C", Path = "/music/C.mp3" };

    private PlaylistControlViewModel Control(Library library)
    {
        var vm = Own(new PlaylistControlViewModel(
            new FakeAudioManager(), new MainPlaylist(library.Tracks.ToList()), library, new AppSettings(),
            new AppSettingsStore(NullLogger<AppSettingsStore>.Instance),
            NullLogger<PlaylistControlViewModel>.Instance,
            queueStore: new PlayQueueStore(NullLogger<PlayQueueStore>.Instance)));
        vm.OnUiThread = work => work();
        vm.OffPlaybackThread = work => work();
        return vm;
    }

    private (PlaylistControlViewModel Vm, Playlist Playlist, Library Library) Playing(Track[] entries, int slot)
    {
        var library = new Library(entries.Distinct().ToList());
        var playlist = new Playlist("Mix", [.. entries]);
        library.AddPlaylist(playlist);
        var vm = Control(library);
        vm.SetCurrentPlaylist(new Playlist("Now Playing Queue", [.. entries]), playlist, followsOrder: true);
        vm.Play(entries[slot], slot);
        return (vm, playlist, library);
    }

    private static string[] Titles(PlaylistControlViewModel vm) =>
        vm.CurrentPlaylist.Tracks.Select(t => t.Title!).ToArray();

    [Fact]
    public void Removing_the_copy_just_before_the_one_playing_leaves_it_playing()
    {
        var (vm, playlist, _) = Playing([_a, _a, _b], slot: 1);

        playlist.RemoveEntriesAt([0]);

        Assert.Equal(["A", "B"], Titles(vm));
        Assert.Equal(0, vm.QueueIndex);
        vm.Next();
        Assert.Equal("B", vm.CurrentlyPlayingTrack?.Title);
        Assert.Equal(["A", "B"], Titles(vm));
    }

    [Fact]
    public void Removing_the_copy_playing_keeps_the_twin_after_it_to_play_next()
    {
        var (vm, playlist, _) = Playing([_a, _a, _b], slot: 0);

        playlist.RemoveEntriesAt([0]);

        Assert.Equal(["A", "A", "B"], Titles(vm));
        Assert.Equal(0, vm.QueueIndex);
        vm.Next();
        Assert.Equal(["A", "B"], Titles(vm));
        Assert.Equal(0, vm.QueueIndex);
    }

    [Fact]
    public void Dragging_a_copy_past_the_one_playing_moves_the_place()
    {
        var (vm, playlist, _) = Playing([_a, _b, _a], slot: 2);

        playlist.MoveEntries([0], 3);

        Assert.Equal(["B", "A", "A"], Titles(vm));
        Assert.Equal(1, vm.QueueIndex);
    }

    [Fact]
    public void Swapping_twin_copies_takes_the_place_with_the_one_playing()
    {
        var (vm, playlist, _) = Playing([_a, _a, _b], slot: 0);

        playlist.MoveEntries([0], 2);

        Assert.Equal(1, vm.QueueIndex);
    }

    [Fact]
    public void Removing_some_copies_but_not_all_leaves_the_one_playing()
    {
        var (vm, playlist, _) = Playing([_a, _a, _a, _b], slot: 1);

        playlist.RemoveEntriesAt([0, 2]);

        Assert.Equal(["A", "B"], Titles(vm));
        Assert.Equal(0, vm.QueueIndex);
        vm.Next();
        Assert.Equal(["A", "B"], Titles(vm));
    }

    [Fact]
    public void A_copy_taken_out_of_the_queue_by_hand_does_not_shift_the_others()
    {
        var (vm, playlist, _) = Playing([_a, _b, _a, _c], slot: 2);
        vm.RemoveQueueEntry(0);

        playlist.RemoveEntriesAt([0]);

        Assert.Equal(["B", "A", "C"], Titles(vm));
        vm.Next();
        Assert.Equal("C", vm.CurrentlyPlayingTrack?.Title);
        Assert.Equal(["B", "A", "C"], Titles(vm));
    }

    [Fact]
    public void The_copy_playing_is_named_after_its_twin_left_the_queue_by_hand()
    {
        var (vm, _, _) = Playing([_a, _b, _a, _c], slot: 2);

        vm.RemoveQueueEntry(0);

        Assert.Equal(1, vm.CurrentlyPlayingOccurrence);
    }

    [Fact]
    public void A_relaunch_remembers_which_copy_each_row_is()
    {
        var (before, playlist, library) = Playing([_a, _b, _a, _c], slot: 2);
        before.RemoveQueueEntry(0);
        before.SavePlaybackState();
        before.Dispose();

        var after = Control(library);
        after.RestoreQueue();
        playlist.RemoveEntriesAt([2]);

        Assert.Equal(["B", "C"], Titles(after));
        Assert.Equal("C", after.CurrentlyPlayingTrack?.Title);
    }

    [Fact]
    public void Another_devices_edit_to_side_by_side_copies_leaves_the_one_playing()
    {
        var (vm, playlist, library) = Playing([_a, _a, _b], slot: 1);

        library.ReplacePlaylists(
        [
            new Playlist(playlist.Id, "Mix", [], playlist.UpdatedAt.AddMinutes(1), entries: playlist.Entries.Skip(1).ToList()),
        ]);

        Assert.Equal(["A", "B"], Titles(vm));
        Assert.Equal(0, vm.QueueIndex);
        vm.Next();
        Assert.Equal("B", vm.CurrentlyPlayingTrack?.Title);
        Assert.Equal(["A", "B"], Titles(vm));
    }

    [Fact]
    public void Taking_the_servers_row_ids_leaves_the_queue_on_its_copy()
    {
        var (vm, playlist, library) = Playing([_a, _a, _b], slot: 1);
        var rows = playlist.Entries.Select(e => e.ToWire() with { EntryId = Guid.NewGuid() }).ToList();

        Assert.True(playlist.TakeEntryIdsFrom(playlist.Entries, rows));
        library.NotePlaylistRowsRenamed();

        Assert.Equal(["A", "A", "B"], Titles(vm));
        Assert.Equal(1, vm.QueueIndex);
        playlist.RemoveEntriesAt([0]);
        Assert.Equal(["A", "B"], Titles(vm));
        Assert.Equal(0, vm.QueueIndex);
    }

    [Fact]
    public void A_song_lined_up_by_hand_is_not_mistaken_for_a_copy_in_the_playlist()
    {
        var (vm, playlist, _) = Playing([_a, _b, _c], slot: 1);
        vm.AddToQueue([_a]);

        playlist.RemoveEntriesAt([0]);

        Assert.Equal(["B", "C", "A"], Titles(vm));
    }
}
