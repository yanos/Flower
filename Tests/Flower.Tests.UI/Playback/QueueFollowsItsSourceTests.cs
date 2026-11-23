using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;

using Flower.Models;
using Flower.ViewModels;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests.Playback;

// The queue is a copy of the list it was played from. What is done to that
// list while it plays has to reach the copy, or the screen says one order and
// the next song comes from another.
[Collection("PlatformDataDirectory")]
public class QueueFollowsItsSourceTests : PinnedDataDirectory
{
    private static Track T(string title) =>
        new() { Title = title, Album = title, Path = $"/music/{title}.mp3", Duration = TimeSpan.FromMinutes(3) };

    private static string[] Queue(MainViewModelHarness.Parts parts) =>
        parts.PlaylistControl.CurrentPlaylist.Tracks.Select(t => t.Title!).ToArray();

    private static Track Song(Playlist playlist, string title) => playlist.Tracks.First(t => t.Title == title);

    private static TrackRowViewModel Row(MainViewModelHarness.Parts parts, string title) =>
        parts.Main.Rows.First(r => r.Track.Title == title);

    // A playlist of these on screen, played from the row at `playing`.
    private async Task<(MainViewModelHarness.Parts Parts, Playlist Playlist)> Playing(int playing, params string[] titles)
    {
        var tracks = titles.Select(T).ToList();
        var library = new Library(tracks);
        var playlist = new Playlist("Mix", new List<Track>(tracks));
        library.AddPlaylist(playlist);

        var parts = Own(MainViewModelHarness.Build(library, new MainPlaylist(library.Tracks)));
        parts.PlaylistControl.OnUiThread = work => work();
        parts.PlaylistControl.OffPlaybackThread = work => work();
        parts.Main.SelectedSidebarItem = parts.Main.SidebarItems.Single(i => i.Playlist == playlist);
        await parts.Main.RebuildRowsImmediatelyAsync();
        parts.Main.PlayTrack(tracks[playing], playing);
        return (parts, playlist);
    }

    [AvaloniaFact]
    public async Task A_song_moved_up_the_playing_playlist_is_what_plays_next()
    {
        var (parts, playlist) = await Playing(1, "A", "B", "C", "D");

        await parts.Main.ReorderPlaylistRow(playlist, Row(parts, "D"), Row(parts, "C"));

        Assert.Equal(new[] { "A", "B", "D", "C" }, Queue(parts));
        Assert.Equal(1, parts.PlaylistControl.QueueIndex);
        Assert.Equal("D", parts.Audio.LastUpcoming?.Title);
    }

    [AvaloniaFact]
    public async Task A_song_moved_from_after_the_playing_one_to_before_it_no_longer_plays_next()
    {
        var (parts, playlist) = await Playing(2, "A", "B", "C", "D");

        await parts.Main.ReorderPlaylistRow(playlist, Row(parts, "D"), Row(parts, "A"));

        Assert.Equal(new[] { "D", "A", "B", "C" }, Queue(parts));
        Assert.Equal(3, parts.PlaylistControl.QueueIndex);
        Assert.Null(parts.Audio.LastUpcoming);
    }

    [AvaloniaFact]
    public async Task The_playing_song_moved_carries_on_from_its_new_place()
    {
        var (parts, playlist) = await Playing(0, "A", "B", "C");

        await parts.Main.ReorderPlaylistRow(playlist, Row(parts, "A"), null);

        Assert.Equal(new[] { "B", "C", "A" }, Queue(parts));
        Assert.Equal(2, parts.PlaylistControl.QueueIndex);
        Assert.Null(parts.Audio.LastUpcoming);
    }

    [AvaloniaFact]
    public async Task Songs_lined_up_by_hand_stay_straight_after_the_one_playing()
    {
        var (parts, playlist) = await Playing(1, "A", "B", "C", "D");
        parts.Main.PlayNext([T("X")]);

        await parts.Main.ReorderPlaylistRow(playlist, Row(parts, "D"), Row(parts, "C"));

        Assert.Equal(new[] { "A", "B", "X", "D", "C" }, Queue(parts));
        Assert.Equal(1, parts.PlaylistControl.LinedUpCount);
        Assert.Equal("X", parts.Audio.LastUpcoming?.Title);
    }

    [AvaloniaFact]
    public async Task A_queue_in_another_order_than_the_playlist_is_left_alone()
    {
        var (parts, playlist) = await Playing(0, "A", "B", "C");
        var sorted = playlist.Tracks.Reverse().ToList();
        parts.Main.SetPlayQueue(sorted);
        parts.PlaylistControl.Play(sorted[0], 0);

        await parts.Main.ReorderPlaylistRow(playlist, Row(parts, "C"), Row(parts, "A"));

        Assert.Equal(new[] { "C", "B", "A" }, Queue(parts));
    }

    [AvaloniaFact]
    public async Task A_song_removed_from_the_library_leaves_the_queue_and_is_not_played_next()
    {
        var (parts, playlist) = await Playing(1, "A", "B", "C", "D");

        parts.Library.RemoveTracks([Song(playlist, "C")], []);

        Assert.Equal(new[] { "A", "B", "D" }, Queue(parts));
        Assert.Equal(1, parts.PlaylistControl.QueueIndex);
        Assert.Equal("D", parts.Audio.LastUpcoming?.Title);
    }

    [AvaloniaFact]
    public async Task The_song_playing_finishes_when_removed_and_is_gone_once_the_queue_moves_on()
    {
        var (parts, playlist) = await Playing(1, "A", "B", "C");
        var removed = Song(playlist, "B");

        parts.Library.RemoveTracks([removed], []);

        Assert.Same(removed, parts.PlaylistControl.CurrentlyPlayingTrack);
        Assert.Equal(new[] { "A", "B", "C" }, Queue(parts));

        parts.PlaylistControl.Next();

        Assert.Equal("C", parts.Audio.LastPlayed?.Title);
        Assert.Equal(new[] { "A", "C" }, Queue(parts));
        Assert.Equal(1, parts.PlaylistControl.QueueIndex);
    }

    [AvaloniaFact]
    public async Task Repeat_does_not_loop_a_song_that_left_the_library()
    {
        var (parts, playlist) = await Playing(0, "A", "B");
        parts.PlaylistControl.ToggleRepeat();
        Assert.Equal("A", parts.Audio.LastUpcoming?.Title);

        parts.Library.RemoveTracks([Song(playlist, "A")], []);

        Assert.Equal("B", parts.Audio.LastUpcoming?.Title);
    }

    [AvaloniaFact]
    public async Task The_last_song_removed_while_playing_is_not_parked_on_when_it_ends()
    {
        var (parts, playlist) = await Playing(1, "A", "B");

        parts.Library.RemoveTracks([Song(playlist, "B")], []);
        parts.Audio.RaiseEndReached();
        parts.Audio.RaisePlayedOut();

        Assert.Equal(new[] { "A" }, Queue(parts));
        Assert.Equal("A", parts.PlaylistControl.CurrentlyPlayingTrack?.Title);
    }

    [AvaloniaFact]
    public async Task A_song_a_scan_stopped_finding_stays_queued_since_its_drive_may_come_back()
    {
        var (parts, playlist) = await Playing(0, "A", "B", "C");

        parts.Library.UpdateTracks([Song(playlist, "A"), Song(playlist, "C")]);

        Assert.Equal(new[] { "A", "B", "C" }, Queue(parts));
    }

    [AvaloniaFact]
    public async Task A_song_removed_from_the_playing_playlist_leaves_the_queue()
    {
        var (parts, playlist) = await Playing(1, "A", "B", "C", "D");

        await parts.Main.RemoveRowsFromPlaylist(playlist, [Row(parts, "C")]);

        Assert.Equal(new[] { "A", "B", "D" }, Queue(parts));
        Assert.Equal("D", parts.Audio.LastUpcoming?.Title);
    }

    [AvaloniaFact]
    public async Task The_playing_song_removed_from_its_playlist_finishes_and_then_goes()
    {
        var (parts, playlist) = await Playing(1, "A", "B", "C");
        var playing = Song(playlist, "B");

        await parts.Main.RemoveRowsFromPlaylist(playlist, [Row(parts, playing.Title!)]);

        Assert.Same(playing, parts.PlaylistControl.CurrentlyPlayingTrack);
        Assert.Equal("C", parts.Audio.LastUpcoming?.Title);

        parts.PlaylistControl.Next();

        Assert.Equal(new[] { "A", "C" }, Queue(parts));
        Assert.Equal(1, parts.PlaylistControl.QueueIndex);
    }

    [AvaloniaFact]
    public async Task A_song_added_to_the_playing_playlist_plays_after_the_last_one()
    {
        var (parts, playlist) = await Playing(1, "A", "B");

        await parts.Main.AddTracksToPlaylist([T("X")], playlist);

        Assert.Equal(new[] { "A", "B", "X" }, Queue(parts));
        Assert.Equal("X", parts.Audio.LastUpcoming?.Title);
    }

    [AvaloniaFact]
    public async Task A_song_added_to_the_playlist_goes_ahead_of_what_was_added_to_the_queue()
    {
        // A song after the one playing, or Add to Queue would count as lined
        // up straight behind it, which plays first by design.
        var (parts, playlist) = await Playing(1, "A", "B", "C");
        parts.Main.AddToQueue([T("Y")]);

        await parts.Main.AddTracksToPlaylist([T("X")], playlist);

        Assert.Equal(new[] { "A", "B", "C", "X", "Y" }, Queue(parts));
    }

    [AvaloniaFact]
    public async Task A_song_taken_out_of_the_queue_by_hand_stays_out_after_a_playlist_edit()
    {
        var (parts, playlist) = await Playing(1, "A", "B", "C", "D");
        parts.PlaylistControl.RemoveQueueEntry(2);

        await parts.Main.AddTracksToPlaylist([T("X")], playlist);

        Assert.Equal(new[] { "A", "B", "D", "X" }, Queue(parts));
    }

    [AvaloniaFact]
    public async Task A_sorted_queue_takes_additions_and_removals_but_keeps_its_own_order()
    {
        var (parts, playlist) = await Playing(0, "A", "B", "C");
        var sorted = playlist.Tracks.Reverse().ToList();
        parts.Main.SetPlayQueue(sorted);
        parts.PlaylistControl.Play(sorted[0], 0);

        await parts.Main.RemoveRowsFromPlaylist(playlist, [Row(parts, "B")]);
        await parts.Main.AddTracksToPlaylist([T("X")], playlist);

        Assert.Equal(new[] { "C", "A", "X" }, Queue(parts));
    }

    [AvaloniaFact]
    public async Task A_reorder_synced_from_another_device_changes_what_plays_next()
    {
        var (parts, playlist) = await Playing(1, "A", "B", "C", "D");
        var synced = new Playlist(playlist.Id, playlist.Name, playlist.Tracks.Reverse().ToList(), playlist.UpdatedAt.AddMinutes(1));

        parts.Library.ReplacePlaylists([synced]);

        Assert.Equal(new[] { "D", "C", "B", "A" }, Queue(parts));
        Assert.Equal(2, parts.PlaylistControl.QueueIndex);
        Assert.Equal("A", parts.Audio.LastUpcoming?.Title);
    }

    [AvaloniaFact]
    public async Task Songs_added_and_removed_on_another_device_reach_the_queue()
    {
        var (parts, playlist) = await Playing(0, "A", "B", "C");
        var x = T("X");
        var synced = new Playlist(playlist.Id, playlist.Name,
            [Song(playlist, "A"), x, Song(playlist, "C")], playlist.UpdatedAt.AddMinutes(1));

        parts.Library.ReplacePlaylists([synced]);

        Assert.Equal(new[] { "A", "X", "C" }, Queue(parts));
        Assert.Same(x, parts.Audio.LastUpcoming);
    }

    [AvaloniaFact]
    public async Task A_song_on_an_unplugged_drive_stays_queued_through_a_playlist_edit()
    {
        var (parts, playlist) = await Playing(0, "A", "B", "C");
        parts.Library.UpdateTracks([Song(playlist, "A"), Song(playlist, "B")]);

        await parts.Main.AddTracksToPlaylist([T("X")], playlist);

        Assert.Equal(new[] { "A", "B", "C", "X" }, Queue(parts));
    }

    [AvaloniaFact]
    public async Task Deleting_the_playing_playlist_leaves_the_queue_playing()
    {
        var (parts, playlist) = await Playing(0, "A", "B");

        parts.Library.RemovePlaylist(playlist);

        Assert.Equal(new[] { "A", "B" }, Queue(parts));
        Assert.Equal("B", parts.Audio.LastUpcoming?.Title);
    }
}
