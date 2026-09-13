using System.Collections.Generic;
using System.Linq;

using Flower.Models;
using Flower.Services;
using Flower.ViewModels;
using Flower.ViewModels.Mobile;

namespace Flower.Tests;

// The greyed-out treatment for an unreachable server, at the two levels it is
// asked about: a song (dim the row) and an album (dim the art and the tile) -
// where an album only counts as gone once *nothing* in it can be played, so a
// single downloaded track keeps the art beside it at full strength.
public class AlbumGroupAvailabilityTests
{
    private const string Server = "server-fingerprint";

    private static Track Placeholder(string title, string album) => new()
    {
        Title = title,
        Album = album,
        Artists = "A",
        Path = null,
        OriginDeviceFingerprint = Server,
    };

    private static Track Downloaded(string title, string album) => new()
    {
        Title = title,
        Album = album,
        Artists = "A",
        Path = $"/music/{album}/{title}.mp3",
    };

    private static List<TrackRowViewModel> Rows(IEnumerable<Track> tracks, bool reachable) =>
        TrackListBuilder.Build(tracks, null, "Title", true,
            pairedServerFingerprint: Server, pairedServerReachable: reachable);

    [Fact]
    public void An_album_of_placeholders_dims_every_row_and_its_art_when_the_server_is_gone()
    {
        var rows = Rows([Placeholder("One", "Gone"), Placeholder("Two", "Gone")], reachable: false);

        Assert.All(rows, row => Assert.True(row.IsUnavailable));
        Assert.All(rows, row => Assert.True(row.IsAlbumGroupUnavailable));
    }

    [Fact]
    public void One_downloaded_track_keeps_the_art_lit_while_its_own_row_still_dims()
    {
        var rows = Rows([Placeholder("Aaa", "Mixed"), Downloaded("Bbb", "Mixed")], reachable: false);

        Assert.True(rows[0].IsUnavailable);
        Assert.False(rows[1].IsUnavailable);
        // Both rows belong to one album run, and the art cell spans it - so
        // neither may claim the album is gone.
        Assert.All(rows, row => Assert.False(row.IsAlbumGroupUnavailable));
    }

    [Fact]
    public void Nothing_dims_while_the_server_is_reachable()
    {
        var rows = Rows([Placeholder("One", "Streamable"), Placeholder("Two", "Streamable")], reachable: true);

        Assert.All(rows, row => Assert.False(row.IsUnavailable));
        Assert.All(rows, row => Assert.False(row.IsAlbumGroupUnavailable));
    }

    // Only one album's run should react - the run boundaries have to be
    // respected, not collapsed into one whole-list answer.
    [Fact]
    public void Only_the_album_that_lost_its_tracks_dims_its_art()
    {
        var rows = Rows(
            [Placeholder("Aaa", "Absent"), Placeholder("Bbb", "Absent"), Downloaded("Ccc", "Present")],
            reachable: false);

        Assert.True(rows[0].IsAlbumGroupUnavailable);
        Assert.True(rows[1].IsAlbumGroupUnavailable);
        Assert.False(rows[2].IsAlbumGroupUnavailable);
    }

    // The live path: rows built while the server was up are re-marked in place
    // when it goes away, rather than waiting for a rebuild that may never come.
    [Fact]
    public void Apply_re_marks_rows_and_their_album_art_when_reachability_flips()
    {
        var rows = Rows([Placeholder("One", "Gone"), Placeholder("Two", "Gone")], reachable: true);
        Assert.All(rows, row => Assert.False(row.IsAlbumGroupUnavailable));

        TrackAvailability.Apply(rows, Server, pairedServerReachable: false);
        Assert.All(rows, row => Assert.True(row.IsUnavailable));
        Assert.All(rows, row => Assert.True(row.IsAlbumGroupUnavailable));

        TrackAvailability.Apply(rows, Server, pairedServerReachable: true);
        Assert.All(rows, row => Assert.False(row.IsUnavailable));
        Assert.All(rows, row => Assert.False(row.IsAlbumGroupUnavailable));
    }

    [Fact]
    public void Apply_leaves_a_mixed_albums_art_lit_when_the_server_goes_away()
    {
        var rows = Rows([Placeholder("Aaa", "Mixed"), Downloaded("Bbb", "Mixed")], reachable: true);

        TrackAvailability.Apply(rows, Server, pairedServerReachable: false);

        Assert.True(rows[0].IsUnavailable);
        Assert.All(rows, row => Assert.False(row.IsAlbumGroupUnavailable));
    }

    // The grids' own tiles, marked from the tracks each tile carries.
    [Fact]
    public void Album_tiles_dim_only_when_the_whole_album_is_out_of_reach()
    {
        var tiles = AlbumGridBuilder.Build([
            Placeholder("One", "Gone"),
            Placeholder("Two", "Gone"),
            Placeholder("Three", "Mixed"),
            Downloaded("Four", "Mixed"),
        ]);

        TrackAvailability.Apply(tiles, Server, pairedServerReachable: false);

        Assert.True(Tile(tiles, "Gone").IsUnavailable);
        Assert.False(Tile(tiles, "Mixed").IsUnavailable);

        TrackAvailability.Apply(tiles, Server, pairedServerReachable: true);
        Assert.All(tiles, tile => Assert.False(tile.IsUnavailable));
    }

    [Fact]
    public void Recently_added_tiles_carry_their_tracks_too()
    {
        var tiles = RecentlyAddedAlbumsBuilder.Build([Placeholder("One", "Gone"), Placeholder("Two", "Gone")]);

        TrackAvailability.Apply(tiles, Server, pairedServerReachable: false);

        Assert.True(Assert.Single(tiles).IsUnavailable);
    }

    // The tile's download icon asks the opposite question to its dimming: not
    // "can any of this be played" but "is any of it still worth fetching".
    [Fact]
    public void Album_tiles_offer_a_download_while_any_track_is_still_a_placeholder()
    {
        var tiles = AlbumGridBuilder.Build([
            Placeholder("One", "Mixed"),
            Downloaded("Two", "Mixed"),
            Downloaded("Three", "Local"),
        ]);

        TrackAvailability.Apply(tiles, Server, pairedServerReachable: true);

        Assert.True(Tile(tiles, "Mixed").IsDownloadable);
        Assert.False(Tile(tiles, "Local").IsDownloadable);

        // Nothing to download from a server that isn't there.
        TrackAvailability.Apply(tiles, Server, pairedServerReachable: false);
        Assert.All(tiles, tile => Assert.False(tile.IsDownloadable));
    }

    private static AlbumTileViewModel Tile(IEnumerable<AlbumTileViewModel> tiles, string name) =>
        tiles.Single(t => t.Name == name);
}
