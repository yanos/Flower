using Flower.Models;
using Flower.Persistence;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Microsoft.Extensions.Logging.Abstractions;

namespace Flower.Tests.ViewModels;

[Collection("PlatformDataDirectory")]
public class QueueViewModelTests : PinnedDataDirectory
{
    private static List<Track> Tracks(params string[] titles) =>
        titles.Select(t => new Track { Title = t, Path = $"/music/{t}.mp3", Album = "Album", Artists = "Artist" }).ToList();

    private sealed class Parts : IDisposable
    {
        public PlaylistControlViewModel PlaylistControl { get; }
        public QueueViewModel Queue { get; }
        public Library Library { get; }

        public Parts(List<Track> tracks)
        {
            Library = new Library(tracks);
            PlaylistControl = new PlaylistControlViewModel(new FakeAudioManager(), new MainPlaylist(tracks),
                Library, new AppSettings(), new AppSettingsStore(NullLogger<AppSettingsStore>.Instance),
                NullLogger<PlaylistControlViewModel>.Instance);
            PlaylistControl.OnUiThread = work => work();
            PlaylistControl.OffPlaybackThread = work => work();
            Queue = new QueueViewModel(PlaylistControl);
            PlaylistControl.SetCurrentPlaylist(new Playlist("Queue", tracks));
            PlaylistControl.Play(tracks[0], 0);
        }

        public void Dispose()
        {
            Queue.Dispose();
            PlaylistControl.Dispose();
        }
    }

    private Parts Playing(params string[] titles) => Own(new Parts(Tracks(titles)));

    private static string[] Titles(QueueViewModel queue) => queue.Rows.Select(r => r.Track.Title!).ToArray();

    // Like Home: a queue nobody is looking at is not rebuilt on every song.
    [Fact]
    public void The_queue_is_only_built_while_it_is_showing()
    {
        using var parts = Playing("A", "B", "C");
        var queue = parts.Queue;
        Assert.Empty(queue.Rows);

        parts.Queue.IsActive = true;
        Assert.Equal(3, queue.Rows.Count);

        queue.IsActive = false;
        parts.PlaylistControl.AddToQueue(Tracks("D"));
        Assert.Equal(3, queue.Rows.Count);

        parts.Queue.IsActive = true;
        Assert.Equal(new[] { "A", "B", "C", "D" }, Titles(queue));
    }

    [Fact]
    public void Playing_a_row_jumps_along_the_queue_rather_than_starting_a_new_one()
    {
        using var parts = Playing("A", "B", "C", "D");
        parts.Queue.IsActive = true;
        var queue = parts.Queue;

        Assert.True(queue.Play(queue.Rows[2]));

        Assert.Equal("C", parts.PlaylistControl.CurrentlyPlayingTrack?.Title);
        Assert.Equal(new[] { "A", "B", "C", "D" }, Titles(queue));
        Assert.Equal(2, queue.PlayedCount);
        Assert.Equal(4, parts.PlaylistControl.CurrentPlaylist.Tracks.Count);
    }

    // What has played stays, above the song playing and faded, so the queue
    // can be played again from its top - and a tap there goes back, along the
    // same queue, rather than starting a new one.
    [Fact]
    public void What_has_played_stays_above_and_plays_again_from_there()
    {
        using var parts = Playing("A", "B", "C", "D");
        parts.Queue.IsActive = true;
        var queue = parts.Queue;
        queue.Play(queue.Rows[2]);

        Assert.Equal(new[] { true, true, false, false }, queue.Rows.Select(r => r.IsPlayed));
        Assert.Equal(new[] { false, false, true, false }, queue.Rows.Select(r => r.IsCurrentlyPlaying));
        Assert.Equal(new[] { false, false, false, true }, queue.Rows.Select(r => r.CanBeDragged));
        Assert.Equal(2, queue.UpcomingCount);

        Assert.True(queue.Play(queue.Rows[0]));

        Assert.Equal("A", parts.PlaylistControl.CurrentlyPlayingTrack?.Title);
        Assert.Equal(0, queue.PlayedCount);
        Assert.Equal(new[] { "A", "B", "C", "D" }, Titles(queue));
        Assert.Equal(4, parts.PlaylistControl.CurrentPlaylist.Tracks.Count);
    }

    [Fact]
    public void A_selection_comes_out_of_the_queue_but_never_the_song_playing()
    {
        using var parts = Playing("A", "B", "C", "D");
        parts.Queue.IsActive = true;
        var queue = parts.Queue;

        Assert.False(queue.CanRemove(queue.Rows[0]));
        queue.Remove([queue.Rows[0], queue.Rows[1], queue.Rows[3]]);

        Assert.Equal(new[] { "A", "C" }, Titles(queue));
        Assert.Equal("A", parts.PlaylistControl.CurrentlyPlayingTrack?.Title);
    }

    // A song queued twice is two rows, and the one dragged is the one moved.
    [Fact]
    public void Reordering_moves_the_requested_copy_of_a_repeated_song()
    {
        using var parts = Playing("A", "B", "C");
        parts.PlaylistControl.AddToQueue([parts.Library.Tracks[1]]);
        parts.Queue.IsActive = true;
        var queue = parts.Queue;
        Assert.Equal(new[] { "A", "B", "C", "B" }, Titles(queue));

        queue.Reorder(queue.Rows[3], queue.Rows[1]);

        Assert.Equal(new[] { "A", "B", "B", "C" }, Titles(queue));
    }
    // A tap on a row under shuffle is a jump along the rows as they are
    // shown: the song tapped stays where it was, what was above it is what
    // has played, and Next plays the row under it.
    [Fact]
    public void Playing_a_row_under_shuffle_leaves_the_rows_where_they_were()
    {
        using var parts = Playing("A", "B", "C", "D", "E", "F");
        parts.PlaylistControl.ToggleShuffle();
        var queue = parts.Queue;
        parts.Queue.IsActive = true;
        var before = Titles(queue);
        var tapped = queue.Rows[3];

        Assert.True(queue.Play(tapped));

        Assert.Equal(before, Titles(queue));
        Assert.Equal(3, queue.PlayedCount);
        Assert.Equal(before[3], parts.PlaylistControl.CurrentlyPlayingTrack?.Title);

        parts.PlaylistControl.Next();

        Assert.Equal(before[4], parts.PlaylistControl.CurrentlyPlayingTrack?.Title);
        Assert.Equal(before, Titles(queue));
    }
}
