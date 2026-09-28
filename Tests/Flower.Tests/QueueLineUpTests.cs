using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Models;
using Flower.Persistence;
using Flower.Tests.TestSupport;
using Flower.ViewModels;
using Flower.ViewModels.Mobile;

using Xunit;

namespace Flower.Tests;

// Songs put into the queue that is playing rather than in place of it - Play
// Next and Add to Queue - and the queue rearranged by hand, from the phone's
// Queue tab the way a playlist is.
[Collection("PlatformDataDirectory")]
public class QueueLineUpTests : PinnedDataDirectory
{
    // An album apiece, so album order and any other order differ - a menu
    // sorting a playlist by album is otherwise invisible.
    private static Track T(string title) => new() { Title = title, Album = title, Path = $"/music/{title}.mp3" };

    private static List<Track> Tracks(params string[] titles) => titles.Select(T).ToList();

    private static PlaylistControlViewModel Control(List<Track> library, out FakeAudioManager audio)
    {
        audio = new FakeAudioManager();
        return new PlaylistControlViewModel(
            audio, new MainPlaylist(library), new Library(library), new AppSettings(),
            new AppSettingsStore(NullLogger<AppSettingsStore>.Instance),
            NullLogger<PlaylistControlViewModel>.Instance);
    }

    // A queue of these, playing the one at `index`.
    private static PlaylistControlViewModel Playing(List<Track> queue, int index, out FakeAudioManager audio)
    {
        var vm = Control(queue, out audio);
        vm.SetCurrentPlaylist(new Playlist("Now Playing Queue", queue));
        vm.Play(queue[index], index);
        return vm;
    }

    private static string[] Titles(PlaylistControlViewModel vm) => vm.CurrentPlaylist.Tracks.Select(t => t.Title!).ToArray();

    [Fact]
    public void Play_next_goes_straight_after_the_song_playing_and_is_armed_next()
    {
        var queue = Tracks("A", "B", "C");
        var vm = Playing(queue, 0, out var audio);
        var x = T("X");

        vm.PlayNext([x]);

        Assert.Equal(new[] { "A", "X", "B", "C" }, Titles(vm));
        Assert.Equal(0, vm.QueueIndex);
        Assert.Same(x, audio.LastUpcoming);
    }

    // "Right after the song playing" - so the latest Play Next is the next one.
    [Fact]
    public void A_later_play_next_goes_ahead_of_an_earlier_one()
    {
        var vm = Playing(Tracks("A", "B"), 0, out _);

        vm.PlayNext([T("X")]);
        vm.PlayNext([T("Y"), T("Z")]);

        Assert.Equal(new[] { "A", "Y", "Z", "X", "B" }, Titles(vm));
    }

    [Fact]
    public void Add_to_queue_goes_at_the_end()
    {
        var vm = Playing(Tracks("A", "B", "C"), 1, out _);

        vm.AddToQueue([T("X"), T("Y")]);

        Assert.Equal(new[] { "A", "B", "C", "X", "Y" }, Titles(vm));
        Assert.Equal(1, vm.QueueIndex);
    }

    // With nothing playing there is nothing to come after, and the queue as
    // it stands is only the library in scan order.
    [Fact]
    public void With_nothing_playing_what_is_lined_up_becomes_the_queue_and_starts()
    {
        var vm = Control(Tracks("A", "B"), out var audio);
        var x = T("X");

        vm.AddToQueue([x, T("Y")]);

        Assert.Equal(new[] { "X", "Y" }, Titles(vm));
        Assert.Same(x, vm.CurrentlyPlayingTrack);
        Assert.Same(x, audio.LastPlayed);
    }

    // The queue before anything plays is MainPlaylist - the library's own
    // list, which a queue edit must never reach.
    [Fact]
    public void Lining_up_never_edits_the_list_the_queue_was_made_from()
    {
        var library = Tracks("A", "B", "C");
        var vm = Control(library, out _);
        var original = vm.CurrentPlaylist;
        vm.Play(library[0], 0);

        vm.PlayNext([T("X")]);

        Assert.Equal(new[] { "A", "B", "C" }, original.Tracks.Select(t => t.Title));
        Assert.NotSame(original, vm.CurrentPlaylist);
    }

    // Under shuffle the list is not walked in order, so without the lined-up
    // count a Play Next would still be a roll of the dice.
    [Fact]
    public void Under_shuffle_what_was_lined_up_plays_next_in_order()
    {
        var queue = Enumerable.Range(0, 30).Select(i => T($"S{i}")).ToList();
        var vm = Playing(queue, 5, out var audio);
        vm.ToggleShuffle();

        vm.PlayNext([T("X"), T("Y")]);
        Assert.Equal("X", audio.LastUpcoming?.Title);

        vm.Next();
        Assert.Equal("X", vm.CurrentlyPlayingTrack?.Title);
        vm.Next();
        Assert.Equal("Y", vm.CurrentlyPlayingTrack?.Title);
        Assert.Equal(0, vm.LinedUpCount);
    }

    // There is no end of the queue to speak of under shuffle, so Add to Queue
    // goes after what was lined up before it rather than into the pool.
    [Fact]
    public void Under_shuffle_add_to_queue_goes_after_what_was_lined_up()
    {
        var vm = Playing(Tracks("A", "B", "C"), 0, out _);
        vm.ToggleShuffle();

        vm.PlayNext([T("X")]);
        vm.AddToQueue([T("Y")]);

        Assert.Equal(new[] { "A", "X", "Y", "B", "C" }, Titles(vm));
        Assert.Equal(2, vm.LinedUpCount);
    }

    [Fact]
    public void Jumping_somewhere_else_forgets_what_was_lined_up()
    {
        var queue = Tracks("A", "B", "C", "D");
        var vm = Playing(queue, 0, out _);
        vm.PlayNext([T("X")]);

        vm.Play(queue[3], 4);

        Assert.Equal(0, vm.LinedUpCount);
    }

    [Fact]
    public void A_queue_entry_moves_like_a_playlist_entry_and_the_song_playing_keeps_its_place()
    {
        var vm = Playing(Tracks("A", "B", "C", "D"), 1, out var audio);

        vm.MoveQueueEntry(3, 2);

        Assert.Equal(new[] { "A", "B", "D", "C" }, Titles(vm));
        Assert.Equal(1, vm.QueueIndex);
        Assert.Equal("D", audio.LastUpcoming?.Title);
    }

    [Fact]
    public void Nothing_moves_ahead_of_the_song_playing_and_it_does_not_move_itself()
    {
        var vm = Playing(Tracks("A", "B", "C"), 1, out _);

        vm.MoveQueueEntry(2, 1);
        vm.MoveQueueEntry(1, 3);

        Assert.Equal(new[] { "A", "B", "C" }, Titles(vm));
    }

    // One song twice in the queue: the slot moved is the one asked for, and
    // the song playing stays the copy that is playing.
    [Fact]
    public void Moving_one_copy_of_a_song_leaves_the_other_where_it_is()
    {
        var a = T("A");
        var b = T("B");
        var vm = Playing([a, b, a, T("C")], 2, out _);

        vm.MoveQueueEntry(0, 4);

        Assert.Equal(new[] { "B", "A", "C", "A" }, Titles(vm));
        Assert.Equal(1, vm.QueueIndex);
    }

    [Fact]
    public void A_queue_entry_can_be_taken_out_but_not_the_song_playing()
    {
        var vm = Playing(Tracks("A", "B", "C"), 1, out _);

        vm.RemoveQueueEntry(1);
        vm.RemoveQueueEntry(0);

        Assert.Equal(new[] { "B", "C" }, Titles(vm));
        Assert.Equal(0, vm.QueueIndex);
    }

    // ── The phone ─────────────────────────────────────────────────────────

    private static MainViewModelHarness.MobileParts Phone(List<Track> tracks)
    {
        var parts = MainViewModelHarness.BuildParts(new Library(tracks), new MainPlaylist(tracks));
        parts.AppSettings.MobileTabs = [nameof(MobileTab.Queue), nameof(MobileTab.Playlists)];
        var mobile = new MobileMainViewModel(parts.Main, parts.PlaylistControl, parts.CurrentlyPlaying,
            NullLogger<MobileMainViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        return new MainViewModelHarness.MobileParts(mobile, parts);
    }

    private static string[] QueueTitles(MobileMainViewModel mobile) => mobile.QueueRows.Select(r => r.Track.Title!).ToArray();

    private static MainViewModelHarness.MobileParts PhonePlaying(params string[] titles)
    {
        var tracks = Tracks(titles);
        var scope = Phone(tracks);
        scope.Parts.Main.SetPlayQueue(tracks);
        scope.Parts.PlaylistControl.Play(tracks[0], 0);
        Dispatcher.UIThread.RunJobs();
        return scope;
    }

    [AvaloniaFact]
    public void Dragging_a_row_on_the_queue_rearranges_what_plays_next()
    {
        using var scope = PhonePlaying("A", "B", "C", "D");
        var mobile = scope.Mobile;

        var rows = mobile.QueueRows;
        mobile.ReorderQueueRow(rows[3], rows[1]);

        Assert.Equal(new[] { "A", "D", "B", "C" }, QueueTitles(mobile));
        Assert.Equal("D", scope.Parts.Audio.LastUpcoming?.Title);
    }

    [AvaloniaFact]
    public void The_song_playing_has_no_handle_and_shuffle_takes_them_all_away()
    {
        using var scope = PhonePlaying("A", "B", "C");
        var mobile = scope.Mobile;

        Assert.False(mobile.QueueRows[0].CanBeDragged);
        Assert.True(mobile.QueueRows[1].CanBeDragged);
        Assert.True(mobile.IsQueueReorderable);

        scope.Parts.PlaylistControl.ToggleShuffle();

        Assert.False(mobile.IsQueueReorderable);
    }

    [AvaloniaFact]
    public void A_songs_menu_plays_it_next()
    {
        using var scope = PhonePlaying("A", "B", "C");
        var mobile = scope.Mobile;

        mobile.OpenTrackActionsCommand.Execute(mobile.QueueRows[2]);
        mobile.PlayNextActionTargetCommand.Execute(null);

        Assert.Equal(new[] { "A", "C", "B", "C" }, QueueTitles(mobile));
        Assert.Equal(MobileSheet.None, mobile.ActiveSheet);
    }

    // Only on the queue's own rows, and not on the song playing.
    [AvaloniaFact]
    public void A_song_on_the_queue_can_be_taken_out_of_it_from_its_menu()
    {
        using var scope = PhonePlaying("A", "B", "C");
        var mobile = scope.Mobile;

        mobile.OpenTrackActionsCommand.Execute(mobile.QueueRows[0]);
        Assert.False(mobile.CanRemoveActionTargetFromQueue);
        mobile.CloseSheetCommand.Execute(null);

        mobile.OpenTrackActionsCommand.Execute(mobile.QueueRows[1]);
        Assert.True(mobile.CanRemoveActionTargetFromQueue);
        mobile.RemoveActionTargetFromQueueCommand.Execute(null);

        Assert.Equal(new[] { "A", "C" }, QueueTitles(mobile));
    }

    // In the playlist's own order - an album menu's songs otherwise go in
    // album order, which for a playlist would scramble it.
    [AvaloniaFact]
    public void A_playlists_menu_queues_it_in_its_own_order()
    {
        using var scope = PhonePlaying("A", "B", "C");
        var mobile = scope.Mobile;
        var library = scope.Parts.Library.Tracks;
        var playlist = new Playlist("Mix", [library.Single(t => t.Title == "C"), library.Single(t => t.Title == "B")]);
        var item = new SidebarItem(SidebarItemKind.Playlist, playlist.Name, playlist: playlist);

        mobile.OpenPlaylistActionsCommand.Execute(item);
        mobile.AddAlbumActionTargetToQueueCommand.Execute(null);

        Assert.Equal(new[] { "A", "B", "C", "C", "B" }, QueueTitles(mobile));
    }

    private static SidebarItem Mix(MainViewModelHarness.MobileParts scope, params string[] titles)
    {
        var library = scope.Parts.Library.Tracks;
        var playlist = new Playlist("Mix", titles.Select(t => library.Single(x => x.Title == t)).ToList());
        return new SidebarItem(SidebarItemKind.Playlist, playlist.Name, playlist: playlist);
    }

    [AvaloniaFact]
    public void A_playlists_menu_plays_it_in_its_own_order()
    {
        using var scope = PhonePlaying("A", "B", "C");
        var mobile = scope.Mobile;

        mobile.OpenPlaylistActionsCommand.Execute(Mix(scope, "C", "A", "B"));
        mobile.PlayAlbumActionTargetCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "C", "A", "B" }, Titles(scope.Parts.PlaylistControl));
        Assert.Equal("C", scope.Parts.PlaylistControl.CurrentlyPlayingTrack?.Title);
    }

    [AvaloniaFact]
    public void A_playlists_menu_adds_it_to_another_playlist_in_its_own_order()
    {
        using var scope = PhonePlaying("A", "B", "C");
        var mobile = scope.Mobile;
        var target = new Playlist("Other", []);
        scope.Parts.Library.AddPlaylist(target);

        mobile.OpenPlaylistActionsCommand.Execute(Mix(scope, "C", "A", "B"));
        mobile.AddAlbumActionTargetToPlaylistCommand.Execute(null);
        mobile.AddTrackToPlaylistCommand.Execute(new SidebarItem(SidebarItemKind.Playlist, target.Name, playlist: target));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "C", "A", "B" }, target.Tracks.Select(t => t.Title));
    }
}
