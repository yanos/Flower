using System.Collections.Generic;
using System.Linq;

using Flower.Models;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests.ViewModels;

// Where a playlist dragged over a sidebar row lands (PlaylistDropPlacement):
// beside a playlist by which half of its row, beside or into a folder by
// which part of its row, and never into itself.
public class PlaylistDropPlacementTests
{
    private readonly Playlist _rock = Playlist.NewFolder("Rock");
    private readonly Playlist _live;
    private readonly Playlist _a = new("A", []);
    private readonly Playlist _b = new("B", []);
    private readonly List<Playlist> _all;

    public PlaylistDropPlacementTests()
    {
        _live = new Playlist("Live", []) { ParentId = _rock.Id };
        _all = [_a, _rock, _live, _b];
    }

    [Theory]
    [InlineData(0.2, PlaylistDropEdge.Above, 0)]
    [InlineData(0.8, PlaylistDropEdge.Below, 1)]
    public void Over_a_playlist_it_goes_before_or_after_it(double fraction, PlaylistDropEdge edge, int index)
    {
        var drop = PlaylistDropPlacement.For(_b, _a, overIsExpanded: false, fraction, _all);

        Assert.Equal(new PlaylistDrop(null, index, _a, edge), drop);
    }

    [Fact]
    public void The_middle_of_a_folder_is_into_it()
    {
        var drop = PlaylistDropPlacement.For(_a, _rock, overIsExpanded: true, 0.5, _all);

        Assert.Equal(PlaylistDropEdge.Into, drop?.Edge);
        Assert.Equal(_rock, drop?.Folder);
    }

    // The line under an open folder sits over its first playlist, so that is
    // where the drop goes.
    [Fact]
    public void Just_under_an_open_folder_is_the_first_place_inside_it()
    {
        Assert.Equal(new PlaylistDrop(_rock, 0, _rock, PlaylistDropEdge.Below),
            PlaylistDropPlacement.For(_a, _rock, overIsExpanded: true, 0.9, _all));
        Assert.Equal(new PlaylistDrop(null, 1, _rock, PlaylistDropEdge.Below),
            PlaylistDropPlacement.For(_a, _rock, overIsExpanded: false, 0.9, _all));
    }

    // Counted without the playlist being dragged: A is first, so below Rock
    // is the second place once A has left the first.
    [Fact]
    public void Beside_a_playlist_inside_a_folder_is_in_that_folder()
    {
        var drop = PlaylistDropPlacement.For(_b, _live, overIsExpanded: false, 0.8, _all);

        Assert.Equal(new PlaylistDrop(_rock, 1, _live, PlaylistDropEdge.Below), drop);
    }

    [Fact]
    public void Not_onto_itself_nor_into_a_folder_inside_it()
    {
        var inner = Playlist.NewFolder("Inner", _rock.Id);
        _all.Add(inner);

        Assert.Null(PlaylistDropPlacement.For(_a, _a, overIsExpanded: false, 0.5, _all));
        Assert.Null(PlaylistDropPlacement.For(_rock, inner, overIsExpanded: false, 0.5, _all));
        Assert.Null(PlaylistDropPlacement.For(_rock, _live, overIsExpanded: false, 0.2, _all));
    }
}
