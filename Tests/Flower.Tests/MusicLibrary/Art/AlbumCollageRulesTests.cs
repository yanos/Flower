using Flower.Models;
using Flower.Services;
using Flower.ViewModels;

namespace Flower.Tests.MusicLibrary.Art;

public class AlbumCollageRulesTests
{
    // A playlist can hold two albums with one name - they are two albums,
    // told apart by who made them, where an artist's own are grouped by name.
    [Fact]
    public void A_playlist_tells_same_named_albums_apart_by_their_artist()
    {
        List<Track> tracks =
        [
            new() { Title = "a", Album = "Greatest Hits", Artists = "Queen", PlayCount = 2 },
            new() { Title = "b", Album = "Greatest Hits", Artists = "ABBA", PlayCount = 1 },
        ];

        Assert.Equal(["Queen", "ABBA"], AlbumCollage.ByAlbumAndArtist(tracks).AlbumsInPlayOrder.Select(t => t.Artists));
        Assert.Single(AlbumCollage.ForArtist("Artist", tracks, thumbnail: false).AlbumsInPlayOrder);
    }

    // A song added to a playlist can change which albums are its most played,
    // so the row's cover is rebuilt along with what the row says.
    [Fact]
    public void A_playlists_cover_follows_its_songs()
    {
        var playlist = new Playlist("Mix", [new Track { Title = "a", Album = "One", Artists = "X" }]);
        var row = new SidebarItem(SidebarItemKind.Playlist, "Mix", playlist: playlist);
        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.Equal(["One"], row.PlaylistCover.AlbumsInPlayOrder.Select(t => t.Album));

        playlist.InsertTrack(1, new Track { Title = "b", Album = "Two", Artists = "X", PlayCount = 5 });
        row.NotifyPlaylistSummaryChanged();

        Assert.Contains(nameof(SidebarItem.PlaylistCover), raised);
        Assert.Equal(["Two", "One"], row.PlaylistCover.AlbumsInPlayOrder.Select(t => t.Album));
    }
}
