using System.Collections.Generic;
using System.Linq;
using Flower.Models;

namespace Flower.Tests;

public class PlaylistTests
{
    private static Track T(string title) => new Track { Title = title, Path = $"/music/{title}.mp3" };

    [Fact]
    public void AppendTrack_adds_to_the_end()
    {
        var playlist = new Playlist("My Mix", new List<Track> { T("A"), T("B") });
        playlist.AppendTrack(T("C"));

        Assert.Equal(new[] { "A", "B", "C" }, playlist.Tracks.Select(t => t.Title));
    }

    [Fact]
    public void InsertTrack_inserts_at_given_index()
    {
        var playlist = new Playlist("My Mix", new List<Track> { T("A"), T("C") });
        playlist.InsertTrack(1, T("B"));

        Assert.Equal(new[] { "A", "B", "C" }, playlist.Tracks.Select(t => t.Title));
    }

    [Fact]
    public void RemoveTrack_removes_the_given_track()
    {
        var b = T("B");
        var playlist = new Playlist("My Mix", new List<Track> { T("A"), b, T("C") });
        playlist.RemoveTrack(b);

        Assert.Equal(new[] { "A", "C" }, playlist.Tracks.Select(t => t.Title));
    }

    [Fact]
    public void ReplaceAll_clears_and_replaces_contents()
    {
        var playlist = new Playlist("My Mix", new List<Track> { T("A"), T("B") });
        playlist.ReplaceAll(new List<Track> { T("X"), T("Y"), T("Z") });

        Assert.Equal(new[] { "X", "Y", "Z" }, playlist.Tracks.Select(t => t.Title));
    }

    [Fact]
    public void GetTrack_returns_null_when_index_out_of_range()
    {
        var playlist = new Playlist("My Mix", new List<Track> { T("A") });

        Assert.Null(playlist.GetTrack(5));
        Assert.NotNull(playlist.GetTrack(0));
    }

    [Fact]
    public void GetNextTrack_returns_the_following_track()
    {
        var a = T("A");
        var b = T("B");
        var c = T("C");
        var playlist = new Playlist("My Mix", new List<Track> { a, b, c });

        Assert.Same(c, playlist.GetNextTrack(b));
    }

    [Fact]
    public void GetNextTrack_wraps_to_first_track_after_the_last()
    {
        var a = T("A");
        var b = T("B");
        var playlist = new Playlist("My Mix", new List<Track> { a, b });

        Assert.Same(a, playlist.GetNextTrack(b));
    }

    [Fact]
    public void GetNextTrack_for_a_track_not_in_the_playlist_returns_the_first_track()
    {
        var a = T("A");
        var playlist = new Playlist("My Mix", new List<Track> { a, T("B") });

        Assert.Same(a, playlist.GetNextTrack(T("Not In Playlist")));
    }

    [Fact]
    public void GetPreviousTrack_returns_the_preceding_track()
    {
        var a = T("A");
        var b = T("B");
        var c = T("C");
        var playlist = new Playlist("My Mix", new List<Track> { a, b, c });

        Assert.Same(b, playlist.GetPreviousTrack(c));
    }

    [Fact]
    public void GetPreviousTrack_at_the_start_stays_on_the_first_track()
    {
        var a = T("A");
        var playlist = new Playlist("My Mix", new List<Track> { a, T("B") });

        // Existing behavior: index-1 underflows to -1, which falls back to
        // FirstOrDefault() rather than wrapping to the last track.
        Assert.Same(a, playlist.GetPreviousTrack(a));
    }
}
