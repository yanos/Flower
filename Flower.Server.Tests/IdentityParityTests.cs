using Flower.Models;
using Flower.Server.Services;
using Flower.Services;

namespace Flower.Server.Tests;

// ARCHITECTURE-REVIEW Tier 2.1: the standalone server and the embedded
// peer-to-peer host used to compute album/artist ids two different ways
// (differing by a punctuation character and an argument order) and to round
// durations two different ways, with nothing enforcing either agreement.
// These pin the single shared implementation from the server's side; the
// client's half is in Flower.Tests' LibraryOpenSubsonicMapperTests (AlbumIdFor
// is SubsonicIdentity.AlbumId over EffectiveAlbumArtist). The two projects
// meet at Flower.Core's SubsonicIdentity rather than at each other, which is
// the point - the embedded host lives in the Avalonia-referencing Flower
// project this one deliberately does not depend on.
public class IdentityParityTests
{
    // Flower.Core's Track, not a server-private entity: the server now maps
    // straight off the shared model (see LibraryQueries), which is one fewer
    // place for the two sides to disagree about what a track even is.
    private static Track Song(string artist, string album, double durationSeconds) => new()
    {
        Path = "/music/song.mp3",
        Title = "Song",
        Artists = artist,
        AlbumArtists = artist,
        Album = album,
        Duration = TimeSpan.FromSeconds(durationSeconds),
    };

    [Fact]
    public void The_servers_song_duration_rounds_the_same_way_the_clients_does()
    {
        // 369.888s: the exact real-device case that made a peer's stream
        // request carry a SyncKey the serving device could never match, back
        // when one side truncated and the other rounded (see
        // Track.RoundedSeconds).
        var child = SubsonicMapper.ToChild(Song("Angine de Poitrine", "Vol.II", 369.888));

        Assert.Equal(370, child.Duration);
        Assert.Equal(Track.RoundedSeconds(369.888), child.Duration);
    }

    [Theory]
    [InlineData(200.4, 200)]
    [InlineData(200.6, 201)]
    // Math.Round's default is banker's rounding, so an exact .5 goes to the
    // even value rather than up. Asserted rather than corrected: what matters
    // is that both sides do the same thing, and they do it by calling the
    // same method.
    [InlineData(200.5, 200)]
    [InlineData(201.5, 202)]
    public void Rounding_is_round_not_truncate_on_both_sides(double seconds, int expected)
    {
        Assert.Equal(expected, SubsonicMapper.ToChild(Song("A", "B", seconds)).Duration);
        Assert.Equal(expected, Track.RoundedSeconds(seconds));
    }
}
