using System;
using System.Collections.Generic;

using Flower.Models;
using Flower.Services;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests.Playlists;

// /info's playlistsToken is what tells a device that stayed open to come back
// for its playlists, so it has to move whenever GET /playlists would answer
// differently for that listener.
public class PlaylistRevisionsTokenTests
{
    private static Track T(string title) => new() { Title = title, Path = $"/music/{title}.mp3" };

    private static (Library Library, PlaylistRevisions Revisions) Server(params Playlist[] playlists)
    {
        var library = new Library(new List<Track>());
        library.ReplacePlaylists(new List<Playlist>(playlists));
        var revisions = new PlaylistRevisions(library, new InMemoryPlaylistRevisionStore());
        revisions.Start();
        return (library, revisions);
    }

    // A sync installs another device's copy at whatever UpdatedAt it carries,
    // which can be the one already here: a new revision all the same, and
    // the devices that hold the old one have to hear about it.
    [Fact]
    public void A_copy_installed_at_the_same_UpdatedAt_moves_the_token()
    {
        var at = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var (library, revisions) = Server(new Playlist(id, "Old name", [], at));
        var before = revisions.TokenFor(Listeners.Owner);

        library.ReplacePlaylists([new Playlist(id, "New name", [], at)]);

        Assert.NotEqual(before, revisions.TokenFor(Listeners.Owner));
    }

    // The server rebuilds a pool without moving UpdatedAt or issuing a
    // revision, so the token has to move with the songs or a device never
    // comes back for them.
    [Fact]
    public void The_token_moves_when_a_vibe_playlists_pool_does()
    {
        var playlist = new Playlist("Rainy", [T("A")]) { VibePrompt = "music for a rainy day" };
        var (_, revisions) = Server(playlist);
        var before = revisions.TokenFor(Listeners.Owner);

        playlist.Materialize([T("B")]);

        Assert.NotEqual(before, revisions.TokenFor(Listeners.Owner));
    }
}
