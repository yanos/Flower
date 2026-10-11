using System.Linq;

using Flower.Models;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests.ViewModels;

// A track row prints the artist under the title, which inside one album meant
// the same name repeated down the whole screen. The list decides
// (TrackRowViewModel.ArtistIsWorthShowing, asked by ITrackRowHost) rather than
// the row, because "the same for this whole view" is a property of the list,
// not of a track.
public class TrackRowArtistTests
{
    private static TrackRowViewModel[] Rows(params string[] artists) =>
        artists.Select((a, i) => new TrackRowViewModel { Track = new Track { Title = "Song " + i, Artists = a } }).ToArray();

    [Fact]
    public void One_artist_across_the_list_drops_the_line_from_every_row() =>
        Assert.False(TrackRowViewModel.ArtistIsWorthShowing(Rows("Fugazi", "Fugazi", "Fugazi")));

    [Fact]
    public void A_compilation_keeps_the_artist_on_each_row() =>
        Assert.True(TrackRowViewModel.ArtistIsWorthShowing(Rows("Fugazi", "Slint", "Unwound")));

    // One row is not a repetition, and in a list with no album header above it
    // that line is the only place the artist is named at all.
    [Fact]
    public void A_single_row_keeps_its_artist() =>
        Assert.True(TrackRowViewModel.ArtistIsWorthShowing(Rows("Fugazi")));
}
