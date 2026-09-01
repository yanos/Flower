using System.Collections.Generic;
using System.Linq;

using Flower.Models;
using Flower.Services;

using Xunit;

namespace Flower.Tests;

// A client's plays of its server's tracks, reported back so the server - and
// through it every other listener - can see them.
//
// The server never pulls from a client (see SyncEndpoints), so this push is
// the only route a play made on a paired desktop or phone has off that device.
// Without it the count moved locally, showed up in that one app's Plays
// column, and was invisible everywhere else forever. The receiving half is
// covered end to end in Flower.Server.Tests' SyncEndpointTests; what is
// decided here is which counts are this device's to state at all.
public class PlayCountReportingTests
{
    private static Track Played(string title, string? originTrackId, int playCount, int importedPlayCount = 0) =>
        new()
        {
            Title = title,
            OriginTrackId = originTrackId,
            PlayCount = playCount,
            ImportedPlayCount = importedPlayCount,
        };

    [Fact]
    public void A_played_track_the_server_lent_this_device_is_reported()
    {
        var counts = LibrarySyncService.UnreportedPlayCounts(
            [Played("Second Song", "server-track-7", 3)],
            new Dictionary<string, int>());

        var reported = Assert.Single(counts);
        // The server's own id for the track, not this device's Guid - which is
        // minted locally and means nothing there. See Track.OriginTrackId.
        Assert.Equal("server-track-7", reported.TrackId);
        Assert.Equal(3, reported.Count);
    }

    // A file this device imported itself that no server has. Reporting it
    // would be naming a track the far side cannot resolve.
    [Fact]
    public void A_track_with_no_origin_id_is_not_reported()
    {
        var counts = LibrarySyncService.UnreportedPlayCounts(
            [Played("A Local Recording", originTrackId: null, playCount: 12)],
            new Dictionary<string, int>());

        Assert.Empty(counts);
    }

    // Never played here. Sending a zero says nothing the far side does not
    // already assume, and a library is mostly zeroes.
    [Fact]
    public void A_track_this_device_has_never_played_is_not_reported()
    {
        var counts = LibrarySyncService.UnreportedPlayCounts(
            [Played("Unheard", "server-track-1", 0)],
            new Dictionary<string, int>());

        Assert.Empty(counts);
    }

    // The same sum the manifest already sends as this device's own tally - a
    // play imported from iTunes is still a play this device is the record of.
    [Fact]
    public void Imported_plays_count_towards_this_devices_total()
    {
        var counts = LibrarySyncService.UnreportedPlayCounts(
            [Played("Love Song", "server-track-2", playCount: 2, importedPlayCount: 40)],
            new Dictionary<string, int>());

        Assert.Equal(42, Assert.Single(counts).Count);
    }

    // The five-second tick this rides on would otherwise re-state every played
    // track in the library forever.
    [Fact]
    public void A_total_this_peer_has_already_been_told_is_not_sent_again()
    {
        var counts = LibrarySyncService.UnreportedPlayCounts(
            [Played("Alpha Song", "server-track-3", 5)],
            new Dictionary<string, int> { ["server-track-3"] = 5 });

        Assert.Empty(counts);
    }

    [Fact]
    public void A_further_play_of_an_already_reported_track_is_sent()
    {
        var counts = LibrarySyncService.UnreportedPlayCounts(
            [Played("Alpha Song", "server-track-3", 6)],
            new Dictionary<string, int> { ["server-track-3"] = 5 });

        // The new total, not the difference: the far side merges by max, which
        // is what makes a lost response cost nothing. See PlayCountReportDto.
        Assert.Equal(6, Assert.Single(counts).Count);
    }

    [Fact]
    public void Only_the_tracks_that_moved_are_sent()
    {
        var counts = LibrarySyncService.UnreportedPlayCounts(
            [
                Played("Alpha Song", "server-track-3", 5),
                Played("Beta Song", "server-track-4", 9),
                Played("Second Song", "server-track-5", 1),
            ],
            new Dictionary<string, int> { ["server-track-3"] = 5, ["server-track-4"] = 2 });

        Assert.Equal(["server-track-4", "server-track-5"], counts.Select(c => c.TrackId).Order());
    }
}
