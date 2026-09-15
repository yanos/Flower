using System.Collections.Generic;

using Flower.Models;

using Xunit;

namespace Flower.Tests;

// Library.PlaylistsToken is what a client polls to notice a playlist changed
// on another device, and ChangeToken is what makes it pull the whole catalog -
// so each has to move for its own changes and only those.
public class LibraryPlaylistsTokenTests
{
    private static Track T(string title) => new Track { Title = title, Path = $"/music/{title}.mp3" };

    [Fact]
    public void Creating_and_deleting_a_playlist_moves_the_playlists_token_but_not_the_catalog_token()
    {
        var library = new Library(new List<Track> { T("A") });
        var catalog = library.ChangeToken;
        var before = library.PlaylistsToken;

        var playlist = new Playlist("P", new List<Track>());
        library.AddPlaylist(playlist);
        var afterAdd = library.PlaylistsToken;
        library.RemovePlaylist(playlist);

        Assert.NotEqual(before, afterAdd);
        Assert.NotEqual(afterAdd, library.PlaylistsToken);
        Assert.Equal(catalog, library.ChangeToken);
    }

    [Fact]
    public void An_in_place_edit_moves_the_playlists_token()
    {
        var library = new Library(new List<Track> { T("A") });
        var playlist = new Playlist("P", new List<Track>());
        library.AddPlaylist(playlist);
        var before = library.PlaylistsToken;

        playlist.AppendTrack(T("A"));

        Assert.NotEqual(before, library.PlaylistsToken);
    }

    // What ends the exchange a client's own push sets off: the server is
    // handed back the set it already has, and must not report that as news.
    [Fact]
    public void Replacing_the_playlists_with_the_same_set_does_not_move_the_token()
    {
        var library = new Library(new List<Track>());
        library.AddPlaylist(new Playlist("P", new List<Track>()));
        var before = library.PlaylistsToken;

        library.ReplacePlaylists(new List<Playlist>(library.Playlists));

        Assert.Equal(before, library.PlaylistsToken);
    }
}
