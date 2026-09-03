using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;
using Flower.Services;

using Xunit;

namespace Flower.Tests;

// What a client knows about its server's tracks, reported back so the server -
// and through it every other listener - can see it.
//
// The server never pulls from a client (see SyncEndpoints), so this push is
// the only route a play, a star or a resume position made on a paired desktop
// or phone has off that device. Without it the change happened locally, showed
// up in that one app, and was invisible everywhere else forever. The receiving
// half is covered end to end in Flower.Server.Tests' SyncEndpointTests; what is
// decided here is what this device has to say at all.
//
// The two halves are selected on different rules and the tests are grouped the
// same way. A count is a G-Counter merged by max, so "not yet told" means
// "higher than what was acknowledged". Owner state is applied as stated, so
// "not yet told" means "different from what the server itself last served" -
// and with no served baseline to compare against, nothing is said.
public class TrackStateReportingTests
{
    // A peer this session has pulled no catalog from yet.
    private static readonly Dictionary<string, LibrarySyncService.TrackStateSnapshot> NoServerState = new();

    private static Dictionary<string, LibrarySyncService.TrackStateSnapshot> ServerSaid(
        string originTrackId, Track track) =>
        new() { [originTrackId] = LibrarySyncService.TrackStateSnapshot.Of(track) };

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
        var counts = LibrarySyncService.UnreportedTrackState(
            [Played("Second Song", "server-track-7", 3)],
            new Dictionary<string, int>(),
            NoServerState, includeOwnerState: false);

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
        var counts = LibrarySyncService.UnreportedTrackState(
            [Played("A Local Recording", originTrackId: null, playCount: 12)],
            new Dictionary<string, int>(),
            NoServerState, includeOwnerState: false);

        Assert.Empty(counts);
    }

    // Never played here. Sending a zero says nothing the far side does not
    // already assume, and a library is mostly zeroes.
    [Fact]
    public void A_track_this_device_has_never_played_is_not_reported()
    {
        var counts = LibrarySyncService.UnreportedTrackState(
            [Played("Unheard", "server-track-1", 0)],
            new Dictionary<string, int>(),
            NoServerState, includeOwnerState: false);

        Assert.Empty(counts);
    }

    // The same sum the manifest already sends as this device's own tally - a
    // play imported from iTunes is still a play this device is the record of.
    [Fact]
    public void Imported_plays_count_towards_this_devices_total()
    {
        var counts = LibrarySyncService.UnreportedTrackState(
            [Played("Love Song", "server-track-2", playCount: 2, importedPlayCount: 40)],
            new Dictionary<string, int>(),
            NoServerState, includeOwnerState: false);

        Assert.Equal(42, Assert.Single(counts).Count);
    }

    // The five-second tick this rides on would otherwise re-state every played
    // track in the library forever.
    [Fact]
    public void A_total_this_peer_has_already_been_told_is_not_sent_again()
    {
        var counts = LibrarySyncService.UnreportedTrackState(
            [Played("Alpha Song", "server-track-3", 5)],
            new Dictionary<string, int> { ["server-track-3"] = 5 },
            NoServerState, includeOwnerState: false);

        Assert.Empty(counts);
    }

    [Fact]
    public void A_further_play_of_an_already_reported_track_is_sent()
    {
        var counts = LibrarySyncService.UnreportedTrackState(
            [Played("Alpha Song", "server-track-3", 6)],
            new Dictionary<string, int> { ["server-track-3"] = 5 },
            NoServerState, includeOwnerState: false);

        // The new total, not the difference: the far side merges by max, which
        // is what makes a lost response cost nothing. See TrackStateDto.
        Assert.Equal(6, Assert.Single(counts).Count);
    }

    [Fact]
    public void Only_the_tracks_that_moved_are_sent()
    {
        var counts = LibrarySyncService.UnreportedTrackState(
            [
                Played("Alpha Song", "server-track-3", 5),
                Played("Beta Song", "server-track-4", 9),
                Played("Second Song", "server-track-5", 1),
            ],
            new Dictionary<string, int> { ["server-track-3"] = 5, ["server-track-4"] = 2 },
            NoServerState, includeOwnerState: false);

        Assert.Equal(["server-track-4", "server-track-5"], counts.Select(c => c.TrackId).Order());
    }

    // The count half of a report is unconditional; the owner-state half is not
    // sent at all by a device the server has not made an admin, so a
    // housemate's phone never even asks to restar the owner's library.
    [Fact]
    public void A_non_admin_device_reports_its_count_and_nothing_else()
    {
        var track = Played("Second Song", "server-track-7", 3);
        track.Starred = true;
        track.LastPlayedAt = DateTimeOffset.UtcNow;

        var report = LibrarySyncService.UnreportedTrackState(
            [track], new Dictionary<string, int>(),
            ServerSaid("server-track-7", Played("Second Song", "server-track-7", 0)),
            includeOwnerState: false);

        var reported = Assert.Single(report);
        Assert.Equal(3, reported.Count);
        Assert.False(reported.Starred);
        Assert.Null(reported.LastPlayedAt);
    }

    // The seed rule, and the reason a restart cannot undo somebody else's
    // star: with no pulled catalog to compare against there is no way to tell
    // "the user just changed this" from "this device simply holds a value", so
    // the owner state stays put until a sync has said what the server holds.
    [Fact]
    public void An_admin_device_says_nothing_about_a_track_it_has_no_served_baseline_for()
    {
        var track = Played("Second Song", "server-track-7", 0);
        track.Starred = true;

        var report = LibrarySyncService.UnreportedTrackState(
            [track], new Dictionary<string, int>(), NoServerState, includeOwnerState: true);

        Assert.Empty(report);
    }

    [Fact]
    public void An_admin_device_reports_a_star_the_server_does_not_have()
    {
        var track = Played("Second Song", "server-track-7", 0);
        track.Starred = true;
        track.StarredAt = DateTimeOffset.UtcNow;

        var report = LibrarySyncService.UnreportedTrackState(
            [track], new Dictionary<string, int>(),
            ServerSaid("server-track-7", Played("Second Song", "server-track-7", 0)),
            includeOwnerState: true);

        var reported = Assert.Single(report);
        Assert.True(reported.Starred);
        Assert.Equal(track.StarredAt, reported.StarredAt);
        // Nothing played here, and a zero says nothing the far side does not
        // already assume - but the field rides along rather than being omitted,
        // because a report carrying only the half that moved would need a way
        // to say "no opinion" about the other.
        Assert.Equal(0, reported.Count);
    }

    // Unstarring is the case a max-merge could never carry: it moves the value
    // down, and Library.SetStarred nulls StarredAt on the way, so there is no
    // timestamp left to order it by. Comparing against what the server served
    // is what makes it reportable at all.
    [Fact]
    public void An_admin_device_reports_clearing_a_star_the_server_holds()
    {
        var served = Played("Second Song", "server-track-7", 0);
        served.Starred = true;
        var track = Played("Second Song", "server-track-7", 0);

        var report = LibrarySyncService.UnreportedTrackState(
            [track], new Dictionary<string, int>(),
            ServerSaid("server-track-7", served), includeOwnerState: true);

        Assert.False(Assert.Single(report).Starred);
    }

    [Fact]
    public void An_admin_device_reports_a_listen_and_the_playback_options_that_came_with_it()
    {
        var track = Played("Second Song", "server-track-7", 1);
        track.LastPlayedAt = DateTimeOffset.UtcNow;
        track.RememberPlaybackPosition = true;
        track.ResumePosition = TimeSpan.FromSeconds(91.5);
        track.IgnoreWhenShuffling = true;
        track.VolumeAdjustment = -3;

        var report = LibrarySyncService.UnreportedTrackState(
            [track], new Dictionary<string, int> { ["server-track-7"] = 1 },
            ServerSaid("server-track-7", Played("Second Song", "server-track-7", 0)),
            includeOwnerState: true);

        var reported = Assert.Single(report);
        Assert.Equal(track.LastPlayedAt, reported.LastPlayedAt);
        Assert.True(reported.RememberPlaybackPosition);
        Assert.Equal(91.5, reported.ResumePositionSeconds);
        Assert.True(reported.IgnoreWhenShuffling);
        Assert.Equal(-3, reported.VolumeAdjustment);
    }

    // The steady state, and the whole point of keeping a baseline: an admin
    // client that agrees with its server about a track says nothing about it,
    // on every one of the twelve ticks a minute this runs on.
    [Fact]
    public void An_admin_device_says_nothing_about_a_track_it_agrees_with_the_server_on()
    {
        var track = Played("Second Song", "server-track-7", 4);
        track.Starred = true;
        track.LastPlayedAt = DateTimeOffset.UtcNow;

        var report = LibrarySyncService.UnreportedTrackState(
            [track], new Dictionary<string, int> { ["server-track-7"] = 4 },
            ServerSaid("server-track-7", track), includeOwnerState: true);

        Assert.Empty(report);
    }

    // The resend loop this rule exists to stop. A track the server has a
    // last-played for and this device has never played is a difference, and
    // used to be reported as one - which the server refuses (a null cannot
    // move a high-water mark forward), so the next catalog pull re-seeded the
    // baseline and the same report went out again, once per pull, forever.
    [Fact]
    public void An_admin_device_says_nothing_about_a_track_only_the_server_has_ever_played()
    {
        var served = Played("Second Song", "server-track-7", 0);
        served.LastPlayedAt = DateTimeOffset.UtcNow;

        var report = LibrarySyncService.UnreportedTrackState(
            [Played("Second Song", "server-track-7", 0)], new Dictionary<string, int>(),
            ServerSaid("server-track-7", served), includeOwnerState: true);

        Assert.Empty(report);
    }

    // Same rule one step along: this device did play it, but the server has
    // since heard about a later listen from somewhere else.
    [Fact]
    public void An_admin_device_says_nothing_about_a_listen_older_than_the_servers()
    {
        var served = Played("Second Song", "server-track-7", 0);
        served.LastPlayedAt = DateTimeOffset.UtcNow;

        var track = Played("Second Song", "server-track-7", 0);
        track.LastPlayedAt = DateTimeOffset.UtcNow.AddDays(-1);

        var report = LibrarySyncService.UnreportedTrackState(
            [track], new Dictionary<string, int>(),
            ServerSaid("server-track-7", served), includeOwnerState: true);

        Assert.Empty(report);
    }

    // A star still crosses on its own terms: it is applied as stated rather
    // than ordered by a timestamp, so an older listen alongside it must not
    // hold it back.
    [Fact]
    public void An_admin_device_reports_a_star_even_when_its_listen_is_the_older_one()
    {
        var served = Played("Second Song", "server-track-7", 0);
        served.LastPlayedAt = DateTimeOffset.UtcNow;

        var track = Played("Second Song", "server-track-7", 0);
        track.LastPlayedAt = DateTimeOffset.UtcNow.AddDays(-1);
        track.Starred = true;

        var report = LibrarySyncService.UnreportedTrackState(
            [track], new Dictionary<string, int>(),
            ServerSaid("server-track-7", served), includeOwnerState: true);

        Assert.True(Assert.Single(report).Starred);
    }

    // StarredAt is deliberately outside the comparison: it is set on starring
    // and nulled on unstarring, so two devices that agree on the star must not
    // be made to disagree forever over the second it was clicked at.
    [Fact]
    public void A_difference_only_in_when_the_star_was_set_is_not_worth_reporting()
    {
        var served = Played("Second Song", "server-track-7", 0);
        served.Starred = true;
        served.StarredAt = DateTimeOffset.UtcNow.AddDays(-30);

        var track = Played("Second Song", "server-track-7", 0);
        track.Starred = true;
        track.StarredAt = DateTimeOffset.UtcNow;

        var report = LibrarySyncService.UnreportedTrackState(
            [track], new Dictionary<string, int>(),
            ServerSaid("server-track-7", served), includeOwnerState: true);

        Assert.Empty(report);
    }
}
