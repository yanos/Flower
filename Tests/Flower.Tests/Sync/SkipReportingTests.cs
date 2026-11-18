using System.Collections.Generic;

using Flower.Models;
using Flower.Services;

using Xunit;

namespace Flower.Tests.Sync;

// Skips (Track.SkipCount) travel the roads plays do, by the same rules: a
// device states its own tally and the server files it under that device and
// keeps the highest; Music.app's count is owner state. TrackStateReportingTests
// covers the plays.
public class SkipReportingTests
{
    private static readonly Dictionary<string, LibrarySyncService.TrackStateSnapshot> NoServerState = new();

    private static Track Skipped(int skips, int plays = 0) =>
        new() { Title = "Song", OriginTrackId = "server-track-7", SkipCount = skips, PlayCount = plays };

    [Fact]
    public void A_track_only_ever_skipped_here_is_reported()
    {
        var report = Assert.Single(LibrarySyncService.UnreportedTrackState(
            [Skipped(2)], new Dictionary<string, int>(), NoServerState, includeOwnerState: false));

        Assert.Equal(2, report.SkipCount);
        Assert.Equal(0, report.Count);
    }

    [Fact]
    public void A_skip_total_this_peer_has_already_been_told_is_not_sent_again()
    {
        Assert.Empty(LibrarySyncService.UnreportedTrackState(
            [Skipped(2)], new Dictionary<string, int>(), NoServerState, includeOwnerState: false,
            sentSkipCounts: new Dictionary<string, int> { ["server-track-7"] = 2 }));
    }

    // Any paired device may state its own skips, as its own plays: they are
    // filed under its name and add to nobody else's.
    [Fact]
    public void A_server_files_a_guests_skips_under_that_device_and_keeps_the_highest()
    {
        var song = new Track { Title = "Song", Path = "/music/song.mp3" };
        var server = new Library([song]);

        server.MergeReportedTrackState("guest-phone", [new TrackStateDto(song.Id.ToKey(), 0, SkipCount: 3)], callerIsAdmin: false);
        server.MergeReportedTrackState("guest-phone", [new TrackStateDto(song.Id.ToKey(), 0, SkipCount: 1)], callerIsAdmin: false);

        Assert.Equal(3, song.RemoteSkipCounts["guest-phone"]);
        Assert.Equal(0, song.SkipCount);
        Assert.Equal(3, song.TotalSkipCount);
    }

    // Music.app's count is the library's one number, an owner's to state.
    [Fact]
    public void Only_an_admin_device_moves_the_imported_skip_count()
    {
        var song = new Track { Title = "Song", Path = "/music/song.mp3" };
        var server = new Library([song]);

        server.MergeReportedTrackState("guest-phone", [new TrackStateDto(song.Id.ToKey(), 0, ImportedSkipCount: 9)], callerIsAdmin: false);
        Assert.Equal(0, song.ImportedSkipCount);

        server.MergeReportedTrackState("owner-mac", [new TrackStateDto(song.Id.ToKey(), 0, ImportedSkipCount: 9)], callerIsAdmin: true);
        Assert.Equal(9, song.ImportedSkipCount);
    }

    // A browser tab has no tally of its own to state, so it reports each skip
    // as it happens: an owner's moves the library's own count, a guest's is
    // filed under the guest.
    [Fact]
    public void A_reported_skip_moves_the_owners_count_or_the_guests_own()
    {
        var song = new Track { Title = "Song", Path = "/music/song.mp3" };
        var server = new Library([song]);

        Assert.True(server.RecordSkip(song.Id.ToKey()));
        Assert.True(server.RecordSkipFor(song.Id.ToKey(), "guest-tab"));

        Assert.Equal(1, song.SkipCount);
        Assert.Equal(1, song.RemoteSkipCounts["guest-tab"]);
        Assert.Equal(0, song.PlayCount);
        Assert.Null(song.LastPlayedAt);
    }

    // The catalog carries every device's skips under its name, and a device
    // pulling it takes everyone's but its own, which it keeps itself.
    [Fact]
    public void A_pulled_catalog_brings_other_devices_skips_and_not_this_ones()
    {
        var song = new Track { Title = "Song", Path = "/music/song.mp3", SkipCount = 4, ImportedSkipCount = 6 };
        song.RemoteSkipCounts["this-phone"] = 1;
        song.RemoteSkipCounts["other-phone"] = 2;

        var dto = LibraryDtoMapper.ToTrackDto(song, selfFingerprint: "server");
        var pulled = LibrarySyncMapper.ToPlaceholderTrack(dto, "server", "this-phone");

        Assert.Equal(4, pulled.RemoteSkipCounts["server"]);
        Assert.Equal(2, pulled.RemoteSkipCounts["other-phone"]);
        Assert.False(pulled.RemoteSkipCounts.ContainsKey("this-phone"));
        Assert.Equal(6, pulled.ImportedSkipCount);
    }
}
