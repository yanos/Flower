using Flower.Models;
using Flower.Services;

namespace Flower.Tests;

public class TrackAvailabilityTests
{
    private static Track Placeholder(string? originDeviceFingerprint) => new()
    {
        Title = "Placeholder",
        Path = null,
        OriginDeviceFingerprint = originDeviceFingerprint,
    };

    private static Track Downloaded(string? originDeviceFingerprint) => new()
    {
        Title = "Downloaded",
        Path = "/music/downloaded.mp3",
        OriginDeviceFingerprint = originDeviceFingerprint,
    };

    [Fact]
    public void IsAvailable_is_true_for_a_placeholder_from_the_reachable_paired_server()
    {
        Assert.True(TrackAvailability.IsAvailable(Placeholder("abc"), pairedServerFingerprint: "abc", pairedServerReachable: true));
    }

    [Fact]
    public void IsAvailable_is_false_when_not_paired_with_any_server()
    {
        Assert.False(TrackAvailability.IsAvailable(Placeholder("abc"), pairedServerFingerprint: null, pairedServerReachable: false));
    }

    [Fact]
    public void IsAvailable_is_false_when_the_paired_server_is_unreachable()
    {
        Assert.False(TrackAvailability.IsAvailable(Placeholder("abc"), pairedServerFingerprint: "abc", pairedServerReachable: false));
    }

    [Fact]
    public void IsAvailable_is_false_for_a_stale_origin_fingerprint_from_a_prior_pairing()
    {
        Assert.False(TrackAvailability.IsAvailable(Placeholder("old-server"), pairedServerFingerprint: "new-server", pairedServerReachable: true));
    }

    [Fact]
    public void IsAvailable_is_false_for_an_already_downloaded_track()
    {
        Assert.False(TrackAvailability.IsAvailable(Downloaded("abc"), pairedServerFingerprint: "abc", pairedServerReachable: true));
    }

    // IsPlayable is the union IsAvailable deliberately isn't: a downloaded
    // track is not "available to download", but it is certainly playable, and
    // that is the question the dimmed/greyed-out visuals actually ask.
    [Fact]
    public void IsPlayable_is_true_for_a_downloaded_track_with_no_server_at_all()
    {
        Assert.True(TrackAvailability.IsPlayable(Downloaded("abc"), pairedServerFingerprint: null, pairedServerReachable: false));
    }

    [Fact]
    public void IsPlayable_is_false_for_a_placeholder_whose_server_is_unreachable()
    {
        Assert.False(TrackAvailability.IsPlayable(Placeholder("abc"), pairedServerFingerprint: "abc", pairedServerReachable: false));
    }

    [Fact]
    public void An_album_of_only_unreachable_placeholders_is_unavailable()
    {
        Track[] album = [Placeholder("abc"), Placeholder("abc")];
        Assert.True(TrackAvailability.IsAlbumUnavailable(album, pairedServerFingerprint: "abc", pairedServerReachable: false));
    }

    // The rule the user asked for: one downloaded track keeps the whole album
    // at full strength, because tapping it still lands on something that plays.
    [Fact]
    public void An_album_with_one_downloaded_track_stays_available_with_the_server_down()
    {
        Track[] album = [Placeholder("abc"), Downloaded("abc"), Placeholder("abc")];
        Assert.False(TrackAvailability.IsAlbumUnavailable(album, pairedServerFingerprint: "abc", pairedServerReachable: false));
    }

    [Fact]
    public void An_album_of_placeholders_is_available_while_the_server_is_reachable()
    {
        Track[] album = [Placeholder("abc"), Placeholder("abc")];
        Assert.False(TrackAvailability.IsAlbumUnavailable(album, pairedServerFingerprint: "abc", pairedServerReachable: true));
    }

    // Nothing there is not the same as nothing playable - an empty tile has no
    // songs to grey out, and dimming it would just look like a bug.
    [Fact]
    public void An_album_with_no_tracks_is_not_unavailable()
    {
        Assert.False(TrackAvailability.IsAlbumUnavailable([], pairedServerFingerprint: "abc", pairedServerReachable: false));
    }
}
